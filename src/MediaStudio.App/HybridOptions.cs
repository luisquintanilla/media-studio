namespace MediaStudio.App;

/// <summary>
/// Launch options for the Media Studio window. <c>--configure</c> and <c>--diagnostics</c> are handled by
/// <see cref="Program"/> before these are parsed.
/// </summary>
public sealed record HybridOptions(string? StateRoot, CopilotAuthoringSettings? Authoring, string? VoiceConfig = null,
    string? MediaConfig = null, string? SourceConfig = null, bool NewProject = false)
{
    /// <summary>
    /// Parses the launch arguments. <c>--authoring agent</c> authors through the GitHub Copilot agent runtime configured
    /// in Settings (subscription or BYOK); <paramref name="copilotRuntime"/> supplies it (default: from the settings file).
    /// </summary>
    public static HybridOptions Parse(string[] args, Func<MediaStudio.Agents.Copilot.CopilotClientFactory>? copilotRuntime = null)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var studioDefault = false;
        var concierge = false;
        var newProject = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--concierge") { concierge = true; continue; }
            if (args[i] == "--new-project")
            {
                if (newProject) throw new InvalidOperationException("Duplicate new-project option.");
                newProject = true; continue;
            }
            if (args[i] == "--studio-default-model")
            {
                if (studioDefault) throw new InvalidOperationException("Duplicate Studio-default model option.");
                studioDefault = true;
                continue;
            }
            if (args[i] is not ("--authoring" or "--state-root" or "--model" or "--voice-config" or "--media-config" or "--source-config")
                || i + 1 == args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal)
                || !values.TryAdd(args[i], args[++i]))
                throw new InvalidOperationException("Unknown, duplicate or incomplete argument. Use --state-root, --media-config, "
                    + "--configure <command> or --diagnostics list.");
        }
        var mode = values.GetValueOrDefault("--authoring", "fixture");
        if (mode is not ("fixture" or "agent"))
            throw new InvalidOperationException("--authoring must be fixture or agent; no fallback is permitted.");
        var state = values.GetValueOrDefault("--state-root");
        if (state is not null && !System.IO.Path.IsPathFullyQualified(state))
            throw new InvalidOperationException("Use an absolute --state-root.");
        CopilotAuthoringSettings? authoring = null;
        if (mode == "agent")
        {
            string Required(string name) => values.GetValueOrDefault(name)
                ?? throw new InvalidOperationException("Agent authoring requires " + name + ".");
            _ = Required("--state-root");
            if (studioDefault && values.ContainsKey("--model"))
                throw new InvalidOperationException("Choose --model or --studio-default-model, never both.");
            var clients = (copilotRuntime ?? DefaultCopilotRuntime)();
            authoring = new(studioDefault ? null : values.GetValueOrDefault("--model") ?? clients.Settings.Agents.Copilot.Model
                ?? Required("--model"), studioDefault, concierge) { Clients = clients };
            authoring.Validate();
        }
        else if (concierge || studioDefault || values.ContainsKey("--model"))
            throw new InvalidOperationException("Agent authoring settings require explicit --authoring agent.");
        var voiceConfig = values.GetValueOrDefault("--voice-config");
        if (voiceConfig is not null && !System.IO.Path.IsPathFullyQualified(voiceConfig))
            throw new InvalidOperationException("Voice requires an explicit absolute local speech configuration.");
        var mediaConfig = values.GetValueOrDefault("--media-config");
        if (mediaConfig is not null && (state is null || !System.IO.Path.IsPathFullyQualified(mediaConfig)))
            throw new InvalidOperationException("Media production requires its explicit absolute configuration and state root.");
        var sourceConfig = values.GetValueOrDefault("--source-config");
        if (sourceConfig is not null && (state is null || !System.IO.Path.IsPathFullyQualified(sourceConfig)))
            throw new InvalidOperationException("Optional OCR needs its explicit absolute source configuration and state root.");
        return new(state, authoring, voiceConfig, mediaConfig, sourceConfig, newProject);
    }

    /// <summary>The Copilot runtime from the saved provider settings (defaults when none are saved). Nothing starts until first use.</summary>
    public static MediaStudio.Agents.Copilot.CopilotClientFactory DefaultCopilotRuntime() =>
        new(new MediaStudio.Configuration.ProviderSettingsStore().TryLoad() ?? new MediaStudio.Configuration.ProviderSettings());
}
