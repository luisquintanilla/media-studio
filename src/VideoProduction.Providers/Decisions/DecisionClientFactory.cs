using MediaStudio.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VideoProduction.Decisions.Experimental;
using VideoProduction.Decisions.Experimental.Julia;
#if OPENAI_DECISIONS
using System.ClientModel;
using System.ClientModel.Primitives;
using OpenAI.Decisions;
using VideoProduction.Decisions;
#endif

#pragma warning disable OPENAI001
namespace VideoProduction.Providers.Decisions;

/// <summary>
/// Builds the ordered decision chain: Julia (local ONNX, when installed and not off) → native OpenAI Decisions
/// (only when cloud is allowed and the build includes them) → <see cref="HeuristicDecisionClient"/> (always last,
/// never fails), wrapped in a <see cref="FallbackDecisionClient"/>.
/// </summary>
public static class DecisionClientFactory
{
    public const string JuliaAssetId = "julia-1";

    /// <summary>Chain from the v2 settings file.</summary>
    /// <param name="juliaLoader">Test seam; loads a Julia client from a verified asset directory.</param>
    /// <param name="choiceRule">Optional domain rule for the heuristic link (see <see cref="HeuristicDecisionClient"/>).</param>
    public static IDecisionClient Create(ProviderSettings settings, ModelAssets? assets, ILoggerFactory? loggerFactory = null,
        Func<string, IDecisionClient>? juliaLoader = null,
        Func<System.Text.Json.JsonElement, ChoiceDecisionQuestion, string?>? choiceRule = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger(typeof(DecisionClientFactory).FullName!);
        var chain = new List<IDecisionClient>();
        var decider = settings.Chat.Routing.Decider?.Trim().ToLowerInvariant() ?? "auto";
        if (decider != "heuristic" && settings.Decisions.Julia != JuliaMode.Off)
        {
            string? directory = null;
            try { directory = assets?.GetPath(JuliaAssetId); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or KeyNotFoundException or InvalidOperationException)
            {
                logger.LogWarning("Julia asset lookup failed ({Error}); the heuristic decider is used.", ex.GetType().Name);
            }
            if (directory is not null)
                chain.Add(new LazyDecisionClient("julia", () => (juliaLoader ?? LoadJulia)(directory)));
            else if (settings.Decisions.Julia == JuliaMode.On)
                logger.LogWarning("Julia is on but '{Asset}' is not installed; run 'video-production assets ensure --id {Asset}'.", JuliaAssetId, JuliaAssetId);
        }
#if OPENAI_DECISIONS
        if (decider is "auto" or "openai" && settings.Privacy.AllowCloud
            && settings.Connections.FirstOrDefault(c => c.Kind == ConnectionKind.Foundry) is { } foundry)
            chain.Add(new LazyDecisionClient("openai", () => CreateFoundryDecisions(foundry, settings.Decisions.CloudDeployment)));
#endif
        chain.Add(new HeuristicDecisionClient(choiceRule));
        return new FallbackDecisionClient(chain, logger);
    }

    /// <summary>
    /// Chain from a legacy job configuration. Configured providers are tried first, the heuristic always answers last.
    /// A native OpenAI provider that this build cannot construct is skipped with a warning.
    /// </summary>
    public static IDecisionClient Create(DecisionProviderConfiguration configuration, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        logger ??= NullLogger.Instance;
        var chain = new List<IDecisionClient>();
        if (configuration.JuliaModelDirectory is { } directory)
            chain.Add(new LazyDecisionClient("julia", () => LoadJulia(PrivateFiles.LocalPath(directory))));
        if (configuration.OpenAi is { } native)
        {
#if OPENAI_DECISIONS
            chain.Add(new LazyDecisionClient("openai", () => CreateStrict(new(null, native))));
#else
            logger.LogWarning("Native OpenAI Decisions are not part of this build (needs OpenAI 2.15); the next decider is used.");
#endif
        }
        chain.Add(new HeuristicDecisionClient());
        return new FallbackDecisionClient(chain, logger);
    }

