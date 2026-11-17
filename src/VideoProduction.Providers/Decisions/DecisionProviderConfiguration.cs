using System.Text.Json.Serialization;

namespace VideoProduction.Providers;

/// <summary>Legacy job-config native OpenAI Decisions provider (kept for job JSON compatibility).</summary>
public sealed record OpenAiDecisionsConfiguration(string Endpoint, string Model, string? ApiKeyFile, bool AllowRemote = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Authentication = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TenantId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool StateAsText = false);

/// <summary>Legacy job-config decision providers. Both null is valid: the heuristic decider answers.</summary>
public sealed record DecisionProviderConfiguration(string? JuliaModelDirectory, OpenAiDecisionsConfiguration? OpenAi);

