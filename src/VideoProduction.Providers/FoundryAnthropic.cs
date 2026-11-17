using ClientOptions = Anthropic.Core.ClientOptions;
using Anthropic.Credentials;
using Azure.Core;
using Azure.Identity;
using System.ClientModel.Primitives;
using Anthropic;
using Microsoft.Extensions.AI;

namespace VideoProduction.Providers;

internal static class FoundryAnthropic
{
    internal static IChatClient CreateClient(PlannerConfiguration config, string? explicitApiKey,
        HttpClient? httpClient, TokenCredential? credential, PipelineTransport? openAiTransport)
    {
        var sdk = new AnthropicClient(Options(config, explicitApiKey, httpClient, credential, openAiTransport));
        return new OwnedChatClient(sdk.AsIChatClient(config.Model), sdk);
    }

    // The SDK's MEAI adapter deliberately does not own its AnthropicClient.
    private sealed class OwnedChatClient(IChatClient inner, AnthropicClient sdk) : DelegatingChatClient(inner)
    {
        protected override void Dispose(bool disposing)
        {
            try { base.Dispose(disposing); }
            finally { if (disposing) sdk.Dispose(); }
        }
    }

    internal static ClientOptions Options(PlannerConfiguration config, string? explicitApiKey,
        HttpClient? httpClient, TokenCredential? credential, PipelineTransport? openAiTransport)
    {
        Guard.Require(Uri.TryCreate(config.Endpoint, UriKind.Absolute, out var endpoint)
            && endpoint.Scheme == "https" && endpoint.UserInfo.Length == 0
            && endpoint.Query.Length == 0 && endpoint.Fragment.Length == 0
            && endpoint.Host.EndsWith(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase)
            && endpoint.AbsolutePath.TrimEnd('/') == "/anthropic" && config.AllowRemote
            && !string.IsNullOrWhiteSpace(config.Model) && config.Model.Length <= 256 && !config.Model.Any(char.IsControl),
            "planner.config", "Anthropic requires an explicitly approved Foundry HTTPS /anthropic endpoint and exact deployment name.");
        Guard.Require(openAiTransport is null, "planner.config",
            "Anthropic uses an HttpClient transport, not an OpenAI pipeline transport.");
        var options = new ClientOptions {
            BaseUrl = endpoint!.AbsoluteUri.TrimEnd('/'), MaxRetries = 0, Timeout = TimeSpan.FromSeconds(120),
            ApiKey = null, AuthToken = null, OpenTelemetry = false
        };
        // AnthropicClient takes ownership, including an explicitly injected test transport.
        if (httpClient is not null) options.HttpClient = httpClient;
        if (config.Authentication == "azure-cli")
        {
            Guard.Require(config.ApiKeyFile is null && config.ApiKeySecret is null && explicitApiKey is null,
                "planner.config", "Entra authentication cannot also select an API key.");
            credential ??= MediaStudio.Configuration.CredentialFactory.CreateEntra(config.TenantId);
            options.Credentials = new EntraAccessTokenProvider(credential);
        }
        else
        {
            Guard.Require(credential is null, "planner.config", "API-key authentication cannot also select an Entra credential.");
            string key;
            if (config.Authentication == "user-secrets")
            {
                Guard.Require(config.ApiKeyFile is null && explicitApiKey is null && config.ApiKeySecret is not null,
                    "planner.config", "Choose one explicit app provider key reference.");
                key = ProviderUserSecrets.Read(config.ApiKeySecret!);
            }
            else
            {
                Guard.Require(config.Authentication == "api-key" && config.ApiKeySecret is null &&
                    (config.ApiKeyFile is null) != (explicitApiKey is null), "planner.config",
                    "Choose explicit Entra, user-secrets or private API-key authentication; no credential fallback is used.");
                if (config.ApiKeyFile is not null) PrivateFiles.RequirePrivate(config.ApiKeyFile);
                key = explicitApiKey ?? File.ReadAllText(config.ApiKeyFile!).Trim();
            }
            Guard.Require(key.Length is > 0 and <= 4096 && !key.Any(char.IsControl), "planner.config",
                "The explicitly configured Anthropic credential is invalid.");
            options.ApiKey = key;
        }
        return options;
    }

    private sealed class EntraAccessTokenProvider(TokenCredential credential) : IAccessTokenProvider
    {
        public async ValueTask<Anthropic.Credentials.AccessToken> GetTokenAsync(
            bool forceRefresh = false, CancellationToken cancellationToken = default)
        {
            var token = await credential.GetTokenAsync(new TokenRequestContext(
                ["https://cognitiveservices.azure.com/.default"]), cancellationToken).ConfigureAwait(false);
            Guard.Require(!string.IsNullOrWhiteSpace(token.Token), "planner.authentication",
                "Anthropic Entra authentication returned no usable token; no fallback credential was used.");
            return new(token.Token, token.ExpiresOn.ToUnixTimeSeconds());
        }
        public void Dispose() { }
    }
}
