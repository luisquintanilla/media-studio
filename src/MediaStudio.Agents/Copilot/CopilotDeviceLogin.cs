using System.Diagnostics;
using System.Text.RegularExpressions;

namespace MediaStudio.Agents.Copilot;

/// <summary>A GitHub device-flow prompt: the one-time code and the page where the user enters it.</summary>
public sealed record CopilotDeviceCode(string UserCode, Uri VerificationUri);

/// <summary>
/// "Sign in with GitHub": runs the bundled CLI's <c>copilot login</c> as a hidden child process with the app's
/// private Copilot home, parses the one-time code and verification URL from its output, then polls the
/// signed-in status every 2 s (5 min timeout, cancellable). If parsing fails, callers fall back to a visible
/// console running <c>copilot login</c>, <c>gh auth login</c>, or <c>COPILOT_GITHUB_TOKEN</c>.
/// </summary>
public static partial class CopilotDeviceLogin
{
    public static readonly Uri DeviceUri = new("https://github.com/login/device");
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    [GeneratedRegex(@"\b[A-Z0-9]{4}-[A-Z0-9]{4}\b")]
    private static partial Regex CodePattern();

    [GeneratedRegex(@"https://github\.com/login/device\b")]
    private static partial Regex UrlPattern();

    /// <summary>Returns the code once both the code and the device URL have appeared in <paramref name="output"/>.</summary>
    public static CopilotDeviceCode? Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var code = CodePattern().Match(output);
        return code.Success && UrlPattern().IsMatch(output) ? new(code.Value, DeviceUri) : null;
    }

    /// <summary>Reads the CLI output until a device code appears, the stream ends (null) or <paramref name="timeout"/> passes (null).</summary>
    public static async Task<CopilotDeviceCode?> ReadCodeAsync(TextReader output, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var seen = new System.Text.StringBuilder();
        try
        {
            while (await output.ReadLineAsync(deadline.Token).ConfigureAwait(false) is { } line)
            {
                seen.AppendLine(line);
                if (Parse(seen.ToString()) is { } code) return code;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        return null;
    }

    /// <summary>Polls <paramref name="isAuthenticated"/> until true (returns true) or the timeout passes (false). Cancellation throws.</summary>
    public static async Task<bool> WaitForSignInAsync(Func<CancellationToken, Task<bool>> isAuthenticated,
        TimeSpan? pollInterval = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(isAuthenticated);
        var stopwatch = Stopwatch.StartNew();
        var limit = timeout ?? Timeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await isAuthenticated(cancellationToken).ConfigureAwait(false)) return true;
            if (stopwatch.Elapsed >= limit) return false;
            await Task.Delay(pollInterval ?? PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Starts <c>copilot login</c> hidden, with redirected output, in the app's private Copilot home.</summary>
    public static Process StartLogin(CopilotClientFactory clients) => Start(clients, "login", hidden: true);

    /// <summary>Fallback when the output cannot be parsed: a visible console running <c>copilot login</c>.</summary>
    public static Process StartVisibleLogin(CopilotClientFactory clients) => Start(clients, "login", hidden: false);

    /// <summary>"Sign out": <c>copilot logout</c> in the same private home.</summary>
    public static Process StartLogout(CopilotClientFactory clients) => Start(clients, "logout", hidden: true);

    private static Process Start(CopilotClientFactory clients, string command, bool hidden)
    {
        ArgumentNullException.ThrowIfNull(clients);
        var cli = clients.ResolveCliPath() ?? throw new VideoProduction.VideoException("copilot.not-installed",
            "The GitHub Copilot runtime was not found. Reinstall Media Studio, or set " + CopilotCliLocator.OverrideVariable + ".");
        Directory.CreateDirectory(clients.ConfigDirectory);
        var info = new ProcessStartInfo(cli, command) {
            UseShellExecute = false,
            CreateNoWindow = hidden,
            RedirectStandardOutput = hidden,
            RedirectStandardError = hidden,
            WorkingDirectory = clients.HomeDirectory
        };
        info.Environment.Clear();
        foreach (var (name, value) in CopilotClientFactory.RuntimeEnvironment(copilotHome: clients.ConfigDirectory))
            info.Environment[name] = value;
        return Process.Start(info) ?? throw new VideoProduction.VideoException("copilot.login", "Could not start `copilot " + command + "`.");
    }
}
