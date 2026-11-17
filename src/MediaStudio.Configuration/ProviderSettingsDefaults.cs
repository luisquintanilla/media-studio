namespace MediaStudio.Configuration;

/// <summary>Builds the shipped local-first profile (§3.1) and the Azure additions written by <c>configure azd</c> / the wizard.</summary>
public static class ProviderSettingsDefaults
{
    public const string LocalConnectionId = "local";
    public const string AzureConnectionId = "azure";
    public const string CopilotConnectionId = "copilot";
    public static readonly Uri OllamaEndpoint = new("http://localhost:11434/v1");

    /// <summary>Local-only profile: Ollama tiers fast/balanced, local vision + embeddings, Kokoro, Whisper, Julia, SVG illustrations.</summary>
    public static ProviderSettings Local(HardwareTier tier, ModelManifest manifest, Uri? localEndpoint = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var models = manifest.GetTier(tier);
        var whisperModel = models.WhisperAsset == "whisper-base" ? "base" : "large-v3-turbo";
        return new ProviderSettings {
            Privacy = new() { AllowCloud = false },
            Connections =
            [
                new ConnectionSettings { Id = LocalConnectionId, Kind = ConnectionKind.OpenAICompatible, Endpoint = (localEndpoint ?? OllamaEndpoint).AbsoluteUri.TrimEnd('/'), Auth = AuthMode.None }
            ],
            Chat = new ChatSettings {
                Tiers =
                [
                    new ChatTier { Name = ChatTierNames.Fast, Connection = LocalConnectionId, Model = models.Fast },
                    new ChatTier { Name = ChatTierNames.Balanced, Connection = LocalConnectionId, Model = models.Balanced }
                ]
            },
            Vision = new RoleModel { Connection = LocalConnectionId, Model = models.Vision },
            Embeddings = new RoleModel { Connection = LocalConnectionId, Model = models.Embedding },
            Documents = new DocumentSettings { Candidates = [DocumentCandidates.PdfPig, DocumentCandidates.Vlm] },
            Speech = new SpeechSettings { Stt = new SttSettings { Model = whisperModel } }
        };
    }

    /// <summary>
    /// Adds (or replaces) the <c>azure</c> Foundry connection and its cloud/strong tiers, keeps local tiers first,
    /// adds the cloud document candidates and turns on <c>allowCloud</c>. Speech stays local unless <paramref name="cloudSpeech"/>.
    /// </summary>
    public static ProviderSettings WithAzure(
        ProviderSettings settings,
        ConnectionSettings azure,
        IReadOnlyDictionary<string, string>? deployments = null,
        bool allowCloud = true,
        bool cloudSpeech = false,
        bool documentIntelligence = true,
        bool contentUnderstanding = false,
        bool mistralOcr = false,
        bool claude = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(azure);
        string Deployment(string role) => deployments is not null && deployments.TryGetValue(role, out var name) && !string.IsNullOrWhiteSpace(name) ? name : role;
        bool Has(string role) => deployments is null || deployments.ContainsKey(role);

        var connections = settings.Connections.Where(c => c.Id != azure.Id).Append(azure).ToArray();
        var tiers = settings.Chat.Tiers
            .Where(t => t.Connection != azure.Id && t.Name is not (ChatTierNames.Cloud or ChatTierNames.Strong))
            .ToList();
        if (Has("chat")) tiers.Add(new ChatTier { Name = ChatTierNames.Cloud, Connection = azure.Id, Model = Deployment("chat"), Api = ChatApi.Responses });
        if (Has("authoring")) tiers.Add(new ChatTier { Name = ChatTierNames.Strong, Connection = azure.Id, Model = Deployment("authoring"), Api = ChatApi.Responses });
        else if (claude && Has("claude")) tiers.Add(new ChatTier { Name = ChatTierNames.Strong, Connection = azure.Id, Model = Deployment("claude"), Api = ChatApi.Anthropic });

        var candidates = settings.Documents.Candidates.Where(c => !DocumentCandidates.IsCloud(c)).ToList();
        if (documentIntelligence) candidates.Add(DocumentCandidates.DocumentIntelligence);
        if (contentUnderstanding) candidates.Add(DocumentCandidates.ContentUnderstanding);
        if (mistralOcr && Has("ocr")) candidates.Add(DocumentCandidates.Mistral);

        var result = settings with {
            Privacy = settings.Privacy with { AllowCloud = allowCloud || settings.Privacy.AllowCloud },
            Connections = connections,
            Chat = settings.Chat with { Tiers = [.. tiers] },
            Embeddings = settings.Embeddings ?? (Has("embedding") ? new RoleModel { Connection = azure.Id, Model = Deployment("embedding") } : null),
            Documents = settings.Documents with { Candidates = [.. candidates.Distinct()], MistralDeployment = Deployment("ocr") },
            Images = settings.Images with { Deployment = Deployment("image") },
            Decisions = settings.Decisions with { CloudDeployment = Deployment("chat") }
        };
        return cloudSpeech ? WithCloudSpeech(result, true) : result;
    }

