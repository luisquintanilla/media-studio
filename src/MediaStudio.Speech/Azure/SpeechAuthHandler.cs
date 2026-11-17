using Azure.Core;
using MediaStudio.Configuration;

namespace MediaStudio.Speech.Azure;

public enum SpeechAuthMode
{
    /// <summary><c>Authorization: Bearer {token}</c> on the resource's custom domain.</summary>
    CustomDomain,
    /// <summary><c>Authorization: Bearer aad#{resourceId}#{token}</c> on <c>{region}.tts.speech.microsoft.com</c>.</summary>
    RegionalAad,
    /// <summary><c>Ocp-Apim-Subscription-Key</c> (api-key connections).</summary>
    ApiKey
}

/// <summary>
/// Adds Speech credentials to outgoing requests. Entra tokens (scope <see cref="FoundryEndpoints.CognitiveServicesScope"/>) are
/// cached until five minutes before expiry. Credentials are only attached for HTTPS requests to the configured Foundry
/// resource, a trusted Azure AI host, or the connection's regional Speech host; anything else is refused.
/// </summary>
public sealed class SpeechAuthHandler : DelegatingHandler
{
    public static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    private readonly FoundryEndpoints endpoints;
    private readonly TokenCredential? credential;
    private readonly string? apiKey;
    private readonly TimeProvider time;
    private readonly SemaphoreSlim gate = new(1, 1);
    private AccessToken? cached;

    public SpeechAuthHandler(FoundryEndpoints endpoints, SpeechAuthMode mode, TokenCredential? credential = null, string? apiKey = null,
        TimeProvider? timeProvider = null, HttpMessageHandler? innerHandler = null)
        : base(innerHandler ?? new HttpClientHandler())
    {
        this.endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
        Mode = mode;
        if (mode == SpeechAuthMode.ApiKey)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
            this.apiKey = apiKey;
        }
        else
        {
            this.credential = credential ?? throw new ArgumentNullException(nameof(credential), "Entra speech auth needs a TokenCredential.");
            if (mode == SpeechAuthMode.RegionalAad && endpoints.ResourceId is null)
                throw new InvalidOperationException("Regional Speech Entra auth needs the Foundry resource id (connections[].resourceId).");
        }
        time = timeProvider ?? TimeProvider.System;
    }

    public SpeechAuthMode Mode { get; }

    /// <summary>Creates the handler for a configured connection: api-key connections use the key, Entra connections a bearer token.</summary>
    public static SpeechAuthHandler For(ConnectionSettings connection, SpeechTtsMode ttsMode = FoundryEndpoints.DefaultSpeechTtsMode,
        HttpMessageHandler? innerHandler = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var endpoints = FoundryEndpoints.From(connection);
        return connection.Auth switch {
            AuthMode.ApiKey => new SpeechAuthHandler(endpoints, SpeechAuthMode.ApiKey, apiKey: CredentialFactory.GetApiKey(connection.Id), innerHandler: innerHandler),
            AuthMode.Entra => new SpeechAuthHandler(endpoints, ttsMode == SpeechTtsMode.RegionalAad ? SpeechAuthMode.RegionalAad : SpeechAuthMode.CustomDomain,
                CredentialFactory.CreateTokenCredential(connection), innerHandler: innerHandler),
            _ => throw new InvalidOperationException($"Connection '{connection.Id}' uses '{connection.Auth}' auth; Azure Speech needs entra or api-key.")
        };
    }

    public bool IsTrusted(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && (
            string.Equals(uri.Host, endpoints.Root.Host, StringComparison.OrdinalIgnoreCase)
            || FoundryEndpoints.IsTrustedHost(uri.Host)
            || endpoints.Region is { } region && string.Equals(uri.Host, $"{region}.tts.speech.microsoft.com", StringComparison.OrdinalIgnoreCase));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri ?? throw new InvalidOperationException("Speech request has no URI.");
        if (!IsTrusted(uri))
            throw new InvalidOperationException($"Refusing to send Speech credentials to untrusted host '{uri.Host}'. Only HTTPS Foundry/Speech hosts are allowed.");
        request.Headers.Authorization = null;
        request.Headers.Remove("Ocp-Apim-Subscription-Key");
        if (Mode == SpeechAuthMode.ApiKey)
            request.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Key", apiKey);
        else
        {
            var token = await GetTokenAsync(cancellationToken).ConfigureAwait(false);
            var value = endpoints.SpeechAuthorization(token, Mode == SpeechAuthMode.RegionalAad ? SpeechTtsMode.RegionalAad : SpeechTtsMode.CustomDomain);
            request.Headers.TryAddWithoutValidation("Authorization", value);
        }
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (cached is { } current && current.ExpiresOn - RefreshMargin > time.GetUtcNow()) return current.Token;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (cached is { } again && again.ExpiresOn - RefreshMargin > time.GetUtcNow()) return again.Token;
            var token = await credential!.GetTokenAsync(new TokenRequestContext([FoundryEndpoints.CognitiveServicesScope]), cancellationToken).ConfigureAwait(false);
            cached = token;
            return token.Token;
        }
        finally { gate.Release(); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) gate.Dispose();
        base.Dispose(disposing);
    }
}
