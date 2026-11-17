using GitHub.Copilot;
using MediaStudio.Configuration;

namespace MediaStudio.Agents.Copilot;

#pragma warning disable GHCP001

/// <summary>What the app knows about GitHub Copilot on this machine. <see cref="Remediation"/> says what to do next.</summary>
public sealed record CopilotStatus(bool Installed, bool Authenticated, string? Login, string[] Models, string? Remediation = null)
{
    public bool Ready => Installed && Authenticated;
}

/// <summary>Checks Copilot sign-in and the signed-in user's own model list. Never throws for "not installed" or "not signed in".</summary>
public sealed class CopilotProbe(CopilotClientFactory clients)
{
    public const string NotEntitled =
        "Your GitHub account has no Copilot plan. Copilot Free is available at github.com/features/copilot, or use BYOK (run the Copilot agent on your own models).";
    public const string PolicyBlocked =
        "Your organization's Copilot policy blocks this use. Ask an admin to enable Copilot CLI/SDK access, or use BYOK.";
    public const string SignIn =
        "Not signed in. Choose \"Sign in with GitHub\" in Settings → Setup → GitHub Copilot, use `gh auth login`, or set COPILOT_GITHUB_TOKEN.";

    public async Task<CopilotStatus> ProbeAsync(CancellationToken cancellationToken = default)
    {
        if (clients.ResolveCliPath() is null)
            return new(false, false, null, [], "The GitHub Copilot runtime is not installed next to the app. Reinstall Media Studio or set " + CopilotCliLocator.OverrideVariable + ".");
        if (clients.IsCloud && !clients.Settings.Privacy.AllowCloud)
            return new(true, false, null, [], "Cloud is off in Settings. Turn on privacy.allowCloud to use your Copilot subscription, or use BYOK with a local connection.");
        try
        {
            var client = await clients.GetClientAsync(cancellationToken).ConfigureAwait(false);
            if (clients.IsByok)
            {
                var byokModels = await client.ListModelsAsync(cancellationToken).ConfigureAwait(false);
                return new(true, true, null, [.. byokModels.Select(m => m.Id)]);
            }
            var auth = await client.GetAuthStatusAsync(cancellationToken).ConfigureAwait(false);
            if (!auth.IsAuthenticated) return new(true, false, null, [], MapError(auth.StatusMessage) ?? SignIn);
            var models = await client.ListModelsAsync(cancellationToken).ConfigureAwait(false);
            var enabled = models.Where(m => m.Policy?.State is null or "enabled").Select(m => m.Id).ToArray();
            return new(true, true, auth.Login, enabled, enabled.Length == 0 ? NotEntitled : null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return new(true, false, null, [], MapError(ex.Message) ?? "Copilot could not start: " + ex.Message);
        }
    }

    /// <summary>Maps the runtime's entitlement and policy messages to user-facing guidance.</summary>
    public static string? MapError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        if (message.Contains("not entitled", StringComparison.OrdinalIgnoreCase)
            || message.Contains("no copilot", StringComparison.OrdinalIgnoreCase)
            || message.Contains("subscription", StringComparison.OrdinalIgnoreCase)) return NotEntitled;
        if (message.Contains("policy", StringComparison.OrdinalIgnoreCase)) return PolicyBlocked;
        return null;
    }

    /// <summary>The default model: the first Claude Sonnet or GPT entry from the user's own list, else the first model.</summary>
    public static string? DefaultModel(IReadOnlyList<string> models) =>
        models.FirstOrDefault(m => m.Contains("sonnet", StringComparison.OrdinalIgnoreCase))
        ?? models.FirstOrDefault(m => m.StartsWith("gpt", StringComparison.OrdinalIgnoreCase))
        ?? models.FirstOrDefault();
}
