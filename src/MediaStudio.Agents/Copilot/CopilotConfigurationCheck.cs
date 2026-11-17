using MediaStudio.Configuration;
using Microsoft.Extensions.AI;

namespace MediaStudio.Agents.Copilot;

#pragma warning disable GHCP001

/// <summary>
/// The <c>configure test</c> rows for GitHub Copilot: <c>copilot.auth</c> (or <c>copilot.byok</c>), <c>copilot.models</c>,
/// <c>copilot.tier</c> (a short chat through the Copilot tier client) and <c>copilot.agent</c> (one agent run that must call
/// a probe tool, proving the tool allow-list and hook path). Nothing starts when cloud is off for a cloud backend.
/// </summary>
public sealed class CopilotConfigurationCheck(Func<ProviderSettings, CopilotClientFactory>? clients = null, TimeSpan? timeout = null)
    : IConfigurationCheck
{
    public const string ProbeToolName = "media_probe";
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(60);

    public async Task<IReadOnlyList<RoleCheck>> CheckAsync(ProviderSettings settings, bool cloudOnly, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!ProviderSettingsDefaults.UsesCopilot(settings)) return [];
        await using var factory = (clients ?? (s => new CopilotClientFactory(s)))(settings);
        if (cloudOnly && !factory.IsCloud) return [];
        var authRole = factory.IsByok ? "copilot.byok" : "copilot.auth";
        if (factory.IsCloud && !settings.Privacy.AllowCloud)
            return [new(authRole, RoleCheckStatus.Skipped, "cloud is off (privacy.allowCloud = false)")];

        var rows = new List<RoleCheck>();
        var status = await new CopilotProbe(factory).ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (!status.Ready)
        {
            rows.Add(new(authRole, RoleCheckStatus.Failed, status.Installed ? "not ready" : "not installed", status.Remediation));
            return rows;
        }
        rows.Add(new(authRole, RoleCheckStatus.Ok, factory.IsByok
            ? $"BYOK on '{settings.Agents.Copilot.ByokConnection}' (no GitHub sign-in)"
            : $"signed in{(status.Login is null ? "" : " as " + status.Login)}"));

        var model = settings.Agents.Copilot.Model;
        rows.Add(model is not null && status.Models.Length > 0 && !status.Models.Contains(model, StringComparer.OrdinalIgnoreCase)
            ? new("copilot.models", RoleCheckStatus.Failed, $"'{model}' is not in your model list ({string.Join(", ", status.Models.Take(8))})",
                "Pick one of your models in Setup or run: configure copilot --model <id>")
            : new("copilot.models", RoleCheckStatus.Ok, $"{status.Models.Length} model(s); using {model ?? CopilotProbe.DefaultModel(status.Models) ?? "the Copilot default"}"));

        var agents = new CopilotAgentFactory(factory);
        if (settings.Chat.Tiers.FirstOrDefault(t => settings.FindConnection(t.Connection)?.Kind == ConnectionKind.GitHubCopilot) is { } tier)
            rows.Add(await RunAsync("copilot.tier", async token =>
            {
                var response = await agents.CreateChatClient(AgentRole.Tier, model: tier.Model)
                    .GetResponseAsync("Reply with the single word OK.", new ChatOptions { MaxOutputTokens = 16 }, token).ConfigureAwait(false);
                return new("copilot.tier", RoleCheckStatus.Ok, $"{tier.Model} replied ({response.Text.Trim().Length} chars)");
            }, cancellationToken).ConfigureAwait(false));

        if (settings.Agents.Authoring.Runtime == AgentRuntime.Copilot)
            rows.Add(await RunAsync("copilot.agent", async token =>
            {
                var called = 0;
                var tool = AIFunctionFactory.Create(() => { Interlocked.Increment(ref called); return "recorded"; }, ProbeToolName,
                    "Records that the media tool path works. Call it exactly once.");
                var agent = agents.Create(AgentRole.Authoring, [tool], $"Call the {ProbeToolName} tool exactly once, then reply DONE.");
                await agent.RunAsync("Run the configuration check.", cancellationToken: token).ConfigureAwait(false);
                return called > 0
                    ? new("copilot.agent", RoleCheckStatus.Ok, "agent ran and called the media tool")
                    : new("copilot.agent", RoleCheckStatus.Failed, "agent ran but did not call the media tool", "Pick a model with tool calling.");
            }, cancellationToken).ConfigureAwait(false));
        return rows;
    }

    private async Task<RoleCheck> RunAsync(string role, Func<CancellationToken, Task<RoleCheck>> probe, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        try
        {
            return await probe(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(role, RoleCheckStatus.Failed, $"timed out after {_timeout.TotalSeconds:0} s");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(role, RoleCheckStatus.Failed, ex.Message, CopilotProbe.MapError(ex.Message));
        }
    }
}