    /// <summary>
    /// Exactly one explicit provider, no fallback. Used where a durable binding records which model decided
    /// (storyboard route bindings), so a silent substitution would falsify the evidence.
    /// </summary>
    public static IDecisionClient CreateStrict(DecisionProviderConfiguration configuration,
        System.ClientModel.Primitives.PipelineTransport? transport = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        Guard.Require((configuration.JuliaModelDirectory is not null) != (configuration.OpenAi is not null),
            "decision.config", "Choose exactly one explicit Julia/local or typed OpenAI decision provider for a bound route.");
        if (configuration.JuliaModelDirectory is { } directory)
            return LoadJulia(PrivateFiles.LocalPath(directory));
#if !OPENAI_DECISIONS
        _ = transport;
        throw new VideoException("decision.unavailable",
            "Native OpenAI Decisions need OpenAI 2.15, which the published Microsoft.Extensions.AI.OpenAI does not support yet; use the Julia or Heuristic decision provider.");
#else
        var native = configuration.OpenAi!;
        Guard.Require(Uri.TryCreate(native.Endpoint, UriKind.Absolute, out var endpoint)
            && endpoint.Scheme is "http" or "https" && endpoint.UserInfo.Length == 0
            && endpoint.Query.Length == 0 && endpoint.Fragment.Length == 0
            && (endpoint.IsLoopback || native.AllowRemote && endpoint.Scheme == "https")
            && !string.IsNullOrWhiteSpace(native.Model), "decision.config", "Use an explicit local endpoint/model or an opted-in trusted HTTPS endpoint.");
        var options = new DecisionsClientOptions
        {
            Endpoint = endpoint, RetryPolicy = new ClientRetryPolicy(0), NetworkTimeout = TimeSpan.FromSeconds(60)
        };
        if (transport is not null) options.Transport = transport;
        if (native.Authentication == "azure-cli")
        {
            Guard.Require(native.ApiKeyFile is null && endpoint!.Scheme == "https"
                && endpoint.AbsolutePath.TrimEnd('/') == "/openai/v1"
                && (endpoint.Host.EndsWith(".openai.azure.com", StringComparison.OrdinalIgnoreCase)
                    || endpoint.Host.EndsWith(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase)),
                "decision.auth", "Entra native Decisions require a verified trusted Azure endpoint and no key file.");
            var credential = CredentialFactory.CreateEntra(native.TenantId);
            return new OpenAiDecisionClient(new DecisionsClient(
                new BearerTokenPolicy(credential, CredentialFactory.CognitiveServicesScope), options),
                native.Model, stateAsText: native.StateAsText);
        }
        Guard.Require((native.Authentication is null or "api-key") && native.ApiKeyFile is not null,
            "decision.auth", "Choose explicit native Decisions Entra auth or the existing private key-file configuration; no credential fallback.");
        PrivateFiles.RequirePrivate(native.ApiKeyFile!);
        var key = File.ReadAllText(native.ApiKeyFile!).Trim();
        Guard.Require(key.Length is > 0 and <= 4096 && !key.Any(char.IsControl), "decision.config", "The explicitly configured private decision credential is invalid.");
        return new OpenAiDecisionClient(new DecisionsClient(new ApiKeyCredential(key), options), native.Model, stateAsText: native.StateAsText);
#endif
    }

#if OPENAI_DECISIONS
    private static IDecisionClient CreateFoundryDecisions(ConnectionSettings foundry, string deployment)
    {
        var endpoint = FoundryEndpoints.From(foundry).OpenAIv1;
        var options = new DecisionsClientOptions { Endpoint = endpoint, RetryPolicy = new ClientRetryPolicy(0), NetworkTimeout = TimeSpan.FromSeconds(30) };
        return foundry.Auth == AuthMode.ApiKey
            ? new OpenAiDecisionClient(new DecisionsClient(new ApiKeyCredential(CredentialFactory.GetApiKey(foundry.Id)), options), deployment)
            : new OpenAiDecisionClient(new DecisionsClient(new BearerTokenPolicy(CredentialFactory.CreateTokenCredential(foundry),
                CredentialFactory.CognitiveServicesScope), options), deployment);
    }
#endif

    /// <summary>Loads Julia from a verified directory with the app's CPU threading policy.</summary>
    public static IDecisionClient LoadJulia(string modelDirectory) =>
        JuliaDecisionClient.LoadFromDirectory(JuliaModelAssets.VerifyDirectory(modelDirectory),
            options => { options.IntraOpNumThreads = 2; options.InterOpNumThreads = 1; });

    /// <summary>Evidence fingerprint recorded in route bindings for a legacy configuration.</summary>
    public static string EvidenceSha256(DecisionProviderConfiguration configuration) => configuration.JuliaModelDirectory is not null
        ? JuliaModelAssets.Fingerprint : Files.Fingerprint(configuration);
}
