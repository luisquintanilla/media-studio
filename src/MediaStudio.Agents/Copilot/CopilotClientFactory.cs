using GitHub.Copilot;
using MediaStudio.Configuration;
using VideoProduction;
using VideoProduction.Providers;

namespace MediaStudio.Agents.Copilot;

#pragma warning disable GHCP001

/// <summary>
/// Owns the one shared <see cref="CopilotClient"/> per app (lazily started, disposed with the app).
/// Subscription mode signs in as the CLI's stored GitHub login (<see cref="AuthMode.LoggedInUser"/>) or with a
/// token from <c>COPILOT_GITHUB_TOKEN</c>/<c>GH_TOKEN</c> (<see cref="AuthMode.EnvToken"/>); tokens are never stored.
/// BYOK mode runs the same locked-down harness against the user's own connection and needs no GitHub sign-in.
/// The runtime gets a private home under LocalAppData, a sanitized environment, and Copilot telemetry disabled.
/// </summary>
public sealed class CopilotClientFactory : IAsyncDisposable, IDisposable
{
    public static readonly string[] TokenVariables = ["COPILOT_GITHUB_TOKEN", "GH_TOKEN", "GITHUB_TOKEN"];

    private readonly Func<CopilotClientOptions, CancellationToken, Task<CopilotClient>> start;
    private readonly Func<string, string?> environment;
    private readonly SemaphoreSlim gate = new(1, 1);
    private CopilotClient? client;

    public CopilotClientFactory(ProviderSettings settings, string? homeDirectory = null, string? cliPath = null,
        Func<string, string?>? environment = null,
        Func<CopilotClientOptions, CancellationToken, Task<CopilotClient>>? start = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Settings = settings;
        HomeDirectory = homeDirectory ?? DefaultHomeDirectory;
        CliPath = cliPath;
        this.environment = environment ?? Environment.GetEnvironmentVariable;
        this.start = start ?? StartAsync;
    }

    public static string DefaultHomeDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaStudio", "copilot");

    public ProviderSettings Settings { get; }
    public string HomeDirectory { get; }
    public string? CliPath { get; }
    /// <summary>The empty private working directory every Copilot session runs in (no repository, no user files).</summary>
    public string WorkingDirectory => Path.Combine(HomeDirectory, "work");
    public string ConfigDirectory => Path.Combine(HomeDirectory, "copilot-home");
    public bool IsByok => Settings.Agents.Copilot.Backend == CopilotBackend.Byok;

    /// <summary>The GitHub Copilot connection (subscription mode), or a logged-in-user default when none is configured.</summary>
    public ConnectionSettings SubscriptionConnection =>
        Settings.Connections.FirstOrDefault(c => c.Kind == ConnectionKind.GitHubCopilot)
        ?? new ConnectionSettings { Id = ProviderSettingsDefaults.CopilotConnectionId, Kind = ConnectionKind.GitHubCopilot, Auth = AuthMode.LoggedInUser };

    /// <summary>True when a run sends prompts off this machine: the subscription backend, or BYOK on a cloud connection.</summary>
    public bool IsCloud => !IsByok || Settings.FindConnection(Settings.Agents.Copilot.ByokConnection) is not { IsCloud: false };

    public string? ResolveCliPath() => CliPath ?? CopilotCliLocator.Find(environment: environment);

    public string? EnvironmentToken() => TokenVariables.Select(environment).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    public CopilotClientOptions CreateOptions(Func<CancellationToken, Task<IList<ModelInfo>>>? listModels = null)
    {
        var cli = ResolveCliPath();
        Guard.Require(cli is not null, "copilot.not-installed",
            "The GitHub Copilot runtime was not found next to the app. Reinstall Media Studio, or set " + CopilotCliLocator.OverrideVariable + ".");
        var auth = SubscriptionConnection.Auth;
        string? token = null;
        if (!IsByok && auth == AuthMode.EnvToken)
        {
            token = EnvironmentToken();
            Guard.Require(token is not null, "copilot.token-missing",
                "Set COPILOT_GITHUB_TOKEN (or GH_TOKEN) for env-token sign-in, or switch the Copilot connection to logged-in-user.");
        }
        return new() {
            Mode = CopilotClientMode.Empty,
            Connection = RuntimeConnection.ForStdio(cli!),
            BaseDirectory = ConfigDirectory,
            WorkingDirectory = WorkingDirectory,
            UseLoggedInUser = !IsByok && auth == AuthMode.LoggedInUser,
            GitHubToken = token,
            Environment = RuntimeEnvironment(environment, ConfigDirectory),
            EnableRemoteSessions = false,
            BuiltinPluginDirectories = [],
            OnListModels = IsByok ? listModels ?? ByokModels : null
        };
    }

    /// <summary>Returns the started shared client. Subscription mode refuses to start while cloud is disabled.</summary>
    public async Task<CopilotClient> GetClientAsync(CancellationToken cancellationToken = default)
    {
        Guard.Require(!IsCloud || Settings.Privacy.AllowCloud, "copilot.cloud-disabled", DecisionRoutingChatClient.CloudDisabledMessage);
        if (client is not null) return client;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (client is not null) return client;
            Directory.CreateDirectory(WorkingDirectory);
            Directory.CreateDirectory(ConfigDirectory);
            PrivateFiles.Protect(HomeDirectory, directory: true);
            client = await start(CreateOptions(), cancellationToken).ConfigureAwait(false);
            return client;
        }
        finally { gate.Release(); }
    }

    private static async Task<CopilotClient> StartAsync(CopilotClientOptions options, CancellationToken cancellationToken)
    {
        var started = new CopilotClient(options);
        try
        {
            await started.StartAsync(cancellationToken).ConfigureAwait(false);
            return started;
        }
        catch
        {
            await started.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private Task<IList<ModelInfo>> ByokModels(CancellationToken cancellationToken)
    {
        IList<ModelInfo> models = Settings.Agents.Copilot.Model is { } model ? [new ModelInfo { Id = model, Name = model }] : [];
        return Task.FromResult(models);
    }

    /// <summary>Only the variables the runtime needs (system paths, proxy and CA settings); Copilot telemetry is disabled.</summary>
    public static Dictionary<string, string> RuntimeEnvironment(Func<string, string?>? environment = null, string? copilotHome = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var names = new[] { "SystemRoot", "SystemDrive", "ComSpec", "PATH", "PATHEXT", "TEMP", "TMP",
            "USERPROFILE", "APPDATA", "LOCALAPPDATA", "HOMEDRIVE", "HOMEPATH", "HOME", "ProgramData", "COPILOT_HOME",
            "GH_CONFIG_DIR", "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY", "SSL_CERT_FILE", "NODE_EXTRA_CA_CERTS" };
        var result = names.Select(name => (name, value: environment(name)))
            .Where(item => item.value is not null).ToDictionary(item => item.name, item => item.value!);
        if (copilotHome is not null) result["COPILOT_HOME"] = copilotHome;
        result["COPILOT_GITHUB_TELEMETRY_DISABLED"] = "true";
        return result;
    }

    /// <summary>Synchronous dispose for hosts (such as a synchronously disposed service provider) that cannot await.</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (client is not null) await client.DisposeAsync().ConfigureAwait(false);
        client = null;
    }
}