    /// <summary>Switches narration/transcription between local (Kokoro/Whisper) and Azure Speech (MAI), keeping everything else.</summary>
    public static ProviderSettings WithCloudSpeech(ProviderSettings settings, bool enabled, string? voice = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var azure = settings.Connections.FirstOrDefault(c => c.Kind == ConnectionKind.Foundry);
        if (!enabled || azure is null)
        {
            var whisper = settings.Speech.Stt.Provider == SttProvider.Whisper ? settings.Speech.Stt.Model : "large-v3-turbo";
            return settings with { Speech = settings.Speech with { Tts = new TtsSettings(), Stt = new SttSettings { Model = whisper } } };
        }
        return settings with {
            Speech = settings.Speech with {
                Tts = new TtsSettings { Provider = TtsProvider.AzureSpeech, Connection = azure.Id, Voice = voice ?? ModelManifestVoices.DefaultVoice, Style = "narrator", Quality = null },
                Stt = new SttSettings { Provider = SttProvider.AzureSpeech, Connection = azure.Id, Model = "MAI-Transcribe-2", Style = "clean" }
            }
        };
    }

    /// <summary>
    /// Sets the whole GitHub Copilot configuration (the wizard step and <c>configure copilot</c>). <paramref name="tier"/>
    /// adds the <c>copilot</c> chat tier (and its connection) ahead of any <c>strong</c> tier, because the user chose it;
    /// <paramref name="authoringRuntime"/>/<paramref name="illustrationRuntime"/> switch those agents to the Copilot
    /// runtime (otherwise MAF). <paramref name="byokConnection"/> selects BYOK on that connection (no GitHub sign-in);
    /// null selects the subscription backend. Privacy is never changed here: the subscription backend still needs
    /// <c>privacy.allowCloud</c>, which the validator enforces.
    /// </summary>
    public static ProviderSettings WithCopilot(ProviderSettings settings, string? model = null, bool tier = false,
        bool authoringRuntime = false, bool illustrationRuntime = false, string? byokConnection = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        model = string.IsNullOrWhiteSpace(model) ? settings.Agents.Copilot.Model : model.Trim();
        var tiers = settings.Chat.Tiers.Where(t => t.Name != ChatTierNames.Copilot).ToList();
        var connections = settings.Connections.Where(c => c.Kind != ConnectionKind.GitHubCopilot).ToList();
        if (tier)
        {
            if (model is null) throw new ArgumentException("The Copilot tier needs a model. Pick one from your Copilot model list (--model <id>).");
            var existing = settings.Connections.FirstOrDefault(c => c.Kind == ConnectionKind.GitHubCopilot);
            connections.Add(existing ?? new ConnectionSettings { Id = CopilotConnectionId, Kind = ConnectionKind.GitHubCopilot, Auth = AuthMode.LoggedInUser });
            var strong = tiers.FindIndex(t => t.Name == ChatTierNames.Strong);
            tiers.Insert(strong < 0 ? tiers.Count : strong,
                new ChatTier { Name = ChatTierNames.Copilot, Connection = existing?.Id ?? CopilotConnectionId, Model = model });
        }
        var runtime = (bool copilot) => new AgentRuntimeSettings { Runtime = copilot ? AgentRuntime.Copilot : AgentRuntime.Maf };
        return settings with {
            Connections = [.. connections],
            Chat = settings.Chat with { Tiers = [.. tiers] },
            Agents = settings.Agents with {
                Authoring = runtime(authoringRuntime),
                Illustration = runtime(illustrationRuntime),
                Copilot = new CopilotAgentSettings {
                    Backend = byokConnection is null ? CopilotBackend.Subscription : CopilotBackend.Byok,
                    Model = model,
                    ByokConnection = byokConnection
                }
            }
        };
    }

    /// <summary>"Sign out" / <c>configure copilot --off</c>: removes the Copilot tier and connection and returns agents to MAF.</summary>
    public static ProviderSettings WithoutCopilot(ProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var cleared = WithCopilot(settings);
        return cleared with { Agents = cleared.Agents with { Copilot = new CopilotAgentSettings() } };
    }

    /// <summary>True when any role uses GitHub Copilot (a <c>copilot</c> tier or the Copilot agent runtime).</summary>
    public static bool UsesCopilot(ProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.Chat.Tiers.Any(t => settings.FindConnection(t.Connection)?.Kind == ConnectionKind.GitHubCopilot)
               || settings.Agents.Authoring.Runtime == AgentRuntime.Copilot
               || settings.Agents.Illustration.Runtime == AgentRuntime.Copilot;
    }
}

internal static class ModelManifestVoices
{
    public const string DefaultVoice = "en-US-Harper:MAI-Voice-2.1-Flash";
}
