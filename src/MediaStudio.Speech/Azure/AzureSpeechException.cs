using System.Net;

namespace MediaStudio.Speech.Azure;

/// <summary>Raised when an Azure Speech call fails; the message is actionable and never contains credentials.</summary>
public sealed class AzureSpeechException(string message, HttpStatusCode? statusCode = null, Exception? inner = null)
    : Exception(message, inner)
{
    public const string AuthHelp = "docs/troubleshooting.md#speech-auth";
    public HttpStatusCode? StatusCode { get; } = statusCode;

    internal static async Task<AzureSpeechException> FromResponseAsync(HttpResponseMessage response, string operation, string? voice,
        CancellationToken cancellationToken)
    {
        var body = await SafeBodyAsync(response, cancellationToken).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        var message = response.StatusCode switch {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                $"Azure Speech {operation} was rejected with HTTP {status}. Check that your identity has the 'Cognitive Services User' role on the " +
                $"Foundry resource and the right tenant, or that the API key is valid. See {AuthHelp}.",
            HttpStatusCode.BadRequest when voice is not null =>
                $"Azure Speech {operation} returned HTTP 400: voice '{voice}' not available in region, or the request was rejected. {body}".TrimEnd(),
            HttpStatusCode.TooManyRequests =>
                $"Azure Speech {operation} is throttled (HTTP 429) after one retry. Wait and try again, or switch to local speech.",
            _ => $"Azure Speech {operation} failed with HTTP {status}. {body}".TrimEnd()
        };
        return new AzureSpeechException(message, response.StatusCode);
    }

    internal static TimeSpan RetryDelay(HttpResponseMessage response)
    {
        var retry = response.Headers.RetryAfter;
        var delay = retry?.Delta ?? (retry?.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(1));
        return delay < TimeSpan.Zero ? TimeSpan.Zero : delay > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : delay;
    }

    private static async Task<string> SafeBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            text = text.ReplaceLineEndings(" ").Trim();
            return text.Length > 300 ? text[..300] + "…" : text;
        }
        catch (Exception error) when (error is HttpRequestException or InvalidOperationException) { return ""; }
    }
}

internal static class SpeechHttp
{
    /// <summary>Sends a freshly built request; on HTTP 429 waits for <c>Retry-After</c> (capped at 30 s) and retries once.</summary>
    public static async Task<HttpResponseMessage> SendWithRetryAsync(HttpClient http, Func<HttpRequestMessage> create,
        Func<TimeSpan, CancellationToken, Task> delay, CancellationToken cancellationToken)
    {
        var response = await http.SendAsync(create(), HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.TooManyRequests) return response;
        var wait = AzureSpeechException.RetryDelay(response);
        response.Dispose();
        await delay(wait, cancellationToken).ConfigureAwait(false);
        return await http.SendAsync(create(), HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
    }

    public static void AddCommonHeaders(HttpRequestMessage request)
    {
        request.Headers.UserAgent.ParseAdd("MediaStudio");
    }
}
