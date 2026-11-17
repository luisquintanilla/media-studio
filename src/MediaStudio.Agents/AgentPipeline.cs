using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using VideoProduction;
using VideoProduction.Providers;

namespace MediaStudio.Agents;

/// <summary>What one agent run used: attached to the response so the UI badge works for MAF and Copilot alike.</summary>
public sealed record AgentUsageReceipt(AuthoringRuntime Runtime, string Provider, string? Model, bool IsCloud,
    long? InputTokens, long? OutputTokens, long? TotalTokens)
{
    /// <summary>"🤖 Copilot · model", "⚡ local · model" or "☁ cloud · model".</summary>
    public string Badge() => AgentPipeline.Badge(Runtime, Model, IsCloud);
}

/// <summary>
/// The middleware every authoring/illustration agent runs through, MAF or Copilot:
/// <c>CloudGuard → UsageReceipt → OpenTelemetry</c>.
/// </summary>
public static class AgentPipeline
{
    public const string UsageKey = "media.agent.usage";
    public const string ActivitySourceName = "MediaStudio.Agents";

    public static AIAgent Wrap(AIAgent agent, bool allowCloud)
    {
        ArgumentNullException.ThrowIfNull(agent);
        return agent.AsBuilder()
            .Use(inner => new CloudGuardAgent(inner, allowCloud))
            .Use(inner => new UsageReceiptAgent(inner))
            .UseOpenTelemetry(ActivitySourceName)
            .Build();
    }

    public static string Badge(AuthoringRuntime runtime, string? model, bool isCloud)
    {
        var label = runtime == AuthoringRuntime.Copilot ? "🤖 Copilot" : isCloud ? "☁ cloud" : "⚡ local";
        return string.IsNullOrWhiteSpace(model) ? label : label + " · " + model;
    }

    /// <summary>The receipt attached by <see cref="Wrap"/>, if any.</summary>
    public static AgentUsageReceipt? Receipt(AgentResponse response) =>
        response?.AdditionalProperties?.TryGetValue(UsageKey, out var value) == true ? value as AgentUsageReceipt : null;

    public static AgentUsageReceipt? Receipt(AgentResponseUpdate update) =>
        update?.AdditionalProperties?.TryGetValue(UsageKey, out var value) == true ? value as AgentUsageReceipt : null;
}

/// <summary>Rejects a run before it starts when the agent sends prompts off this machine and cloud is disabled.</summary>
public sealed class CloudGuardAgent(AIAgent inner, bool allowCloud) : DelegatingAIAgent(inner)
{
    public const string ErrorCode = "agent.cloud-disabled";

    private void Check()
    {
        if (!MediaStudio.Configuration.CloudGuard.Allows(allowCloud, InnerAgent.GetService<AgentRuntimeInfo>() is { IsCloud: true }))
            throw new VideoException(ErrorCode, DecisionRoutingChatClient.CloudDisabledMessage);
    }

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
    {
        Check();
        return base.CreateSessionCoreAsync(cancellationToken);
    }

    protected override Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null,
        AgentRunOptions? options = null, CancellationToken cancellationToken = default)
    {
        Check();
        return base.RunCoreAsync(messages, session, options, cancellationToken);
    }

    protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(IEnumerable<ChatMessage> messages,
        AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
    {
        Check();
        return base.RunCoreStreamingAsync(messages, session, options, cancellationToken);
    }
}

/// <summary>Attaches an <see cref="AgentUsageReceipt"/> to the response (or the final streaming update).</summary>
public sealed class UsageReceiptAgent(AIAgent inner) : DelegatingAIAgent(inner)
{
    private AgentUsageReceipt Receipt(UsageDetails? usage)
    {
        var info = InnerAgent.GetService<AgentRuntimeInfo>() ?? new AgentRuntimeInfo(AuthoringRuntime.Maf, InnerAgent.Name ?? "agent", null);
        return new(info.Runtime, info.Provider, info.Model, info.IsCloud,
            usage?.InputTokenCount, usage?.OutputTokenCount, usage?.TotalTokenCount);
    }

    protected override async Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null,
        AgentRunOptions? options = null, CancellationToken cancellationToken = default)
    {
        var response = await base.RunCoreAsync(messages, session, options, cancellationToken).ConfigureAwait(false);
        (response.AdditionalProperties ??= [])[AgentPipeline.UsageKey] = Receipt(response.Usage);
        return response;
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(IEnumerable<ChatMessage> messages,
        AgentSession? session = null, AgentRunOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        UsageDetails? usage = null;
        AgentResponseUpdate? pending = null;
        await foreach (var update in base.RunCoreStreamingAsync(messages, session, options, cancellationToken).ConfigureAwait(false))
        {
            foreach (var content in update.Contents.OfType<UsageContent>())
            {
                usage ??= new UsageDetails();
                usage.Add(content.Details);
            }
            if (pending is not null) yield return pending;
            pending = update;
        }
        if (pending is null) yield break;
        (pending.AdditionalProperties ??= [])[AgentPipeline.UsageKey] = Receipt(usage);
        yield return pending;
    }
}
