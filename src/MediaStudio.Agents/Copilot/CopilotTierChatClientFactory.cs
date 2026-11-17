using System.Runtime.CompilerServices;
using System.Text.Json;
using MediaStudio.Configuration;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using VideoProduction.Providers;

namespace MediaStudio.Agents.Copilot;

/// <summary>Builds the router's <c>copilot</c> tier client: a no-tools Copilot agent behind <see cref="CopilotTierChatClient"/>.</summary>
public sealed class CopilotTierChatClientFactory(CopilotAgentFactory agents) : ITierChatClientFactory
{
    public ConnectionKind Kind => ConnectionKind.GitHubCopilot;

    public IChatClient Create(ChatTier tier, ConnectionSettings connection, ProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(tier);
        return agents.CreateChatClient(AgentRole.Tier, model: tier.Model);
    }
}

/// <summary>
/// <see cref="IChatClient"/> over a Copilot (or any) <see cref="AIAgent"/>: stateless (a new session per call, the
/// full transcript passed in), reports <c>ChatClientMetadata("github-copilot", null, model)</c>, and enforces
/// <see cref="ChatResponseFormat.Json"/> by instruction plus <see cref="JsonDocument"/> validation with one repair retry.
/// </summary>
public sealed class CopilotTierChatClient : IChatClient
{
    public const string ProviderName = "github-copilot";
    public const string JsonInstruction =
        "Respond with a single valid JSON value only: no prose, no markdown fences.";
    public const string RepairInstruction =
        "Your previous reply was not valid JSON. Reply again with only the corrected JSON value, nothing else.";

    private readonly AIAgent agent;
    private readonly ChatClientMetadata metadata;

    public CopilotTierChatClient(AIAgent agent, string? model)
    {
        ArgumentNullException.ThrowIfNull(agent);
        this.agent = agent;
        metadata = new ChatClientMetadata(ProviderName, null, model);
    }

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var transcript = Prepare(messages, options);
        var response = await RunAsync(transcript, cancellationToken).ConfigureAwait(false);
        if (WantsJson(options) && !IsJson(response.Text))
        {
            transcript = [.. transcript, .. response.Messages, new ChatMessage(ChatRole.User, RepairInstruction)];
            var repaired = await RunAsync(transcript, cancellationToken).ConfigureAwait(false);
            repaired.Usage = Add(response.Usage, repaired.Usage);
            response = repaired;
            if (!IsJson(response.Text))
                throw new VideoProduction.VideoException("copilot.invalid-json", "GitHub Copilot did not return valid JSON after one repair attempt.");
            response.Messages = [new ChatMessage(ChatRole.Assistant, StripFences(response.Text))];
        }
        else if (WantsJson(options))
            response.Messages = [new ChatMessage(ChatRole.Assistant, StripFences(response.Text))];
        return response;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (WantsJson(options))
        {
            // JSON must be validated as a whole, so stream the validated response as one update.
            var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            foreach (var update in response.ToChatResponseUpdates()) yield return update;
            yield break;
        }
        var session = await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        await foreach (var update in agent.RunStreamingAsync(Prepare(messages, options), session, cancellationToken: cancellationToken)
            .ConfigureAwait(false))
        {
            var chat = update.AsChatResponseUpdate();
            chat.ModelId ??= metadata.DefaultModelId;
            yield return chat;
        }
    }

    private async Task<ChatResponse> RunAsync(IReadOnlyList<ChatMessage> transcript, CancellationToken cancellationToken)
    {
        var session = await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        var result = await agent.RunAsync(transcript, session, cancellationToken: cancellationToken).ConfigureAwait(false);
        var response = result.AsChatResponse();
        response.ModelId ??= metadata.DefaultModelId;
        return response;
    }

    private static List<ChatMessage> Prepare(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var list = new List<ChatMessage>();
        if (!string.IsNullOrWhiteSpace(options?.Instructions)) list.Add(new ChatMessage(ChatRole.System, options.Instructions));
        list.AddRange(messages);
        if (WantsJson(options))
        {
            var schema = (options!.ResponseFormat as ChatResponseFormatJson)?.Schema is { } s
                ? "\nThe JSON must match this schema:\n" + s.GetRawText() : "";
            list.Add(new ChatMessage(ChatRole.System, JsonInstruction + schema));
        }
        return list;
    }

    private static bool WantsJson(ChatOptions? options) => options?.ResponseFormat is ChatResponseFormatJson;

    internal static bool IsJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            using var _ = JsonDocument.Parse(StripFences(text));
            return true;
        }
        catch (JsonException) { return false; }
    }

    internal static string StripFences(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal)) return trimmed;
        var firstNewLine = trimmed.IndexOf('\n');
        var end = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        return firstNewLine > 0 && end > firstNewLine ? trimmed[(firstNewLine + 1)..end].Trim() : trimmed;
    }

    private static UsageDetails? Add(UsageDetails? first, UsageDetails? second)
    {
        if (first is null) return second;
        if (second is null) return first;
        var total = new UsageDetails();
        total.Add(first);
        total.Add(second);
        return total;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is not null ? null
        : serviceType == typeof(ChatClientMetadata) ? metadata
        : serviceType.IsInstanceOfType(this) ? this
        : agent.GetService(serviceType, serviceKey);

    public void Dispose() => (agent as IDisposable)?.Dispose();
}
