using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AGUI.Abstractions;
using AGUI.Client;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Components.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using MediaStudio.Audio;
using MediaStudio.Sources;
using VideoProduction;
using VideoProduction.Providers;

namespace MediaStudio.App;

public enum MediaView { Create, Review, Jobs, Projects }

public sealed class MediaWorkspace : IAsyncDisposable
{
    public string StateRoot { get; }
    public FileProductionStore Production { get; }
    public MediaProjectStore Projects { get; }
    public MediaDraftStore Drafts { get; }
    public MediaDraftState Draft { get; private set; }
    public MediaHistory HistoryStore { get; }
    public MediaCoverStore Covers { get; }
    public Dictionary<string, string> CoverImages { get; } = new(StringComparer.Ordinal);
    public bool CoverGenerating { get; private set; }
    public bool GenerateLibraryCover { get; private set; } = true;
    public string? CoverSetupError { get; private set; }
    public string CoverReviewLabel => CoverSetupError ?? (reviewedConfiguration?.Cover is { } cover
        ? $"{cover.Connection.Model} · One 640×360 image request using the title only. Existing covers are reused."
        : "Library cover not included in this job");
    public MediaHistoryProjection History { get; private set; } = new([], [], []);
    public event Action? Changed;
    public List<string> AuthoringEvents { get; } = [];
    public string AgentText { get; private set; } = "";
    public string? SelectedItemId => Draft.SelectedItemId;
    public bool SavedMatchesDraft => Saved is not null
        && Files.Fingerprint(Working) == Files.Fingerprint(Saved.Content)
        && File.Exists(Path.Combine(StateRoot, "projects", Saved.Id, $"r{Saved.Revision}.authoring.json"))
        && Json.Read<MediaDraftState>(Path.Combine(StateRoot, "projects", Saved.Id, $"r{Saved.Revision}.authoring.json")).ContentSha256 == Draft.ContentSha256;
    public DocumentSourceStore Sources { get; private set; }
    public bool SourcesBusy { get; private set; }
    public string SourceStatus { get; private set; } = "";
    private CancellationTokenSource? sourceCancellation;
    private TaskCompletionSource? sourceCompletion;
    private IChatClient? ocrChat;
    private string? rememberedSourceHash;
    private MediaSourceSettings? sourceSettings;
    public string? OcrConfirmation { get; private set; }
    public DocumentSource[] SourceList => Draft.Frame.Sources.Select(s =>
    {
        var source = Sources.Read(s.Id);
        Guard.Require(source.OriginalSha256 == s.OriginalSha256 && source.ProjectionSha256 == s.ProjectionSha256
            && Sources.ManifestHash(s.Id) == s.ManifestSha256, "source.identity", "The supplied draft source identity changed.");
        return source;
    }).ToArray();
    public bool AuthorConfigured => agentBackend is not null;
    public CopilotAuthoringAgent? Authoring => author;
    public string? Confirmation { get; private set; }
    public bool DurationRiskAccepted { get; private set; }
    public MediaNarrationEstimate[] NarrationEstimates => MediaDraftRules.EstimateNarration(Working);
    public bool EstimatedDurationOverrun => NarrationEstimates.Any(e => e.Seconds > Working.MaximumSeconds);
    public MediaProject? Saved { get; private set; }
    public MediaProjectContent Working
    {
        get => Draft.Frame.Content;
        set
        {
            if (Busy || SourcesBusy) throw new VideoException("draft.busy", "Wait for the draft/source transaction or cancel it.");
            var ids = Draft.Frame.SceneIds.Take(value.Storyboard?.Scenes.Length ?? 0).ToList();
            while (ids.Count < (value.Storyboard?.Scenes.Length ?? 0)) ids.Add("scene-" + Guid.NewGuid().ToString("N"));
            var remaining = ids.Concat(value.AudioScript?.Select(p => p.Id) ?? []).ToHashSet();
            Draft = Drafts.Edit(Draft, Draft.Frame with {
                Content = value,
                SceneIds = ids.ToArray(),
                SelectedItemId = Draft.Frame.SelectedItemId is { } selected && remaining.Contains(selected) ? selected : remaining.FirstOrDefault(),
                ItemTargets = Draft.Frame.ItemTargets.Where(t => remaining.Contains(t.Key)).ToDictionary(),
                Citations = Draft.Frame.Citations.Where(r => remaining.Contains(r.ItemId)).ToArray()
            }, "Edited project content");
            Confirmation = null; DurationRiskAccepted = false;
        }
    }
    public MediaProposal? Proposal { get; private set; }
    public MediaView View { get; set; }
    public FileAttemptStatus? Status { get; private set; }
    public MediaOutputReport? OutputReport { get; private set; }
    public MediaVerifiedArtifacts? VerifiedOutputs { get; private set; }
    public VoiceInput Voice { get; }
    public ModelProviders Providers { get; }
    public bool Busy { get; private set; }
    public string? Error { get; set; }
    public AuthoringFailure? AuthoringDiagnostic { get; private set; }
    public string AuthoringRequest { get; set; } = "";
    public string ProviderMode => author is null ? "Direct editing; no authoring model configured."
        : $"{author.Name} · {author.ConfiguredModel}; online only when requested.";
    public string GenerationProviderLabel => reviewedConfiguration is null ? "Not configured"
        : reviewedConfiguration.Verification is not null ? "Offline synthetic fixture · not real speech or live rendering"
        : $"{reviewedConfiguration.Audio?.Identity.Provider ?? "Pinned local video runtime"}"
            + (reviewedConfiguration.Audio is { } audio ? $" · voice {audio.Speech.Voice} · {audio.Speech.Language}" : "")
            + (reviewedConfiguration.Video?.Illustration is { } images && Working.Storyboard?.Scenes.Any(s => s.IllustrationPrompt is not null) == true
                ? images.NativeImage ? $" · cloud image generation: {images.Connection.Model}; additional cost"
                    : images.Connection.AllowRemote ? $" · cloud SVG illustrations: {images.Connection.Model}; additional cost"
                    : $" · local SVG illustrations: {images.Connection.Model}"
            : reviewedConfiguration.Video?.RoutedIllustration is not null && Working.Storyboard?.Scenes.Any(s => s.IllustrationPrompt is not null) == true
                ? $" · illustrations routed by settings ({RouteLabel})" : "");
    public bool ProductionConfigured => baseConfigurationFile is not null;
    public string Setup => reviewedConfiguration is null ? "No production configuration. Saving, editing and reopening are available; generation is disabled."
        : reviewedConfiguration.Verification is not null
            ? $"OFFLINE SYNTHETIC VERIFICATION ONLY: two-tone PCM, not TTS speech; FFmpeg-encoded authored cards, not live renderer/provider quality. Output set {Working.Outputs}; maximum {Working.MaximumSeconds}s; 16000Hz PCM16. No models or network. Configuration {Files.Fingerprint(reviewedConfiguration)}."
            : $"Output set {Working.Outputs}; maximum {Working.MaximumSeconds}s. Audio provider {reviewedConfiguration.Audio?.Identity.Provider ?? "not configured"}, voice {reviewedConfiguration.Audio?.Speech.Voice ?? "not configured"}, language {reviewedConfiguration.Audio?.Speech.Language}, speed {reviewedConfiguration.Audio?.Speech.Speed}, sample rate {reviewedConfiguration.Audio?.SampleRate}. Video runtime {(reviewedConfiguration.Video is null ? "not configured" : "explicit pinned tools/font/speech")}. Exact configuration {Files.Fingerprint(reviewedConfiguration)}.";
    public bool CanGenerate => configurationFile is not null && Saved is not null && Proposal is null && !Busy && !SourcesBusy
        && SavedMatchesDraft && Status is null && !Voice.InputPending;
    private string? configurationFile;
    private string? baseConfigurationFile;
    private readonly Func<DefaultProductionResult>? defaultProduction;
    /// <summary>Why the derived default production setup cannot generate yet, with the command that fixes it; null when ready or explicit.</summary>
    public string? ProductionProblem { get; private set; }
    private CopilotAuthoringAgent? author;
    private AIAgent? agentBackend;
    private readonly CopilotAuthoringSettings? originalAuthoringSettings;
    private AGUIChatClient? agentClient;
    private MediaProductionConfiguration? reviewedConfiguration;
    private CancellationTokenSource? authoring;
    private MediaJudgements? judgements;
    public bool JudgementsConfigured => judgements is not null;
    public VisualSuggestion? VisualSuggestion { get; private set; }
    public TranscriptTriage? AdvisoryTriage { get; private set; }
    public MediaPendingOperations? Pending => Saved is null || reviewedConfiguration is null
        || Saved.Content.Outputs != MediaOutput.Video && Saved.Content.AudioScript is null
        || Saved.Content.Outputs != MediaOutput.Audio && Saved.Content.Storyboard is null
            ? null : MediaApprovals.Pending(Saved, reviewedConfiguration);

    public MediaWorkspace(string stateRoot, string? configurationFile, CopilotAuthoringSettings? settings, string? voiceConfig,
        AIAgent? fixtureAuthor = null, string? sourceConfiguration = null, bool newProject = false,
        System.ClientModel.Primitives.PipelineTransport? chatTransport = null, Func<DefaultProductionResult>? defaultProduction = null)
    {
        StateRoot = Path.GetFullPath(stateRoot);
        Providers = new(StateRoot, chatTransport);
        originalAuthoringSettings = settings;
        Production = new(StateRoot);
        Projects = new(Production);
        Drafts = new(Production);
        HistoryStore = new(Production, Projects);
        Covers = new(Production);
        Draft = !newProject && Drafts.Active() is { } retained ? Drafts.Recover(retained.DraftId) : Drafts.New(Blank());
        Drafts.Activate(Draft.DraftId);
        Saved = Draft.ProjectId is { } projectId ? Projects.Read(projectId, Draft.BaseRevision) : null;
        var rememberedSource = Path.Combine(StateRoot, "settings", "source-provider.json");
        rememberedSourceHash = File.Exists(rememberedSource) ? Files.Hash(rememberedSource) : null;
        if ((sourceConfiguration ?? (File.Exists(rememberedSource) ? rememberedSource : null)) is { } sourceConfig)
        {
            var config = PrivateFiles.Read<MediaSourceSettings>(sourceConfig);
            (Sources, ocrChat) = config.Open(Path.Combine(StateRoot, "sources"));
            sourceSettings = config;
        }
        else Sources = new(Path.Combine(StateRoot, "sources"));
        this.defaultProduction = configurationFile is null ? defaultProduction : null;
        if (configurationFile is not null) AdoptConfiguration(configurationFile);
        else RefreshDefaultProduction();
        if (judgements is null && Providers.NativeDecisions() is { } selectedDecisions) judgements = new(selectedDecisions);
        Voice = new(StateRoot, voiceConfig);
        if (voiceConfig is null && Providers.State.SpeechUnassigned) Voice.ClearCloud();
        else if (voiceConfig is null && Providers.State.Speech is { } speech)
            Voice.ConfigureCloud(Providers.Realtime(speech), speech.Model, Providers.Connection(speech).Name, Providers.SpeechUploadAllowed);
        hasFixtureAuthor = fixtureAuthor is not null;
        if (fixtureAuthor is null && Providers.State.RoutedAuthoring) { }
        else if (fixtureAuthor is null && Providers.State.Authoring is { } model)
            author = new(new(model.Model, Concierge: true), draftTool: new(),
                chatClient: Providers.Chat(model), providerName: Providers.Connection(model).Name);
        else if (settings is not null) author = new(settings, draftTool: new());
        agentBackend = fixtureAuthor ?? author;
    }

    public async Task SelectAuthoringModelAsync(ModelSelection? selection)
    {
        await ConfigureModelsAsync(Providers.State with { Authoring = selection, RoutedAuthoring = false }, changeSpeech: false);
    }

    private readonly bool hasFixtureAuthor;
    private IServiceProvider? services;
    private MediaStudio.Configuration.ProviderSettings? providerSettings;
    private string? routeLabel;
    /// <summary>
    /// Attaches the app's DI container so the "routed" authoring and illustration choices resolve the keyed
    /// <c>"authoring"</c> / <c>"illustration"</c> clients registered by <c>AddMediaStudioCopilot</c> + <c>AddMediaStudioAI</c>.
    /// </summary>
    public void AttachServices(IServiceProvider serviceProvider, MediaStudio.Configuration.ProviderSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        services = serviceProvider;
        providerSettings = settings;
        routeLabel = settings is null ? null
            : settings.Agents.Illustration.Runtime == MediaStudio.Configuration.AgentRuntime.Copilot ? "GitHub Copilot"
            : string.Join(", ", settings.Chat.Tiers.Select(t => t.Name));
        if (!hasFixtureAuthor && Providers.State.RoutedAuthoring && author is null && RoutedAuthor() is { } routed)
        {
            author = routed; agentBackend = author;
        }
        Changed?.Invoke();
    }
    public bool RoutedAuthoringAvailable => Routed(MediaStudioAIKeys.Authoring);
    public bool RoutedIllustrationAvailable => Routed(MediaStudioAIKeys.Illustration);
    public string RouteLabel => string.IsNullOrEmpty(routeLabel) ? "settings tiers" : routeLabel;
    private bool Routed(string key) =>
        services?.GetService<IServiceProviderIsKeyedService>()?.IsKeyedService(typeof(IChatClient), key) == true;
    private IChatClient? RoutedChat(string key) =>
        services?.GetKeyedService<IChatClient>(key) is { } client ? new SharedChatClient(client) : null;
    private CopilotAuthoringAgent? RoutedAuthor() => RoutedChat(MediaStudioAIKeys.Authoring) is { } chat
        ? AuthoringAgentFactory.CreateMaf(chat, MediaStudioAIKeys.Authoring, "Settings routing · " + RouteLabel) : null;
    public Task SelectRoutedAuthoringAsync()
    {
        if (!RoutedAuthoringAvailable) throw new InvalidOperationException("Configure chat tiers in settings before choosing routed authoring.");
        return ConfigureModelsAsync(Providers.State with { RoutedAuthoring = true }, changeSpeech: false);
    }

    public string IllustrationSelection => Providers.State.NativeImages ? "native"
        : Providers.State.RoutedIllustration ? "routed"
        : Providers.State.ChatIllustration is { } chat ? $"chat|{chat.Connection}|{chat.Model}"
        : Providers.State.LocalImage is not null ? "local" : "existing";
    public Task SelectIllustrationsAsync(string selection)
    {
        if (selection == "local" && Providers.State.LocalImage is null)
            throw new InvalidOperationException("Configure local illustrations before choosing Local. Your saved setup may use a cloud model; it is not a local fallback.");
        var parts = selection.Split('|');
        var state = selection switch {
            "local" => Providers.State with { NativeImages = false, ChatIllustration = null, RoutedIllustration = false },
            "existing" when Providers.State.LocalImage is null => Providers.State with { NativeImages = false, ChatIllustration = null, RoutedIllustration = false },
            "native" => Providers.State with { NativeImages = true, RoutedIllustration = false },
            "routed" when RoutedIllustrationAvailable => Providers.State with { NativeImages = false, ChatIllustration = null, RoutedIllustration = true },
            _ when parts is ["chat", _, _] => Providers.State with { NativeImages = false, ChatIllustration = new(parts[1], parts[2]), RoutedIllustration = false },
            _ => throw new InvalidDataException("Choose a configured illustration model or local setup.")
        };
        return ConfigureModelsAsync(state, changeSpeech: false);
    }

    public void SelectSpeechProvider(string provider)
    {
        if (Busy || SourcesBusy || Voice.InputPending)
            throw new InvalidOperationException("Finish pending authoring/source/voice work before changing speech providers.");
        if (provider is "Whisper")
        {
            Voice.SelectProvider(provider);
            Providers.Save(Providers.State with { Speech = null, SpeechUnassigned = false });
        }
        else if (provider == "off")
        {
            Providers.Save(Providers.State with { Speech = null, SpeechUnassigned = true });
            Voice.ClearCloud();
        }
        else
        {
            var parts = provider.Split('|');
            if (parts.Length != 2) throw new InvalidDataException("Choose an exact configured realtime transcription deployment.");
            var selection = new ModelSelection(parts[0], parts[1]);
            if (Providers.Connection(selection).SpeechModels?.Contains(selection.Model) != true)
                throw new InvalidDataException("This deployment is not configured for realtime transcription.");
            var client = Providers.Realtime(selection);
            var state = Providers.State with { Speech = selection, SpeechUnassigned = false };
            try { Providers.Save(state); }
            catch { client.Dispose(); throw; }
            Voice.ConfigureCloud(client, selection.Model, Providers.Connection(selection).Name, Providers.SpeechUploadAllowed);
        }
        Changed?.Invoke();
    }

#pragma warning disable MEAI001
    public async Task ConfigureModelsAsync(ModelProviderState state, bool changeSpeech = true)
    {
        if (Busy || SourcesBusy || Voice.InputPending)
            throw new InvalidOperationException("Finish pending work before configuring models.");
        if (!changeSpeech && ModelProviders.SpeechIdentity(state) != ModelProviders.SpeechIdentity(Providers.State))
            throw new InvalidDataException("Changing the speech connection requires an explicit speech setup selection.");
        state = state with { SpeechUnassigned = state.Speech is null && (state.SpeechUnassigned || Providers.State.Speech is not null) };
        Busy = true; Changed?.Invoke();
        CopilotAuthoringAgent? replacement = null;
        IRealtimeClient? speechClient = null;
        MediaJudgements? replacementJudgements = null;
        var installed = false;
        try
        {
            replacement = state.RoutedAuthoring ? RoutedAuthor() ?? throw new InvalidOperationException("Routed authoring needs chat tiers configured in settings.")
                : state.Authoring is { } model
                ? AuthoringAgentFactory.CreateMaf(Providers.Chat(model, state), model.Model,
                    state.Connections.Single(c => c.Id == model.Connection).Name)
                : originalAuthoringSettings is null ? null : AuthoringAgentFactory.CreateCopilot(originalAuthoringSettings);
            if (changeSpeech && state.Speech is { } speech) speechClient = Providers.Realtime(speech, state);
            var decisions = Providers.NativeDecisions(state)
                ?? (baseConfigurationFile is null ? null : PrivateFiles.Read<MediaProductionConfiguration>(baseConfigurationFile).Decisions);
            if (decisions is not null) replacementJudgements = new(decisions);
            Providers.Save(state);
            Confirmation = null; DurationRiskAccepted = false; VisualSuggestion = null; AdvisoryTriage = null;
            var previous = author;
            agentClient?.Dispose(); agentClient = null;
            author = replacement; agentBackend = author; installed = true;
            var previousJudgements = judgements;
            judgements = replacementJudgements; replacementJudgements = null;
            previousJudgements?.Dispose();
            if (speechClient is not null && state.Speech is { } selectedSpeech)
            {
                Voice.ConfigureCloud(speechClient, selectedSpeech.Model,
                    state.Connections.Single(c => c.Id == selectedSpeech.Connection).Name, Providers.SpeechUploadAllowed);
                speechClient = null;
            }
            else if (changeSpeech && state.Speech is null && (Voice.IsCloud || state.SpeechUnassigned)) Voice.ClearCloud();
            if (previous is not null) await previous.DisposeAsync();
        }
        finally
        {
            try { if (!installed && replacement is not null) await replacement.DisposeAsync(); }
            finally { speechClient?.Dispose(); replacementJudgements?.Dispose(); Busy = false; Changed?.Invoke(); }
        }
    }
#pragma warning restore MEAI001

    private static MediaProjectContent Blank() => new("Untitled explanation", "", "", "", MediaOutput.Video, 300, null, null);
    public void New()
    {
        if (Voice.InputPending) throw new InvalidOperationException("Finish or discard local dictation before starting another project.");
        if (Busy || SourcesBusy) throw new InvalidOperationException("Finish or cancel authoring/source reading before opening a new project.");
        Saved = null; Draft = Drafts.New(Blank()); Confirmation = null; Proposal = null; VisualSuggestion = null; AdvisoryTriage = null;
        Status = null; OutputReport = null; VerifiedOutputs = null; Error = null; View = MediaView.Create;
        AuthoringDiagnostic = null; OcrConfirmation = null; DurationRiskAccepted = false; CoverSetupError = null;
        Drafts.Activate(Draft.DraftId);
        AgentText = ""; AuthoringRequest = ""; AuthoringEvents.Clear(); SourceStatus = "";
    }
    public void Open(MediaProject project)
    {
        if (Voice.InputPending) throw new InvalidOperationException("Finish or discard local dictation before switching projects.");
        if (Busy || SourcesBusy) throw new InvalidOperationException("Finish or cancel authoring/source reading before opening another project.");
        Saved = Projects.Read(project.Id, project.Revision);
        Draft = Drafts.List().Where(d => d.ProjectId == project.Id && d.BaseRevision == project.Revision).LastOrDefault() is { } retained
            ? Drafts.Recover(retained.DraftId) : Drafts.New(Saved.Content, Saved);
        Confirmation = null;
        Proposal = null; Error = null; Refresh(); View = MediaView.Review;
        AuthoringDiagnostic = null; OcrConfirmation = null; DurationRiskAccepted = false;
        VisualSuggestion = null; AdvisoryTriage = null;
        Drafts.Activate(Draft.DraftId);
        AgentText = ""; AuthoringRequest = ""; AuthoringEvents.Clear(); SourceStatus = "";
    }
    public void OpenDraft(string id)
    {
        if (Voice.InputPending) throw new InvalidOperationException("Finish or discard local dictation before switching drafts.");
        if (Busy || SourcesBusy) throw new InvalidOperationException("Finish or cancel the active edit/source operation before switching drafts.");
        Draft = Drafts.Recover(id); Drafts.Activate(id);
        Saved = Draft.ProjectId is { } projectId ? Projects.Read(projectId, Draft.BaseRevision) : null;
        Confirmation = null; Proposal = null; Error = null; AgentText = ""; AuthoringRequest = ""; AuthoringEvents.Clear(); SourceStatus = "";
        AuthoringDiagnostic = null; OcrConfirmation = null; DurationRiskAccepted = false;
        VisualSuggestion = null; AdvisoryTriage = null;
        Refresh(); View = MediaView.Review; Changed?.Invoke();
    }
    public void Save()
    {
        if (Busy || SourcesBusy || Proposal is not null) throw new InvalidOperationException("Finish draft/source reading before saving working edits.");
        if (SavedMatchesDraft) return;
        var flushed = MediaDraftRules.ProductionContent(Draft.Frame);
        if (Files.Fingerprint(flushed) != Files.Fingerprint(Working)) Working = flushed;
        Saved = Projects.Save(Saved?.Id, Saved?.Revision ?? 0, Working);
        Draft = Drafts.BindSaved(Draft, Saved);
        Confirmation = null; DurationRiskAccepted = false;
        Refresh();
    }
    public void Refresh()
    {
        VerifiedOutputs = null;
        Status = Saved is null ? null : Projects.Submission(Saved, observeOnly: true);
        OutputReport = null;
        if (Status is { } status)
        {
            VerifiedOutputs = Production.MediaArtifacts(status.AttemptId, observeOnly: true);
            var path = Path.Combine(StateRoot, "attempts", status.AttemptId, "output-outcomes.json");
            if (File.Exists(path))
            {
                var report = Json.Read<MediaOutputReport>(path);
                Guard.Require(report.SchemaVersion == 1 && report.ProjectId == Saved!.Id
                    && report.Revision == Saved.Revision && report.ContentSha256 == Files.Fingerprint(Saved.Content),
                    "media.outcomes", "Per-output outcomes do not bind the selected saved revision.");
                OutputReport = report;
            }
        }
    }
    public void EditFrame(MediaDraftFrame frame, string summary, string? coalesceKey = null)
    {
        if (Busy || SourcesBusy) throw new VideoException("draft.busy", "Wait for authoring/source reading or cancel it before editing.");
        Draft = Drafts.Edit(Draft, frame, summary, coalesceKey: coalesceKey); Confirmation = null; DurationRiskAccepted = false; Changed?.Invoke();
    }
    public void Mutate(MediaDraftOperation[] operations, string summary, string? coalesceKey = null) =>
        EditFrame(MediaDraftRules.Apply(Draft.Frame, operations), summary, coalesceKey);
    public void Undo(bool redo = false)
    {
        if (Busy || SourcesBusy) throw new VideoException("draft.busy", "Finish or cancel authoring/source reading before Undo.");
        Draft = Drafts.Undo(Draft, redo); Confirmation = null; DurationRiskAccepted = false; Changed?.Invoke();
    }
    public void SelectItem(string id)
    {
        Guard.Require(Draft.Frame.SceneIds.Contains(id) || Draft.Frame.Content.AudioScript?.Any(p => p.Id == id) == true,
            "draft.selection", "Select an existing scene or script part.");
        Draft = Drafts.Select(Draft, id); Changed?.Invoke();
    }
    public async Task AddSourceAsync(Stream input, string name, string? type, CancellationToken ct = default)
    {
        await SourceOperation(async token => await Sources.AddAsync(input, name, type, token), ct);
    }
    public async Task ReadSourceLinkAsync(string link)
        => await SourceOperation(async token => await new PublicDocumentFetcher().ReadAsync(Sources, link, token));
    public Task AttributeSourceAsync(string id, SourceProvenance provenance)
    {
        Guard.Require(Draft.Frame.Sources.Any(s => s.Id == id), "source.selection", "Choose a source in this draft.");
        return SourceOperation(token => Sources.AttributeAsync(id, provenance, token), replacement: true);
    }
    private async Task SourceOperation(Func<CancellationToken, Task<DocumentSource>> prepare, CancellationToken ct = default, bool replacement = false)
    {
        if (Busy || SourcesBusy || !replacement && Draft.Frame.Sources.Length >= DocumentSourceStore.MaximumSources)
            throw new SourceException("source.busy-count", "Finish the current operation and keep at most five sources.");
        var draftId = Draft.DraftId; var version = Draft.Version;
        SourcesBusy = true; SourceStatus = "Reading source…";
        sourceCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cancellation.CancelAfter(TimeSpan.FromMinutes(2));
        sourceCancellation = cancellation; Changed?.Invoke();
        try
        {
            var source = await prepare(cancellation.Token);
            Guard.Require(Draft.DraftId == draftId && Draft.Version == version, "source.stale", "The draft changed during extraction. The prepared source was not attached.");
            var reference = new MediaDraftSource(source.Id, source.OriginalSha256, source.ProjectionSha256, Sources.ManifestHash(source.Id));
            var previous = source.Supersedes;
            Draft = Drafts.Edit(Draft, Draft.Frame with {
                Sources = [.. Draft.Frame.Sources.Where(s => s.Id != previous), reference],
                Citations = Draft.Frame.Citations.Where(c => c.SourceId != previous).ToArray()
            },
                previous is null ? "Attached source " + source.Name : "Created source replacement " + source.Name);
            Confirmation = null; DurationRiskAccepted = false;
            SourceStatus = source.Projection.Chunks.Length == 0
                ? source.MediaType == "image/png" ? "Image bytes attached locally. Choose a vision reader and explicitly approve Read image for interpretation."
                    : "This PDF may need OCR; attach a searchable PDF or text version."
                : previous is not null ? "New source version ready."
                : source.Projection.Pages.Any(p => !p.HasText) ? "Source ready; some pages have no extracted text. Review coverage in Sources."
                : "Source ready.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { SourceStatus = "Source reading cancelled; no draft source was attached."; }
        catch (Exception error) { SourceStatus = "Source reading failed: " + error.Message; throw; }
        finally { SourcesBusy = false; sourceCancellation = null; sourceCompletion.TrySetResult(); Changed?.Invoke(); }
    }
    public string PrepareOcr(string id)
    {
        if (Sources.Ocr is null) throw new SourceException("source.ocr-unconfigured", "OCR is not connected; attach a searchable PDF or text version.");
        var source = Sources.Read(id);
        Guard.Require(Draft.Frame.Sources.Any(s => s.Id == id), "source.selection", "Choose a source in this draft.");
        OcrConfirmation = Files.Fingerprint(new { id, source.OriginalSha256, Draft.DraftId, Draft.Version, Sources.Ocr });
        return OcrConfirmation;
    }
    public async Task ConfirmOcrAsync(string id, string confirmation)
    {
        var expected = OcrConfirmation;
        Guard.Require(expected == confirmation && PrepareOcr(id) == confirmation, "source.ocr-stale", "The source/draft/OCR configuration changed. Review upload again.");
        OcrConfirmation = null;
        await SourceOperation(token => Sources.RunOcrAsync(id, true, token), replacement: true);
    }
    public void CancelSources() => sourceCancellation?.Cancel();
    /// <summary>
    /// Resolves the keyed document extractors registered by <c>AddMediaStudioDocuments</c> (<c>documents</c> for Auto,
    /// <c>documents:&lt;candidate&gt;</c> for explicit choices). When unset, Auto reads locally with PdfPig only.
    /// </summary>
    public Func<string, Microsoft.Extensions.DocumentExtraction.DocumentExtractionClient?>? DocumentExtractors { get; set; }
    /// <summary>Explicit extractors offered in the PDF reader list (Settings → Documents candidates other than PdfPig).</summary>
    public string[] DocumentExtractorChoices { get; set; } = [];
    public WorkspaceUploadConsent UploadConsent => uploadConsent ??= new(() => Changed?.Invoke());
    private WorkspaceUploadConsent? uploadConsent;
    public string PdfReaderSelection => sourceSettings?.Extractor is { } extractor ? extractor == MediaSourceSettings.AutoExtractor ? "auto" : "doc|" + extractor
        : sourceSettings?.NativeMistral is { } native ? native.IncludeImages ? "mistral-images" : "mistral"
        : sourceSettings?.Chat is { } chat
            ? Providers.State.Connections.FirstOrDefault(c => c.Endpoint == chat.Endpoint && c.Provider == chat.Provider && c.ChatModels?.Contains(chat.Model) == true) is { } connection
                ? $"vision|{connection.Id}|{chat.Model}" : "existing"
            : "local";
    public string PdfReaderLabel => sourceSettings?.Extractor is { } extractor
            ? extractor == MediaSourceSettings.AutoExtractor ? "Auto (recommended)" : DocumentExtractorFactory.DisplayName(extractor)
        : sourceSettings?.NativeMistral is { } native ? native.IncludeImages ? "Mistral OCR + extracted images" : "Mistral OCR"
        : sourceSettings?.Chat is { } chat ? $"Vision · {chat.Model}" : "Local PdfPig";
    public void SelectPdfReader(string selection)
    {
        if (selection == "local") { ConfigureOcr(MediaSourceSettings.Local()); return; }
        if (selection == "auto") { ConfigureOcr(MediaSourceSettings.Automatic()); return; }
        if (selection.StartsWith("doc|", StringComparison.Ordinal) && DocumentExtractorChoices.Contains(selection[4..]))
        { ConfigureOcr(MediaSourceSettings.Explicit(selection[4..])); return; }
        if (selection == "mistral") { ConfigureOcr(MediaSourceSettings.KnownMistral()); return; }
        if (selection == "mistral-images") { ConfigureOcr(MediaSourceSettings.KnownMistral(includeImages: true)); return; }
        var parts = selection.Split('|');
        if (parts is not ["vision", _, _]) throw new SourceException("source.configuration", "Choose a configured PDF reader.");
        var model = new ModelSelection(parts[1], parts[2]);
        var connection = Providers.Connection(model);
        Guard.Require(connection.ChatModels?.Contains(model.Model) == true,
            "source.configuration", "Vision reading needs an explicitly declared chat-capable model.");
        var chat = new PlannerConfiguration(connection.Endpoint, model.Model, null, AllowRemote: !connection.IsLocal,
            Authentication: connection.Authentication, Responses: !connection.IsLocal,
            ApiKeySecret: connection.Authentication == "user-secrets" ? connection.SecretName : null, Provider: connection.Provider);
        ConfigureOcr(new(1, chat, new(connection.Endpoint, model.Model, true, true,
            DocumentSourceStore.MaximumBytes, DocumentSourceStore.MaximumPages)));
    }
    public Task ReadLocalPdfAsync(string id)
    {
        Guard.Require(Sources.Ocr is null, "source.configuration", "Review cloud upload before reading with the selected remote provider.");
        if (sourceSettings?.Extractor is { } extractor)
            return SourceOperation(async token =>
            {
                var key = extractor == MediaSourceSettings.AutoExtractor
                    ? MediaStudioDocumentKeys.Documents : MediaStudioDocumentKeys.CandidatePrefix + extractor;
                if (DocumentExtractors?.Invoke(key) is { } configured) return await Sources.ReadWithAsync(id, configured, token);
                Guard.Require(extractor is MediaSourceSettings.AutoExtractor or MediaStudio.Configuration.DocumentCandidates.PdfPig, "source.configuration",
                    $"{DocumentExtractorFactory.DisplayName(extractor)} is not configured. Add it in Settings → Documents.");
                using var local = DocumentExtractorFactory.CreateRouter(new MediaStudio.Configuration.ProviderSettings(), null, null, FixedUploadConsent.Deny);
                return await Sources.ReadWithAsync(id, local, token);
            }, replacement: true);
        return SourceOperation(token => Sources.ReadLocalPdfAsync(id, token), replacement: true);
    }
    public void ConfigureOcr(MediaSourceSettings configuration)
    {
        if (Busy || SourcesBusy || Voice.InputPending) throw new SourceException("source.busy", "Finish pending work before changing PDF readers.");
        var opened = configuration.Open(Path.Combine(StateRoot, "sources"));
        try
        {
            var file = Path.Combine(StateRoot, "settings", "source-provider.json");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            PrivateFiles.Protect(Path.GetDirectoryName(file)!, directory: true);
            using var transaction = new FileStream(file + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if ((File.Exists(file) ? Files.Hash(file) : null) != rememberedSourceHash)
                throw new SourceException("source.configuration-stale", "OCR settings changed in another window. Reopen before replacing them.");
            Json.Write(file, configuration); PrivateFiles.Protect(file);
            rememberedSourceHash = Files.Hash(file);
        }
        catch { opened.Store.Dispose(); opened.Chat?.Dispose(); throw; }
        Sources.Dispose(); ocrChat?.Dispose();
        (Sources, ocrChat) = opened;
        sourceSettings = configuration;
        OcrConfirmation = null;
        Changed?.Invoke();
    }
    public void RemoveSource(string id)
    {
        if (SourcesBusy) throw new SourceException("source.busy", "Finish or cancel extraction first.");
        EditFrame(Draft.Frame with {
            Sources = Draft.Frame.Sources.Where(s => s.Id != id).ToArray(),
            Citations = Draft.Frame.Citations.Where(c => c.SourceId != id).ToArray()
        }, "Removed source from this working draft");
    }
    public void ChangeOutput(MediaOutput output)
    {
        if (Busy) throw new VideoException("draft.busy", "Finish or cancel authoring first.");
        var c = Working with {
            Outputs = output,
            Storyboard = output == MediaOutput.Audio ? null : Working.Storyboard,
            AudioScript = output == MediaOutput.Video ? null : Working.AudioScript
        };
        var ids = output == MediaOutput.Audio ? [] : Draft.Frame.SceneIds;
        var valid = ids.Concat(c.AudioScript?.Select(p => p.Id) ?? []).ToHashSet();
        EditFrame(Draft.Frame with {
            Content = c,
            SceneIds = ids,
            SelectedItemId = valid.FirstOrDefault(),
            ItemTargets = Draft.Frame.ItemTargets.Where(t => valid.Contains(t.Key)).ToDictionary(),
            Citations = Draft.Frame.Citations.Where(r => valid.Contains(r.ItemId)).ToArray()
        }, "Changed output intent");
    }
    public async Task ProposeAsync(string request)
    {
        if (Voice.InputPending) throw new InvalidOperationException("Local dictation is not yet in the prompt. Wait for it or discard it before authoring.");
        if (Busy || agentBackend is null)
            throw new InvalidOperationException("Agent not connected. Configure authoring or start manually; no model fallback was used.");
        var activeAuthor = agentBackend as CopilotAuthoringAgent;
        if (SourcesBusy) throw new SourceException("source.busy", "Finish source reading before authoring.");
        var sourceContext = Sources.Context(Draft.Frame.Sources.Select(s => s.Id), Json.Text(Draft) + request);
        Busy = true; Proposal = null; AgentText = ""; Confirmation = null; DurationRiskAccepted = false; Error = null; AuthoringDiagnostic = null;
        AuthoringEvents.Clear();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        authoring = cancellation;
        var run = "run-" + Guid.NewGuid().ToString("N");
        var thread = "thread-" + Draft.DraftId;
        Draft = Drafts.BeginRun(Draft, run, thread, request);
        RunErrorEvent? runError = null;
        var runFinished = false;
        agentClient?.Dispose();
        var transport = new MediaAgentTransport(AgentPipeline.Wrap(agentBackend, originalAuthoringSettings?.Clients?.Settings.Privacy.AllowCloud ?? false), thread, e =>
            {
                AuthoringEvents.Add(e.GetType().Name);
                if (e is RunErrorEvent failed) runError = failed;
                if (e is RunFinishedEvent finished && finished.RunId == run && finished.ThreadId == thread) runFinished = true;
            });
        agentClient = new(new AGUIChatClientOptions {
            Transport = transport,
            JsonSerializerOptions = Json.Options
        });
        var agent = new UIAgent<MediaDraftState>(agentClient, options =>
        {
            options.ChatOptions = new ChatOptions {
                RawRepresentationFactory = _ => new RunAgentInput {
                    ThreadId = thread,
                    RunId = run,
                    State = JsonSerializer.SerializeToElement(Draft, Json.Options),
                    Context = [new() { Description = "media-source-context-v1", Value = sourceContext }]
                }
            };
            options.StateMapper = context =>
            {
                if (context.Update.Text.Length > 0)
                {
                    AgentText = (AgentText + context.Update.Text);
                    Guard.Require(AgentText.Length <= 32000, "draft.agent-text", "Authoring output exceeded its bound.");
                    Changed?.Invoke();
                }
                if (context.Update.RawRepresentation is not StateSnapshotEvent snapshot) return;
                var acknowledgement = snapshot.Snapshot.Deserialize<MediaDraftAcknowledgement>(Json.Options)
                    ?? throw new JsonException("A complete media draft acknowledgment is required.");
                var next = MediaDraftRules.Apply(Draft.Frame, acknowledgement.Transaction.Operations);
                foreach (var citation in next.Citations)
                    Guard.Require(Sources.Read(citation.SourceId).Projection.Chunks.Any(c => c.SectionId == citation.SectionId),
                        "draft.citation", "Citations must name an actual supplied source section.");
                Draft = Drafts.Apply(Draft, acknowledgement.Transaction);
                context.SetState(Draft); Changed?.Invoke();
            };
        }, Draft);
        var context = new AgentContext(agent);
        try
        {
            Changed?.Invoke();
            await context.SendMessageAsync(new ChatMessage(ChatRole.User, request), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (transport.Failure is { } streamFailure)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(streamFailure).Throw();
            if (transport.ToolFailure is { } toolFailure)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(toolFailure).Throw();
            if (context.Error is { } error) throw new InvalidOperationException("Authoring failed.", error);
            if (activeAuthor?.DraftRejection is { } unresolved) throw new VideoException(unresolved.Code, unresolved.Message);
            Guard.Require(runError is null, activeAuthor?.DraftRejection?.Code ?? "draft.agent-error",
                activeAuthor?.DraftRejection?.Message ?? $"The author could not finish this request: {runError?.Message}. Completed draft changes were kept; Undo is available. No generation was started.");
            Guard.Require(runFinished, "draft.incomplete-run", "Authoring ended without its matching completion event. Completed changes were kept.");
            Draft = Drafts.FinishRun(Draft.DraftId, run, "completed");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Draft = Drafts.FinishRun(Draft.DraftId, run, "cancelled");
            Error = "Authoring cancelled. Completed changes were kept; Undo is available.";
        }
        catch (Exception error)
        {
            Draft = Drafts.FinishRun(Draft.DraftId, run, "failed");
            AgentText = "";
            AuthoringDiagnostic = AuthoringFailure.Capture(run,
                transport.ToolFailure is not null ? "draft-tool-invocation" : activeAuthor?.Stage ?? "authoring", AuthoringEvents,
                activeAuthor?.DraftRejection is { } rejected ? new VideoException(rejected.Code, rejected.Message)
                    : transport.Failure ?? error);
            var directory = Path.Combine(StateRoot, "diagnostics");
            Directory.CreateDirectory(directory); PrivateFiles.Protect(directory, directory: true);
            var file = Path.Combine(directory, "authoring-" + run + ".json");
            Json.Write(file, AuthoringDiagnostic); PrivateFiles.Protect(file);
            if (activeAuthor?.DraftRejection is { } rejection) throw new VideoException(rejection.Code, rejection.Message);
            throw new InvalidOperationException(AuthoringDiagnostic.Notice, error);
        }
        finally { authoring = null; Busy = false; Changed?.Invoke(); }
    }
    public void CancelAuthoring() => authoring?.Cancel();
    public async Task SuggestVisualAsync(int sceneIndex)
    {
        if (Busy || judgements is null || Saved is null || Proposal is not null || Working.Storyboard is null)
            throw new InvalidOperationException("Save a video storyboard, configure Decisions explicitly and finish pending proposals before requesting a visual suggestion.");
        var current = JsonSerializer.Deserialize<MediaProjectContent>(Json.Text(Working), Json.Options)!;
        var storyboard = current.Storyboard ?? throw new VideoException("decision.input", "A video storyboard is required.");
        Guard.Require(sceneIndex >= 0 && sceneIndex < storyboard.Scenes.Length,
            "decision.input", "Choose an existing scene.");
        var hash = Files.Fingerprint(current);
        var saved = Saved;
        Busy = true;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        authoring = cancellation;
        try
        {
            var suggestion = await judgements.SuggestVisual(storyboard.Scenes[sceneIndex], cancellation.Token);
            Guard.Require(Saved is { } selected && selected.Id == saved.Id && selected.Revision == saved.Revision
                && Projects.Read(saved.Id).Revision == saved.Revision && Files.Fingerprint(Working) == hash,
                "decision.stale", "Working/saved revision changed during inference; nothing was proposed or applied.");
            var scenes = storyboard.Scenes.ToArray();
            scenes[sceneIndex] = scenes[sceneIndex] with { Visual = suggestion.Treatment };
            VisualSuggestion = suggestion;
            Proposal = new(Guid.NewGuid().ToString("N"), saved.Id, saved.Revision, hash,
                current with { Storyboard = storyboard with { Scenes = scenes } }, TeachingGuidance.Identity);
        }
        finally { authoring = null; Busy = false; }
    }
    public async Task TriageAsync(TranscriptLearning observation)
    {
        if (Busy || judgements is null || Saved is null)
            throw new InvalidOperationException("Save a project and explicitly configure Decisions before advisory transcript triage.");
        Busy = true;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        authoring = cancellation;
        try { AdvisoryTriage = await judgements.Triage(observation, cancellation.Token); }
        finally { authoring = null; Busy = false; }
    }
    public void Reject() => Proposal = null;
    public void Apply()
    {
        var proposed = Proposal ?? throw new InvalidOperationException("No typed proposal is pending.");
        if (Saved is null || proposed.ProjectId != Saved.Id || proposed.BaseRevision != Saved.Revision
            || proposed.WorkingSha256 != Files.Fingerprint(Working) || proposed.Guidance != TeachingGuidance.Identity)
            throw new InvalidOperationException("The proposal is stale; no edits were applied.");
        var saved = Projects.Save(Saved.Id, Saved.Revision, proposed.Content);
        Saved = saved;
        Draft = Drafts.Edit(Draft, MediaDraftRules.FromContent(saved.Content), "Applied explicit visual proposal");
        Draft = Drafts.BindSaved(Draft, saved);
        Proposal = null; DurationRiskAccepted = false; Refresh();
    }
    public async Task ApproveAndGenerateAsync()
    {
        if (!CanGenerate) throw new InvalidOperationException("Save/review the exact current revision, reject/apply proposals and configure production before approval.");
        var configuration = PrivateFiles.Read<MediaProductionConfiguration>(configurationFile!);
        Guard.Require(reviewedConfiguration is not null
            && Files.Fingerprint(configuration) == Files.Fingerprint(reviewedConfiguration),
            "media.config-changed", "Provider/resource configuration changed since review. Explicitly review the new setup before approval.");
        Guard.Require(CoverSetupError is null && (!GenerateLibraryCover || configuration.Verification is not null || configuration.Cover is not null),
            "cover.unconfigured", "Review the cover setup or turn the cover off before generation.");
        Guard.Require(!EstimatedDurationOverrun || DurationRiskAccepted, "media.duration-review",
            "The script estimate exceeds the approved maximum. Shorten it, explicitly increase the maximum, or acknowledge the risk in Review generation. No production started.");
        MediaProjectStore.Validate(Saved!.Content, ready: true);
        var request = MediaApprovals.Approve(Saved, configuration, "local-explicit-approval");
        var host = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(),
            "..", "..", "..", "dotnet.exe"));
        var cli = FileProductionSession.CliDll;
        Busy = true;
        try
        {
            Status = await new MediaProducerLauncher(Production, host, cli).AcceptAsync(request, configurationFile!);
            View = MediaView.Jobs;
        }
        finally { Busy = false; }
    }
    public void PrepareSubmit()
    {
        Confirmation = null; DurationRiskAccepted = false;
        if (Voice.InputPending) throw new VideoException("media.dictation-pending", "Finish or discard local dictation before reviewing generation.");
        if (Busy || SourcesBusy) throw new VideoException("media.busy", "Finish authoring/source reading before reviewing generation.");
        MediaProjectStore.Validate(MediaDraftRules.ProductionContent(Draft.Frame), ready: true);
        Save();
        RefreshDefaultProduction();
        if (configurationFile is null) throw new VideoException("media.config", ProductionProblem
            ?? "Generation is not configured. Your storyboard is saved. Run 'MediaStudio.exe --configure production' to see what is missing.");
        ReviewSetup();
        if (reviewedConfiguration!.Verification is not null) GenerateLibraryCover = false;
        CoverSetupError = null;
        if (GenerateLibraryCover && reviewedConfiguration!.Verification is null)
        {
            try
            {
                var selection = CoverSelection ?? throw new VideoException("cover.unconfigured", "Select a chat model for the library cover, or turn off the cover in this review.");
                var video = reviewedConfiguration.Video ?? throw new VideoException("cover.renderer",
                    "A library cover requires the demo's configured image renderer. Audio generation itself does not; turn off the cover to produce audio alone.");
                var tools = Json.Read<Tools>(video.ToolsFile);
                var cover = new MediaCoverConfiguration(Providers.ChatConfiguration(selection),
                    new(tools.SourceRoot, tools.Node, Path.Combine(StateRoot, "cover-scratch"), video.FontFile, video.FontSha256));
                MediaCoverStore.ValidateConfiguration(cover, StateRoot);
                InstallReviewedConfiguration(reviewedConfiguration with { Cover = cover });
            }
            catch (Exception error) when (error is VideoException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                CoverSetupError = error.Message;
                InstallReviewedConfiguration(reviewedConfiguration with { Cover = null });
            }
        }
        else InstallReviewedConfiguration(reviewedConfiguration! with { Cover = null });
        Confirmation = SubmitFingerprint();
    }
    private string SubmitFingerprint() => Files.Fingerprint(new
    { Saved, Configuration = reviewedConfiguration, Draft.Version, GenerateLibraryCover, DurationRiskAccepted });
    public void ChangeMaximumSeconds(int seconds)
    {
        EditFrame(Draft.Frame with { Content = Working with { MaximumSeconds = seconds } }, "Changed explicit production maximum");
        PrepareSubmit();
    }
    public void AcceptDurationRisk(bool accepted)
    {
        Guard.Require(Confirmation is not null && Confirmation == SubmitFingerprint(),
            "media.confirmation", "The draft or setup changed. Review generation again.");
        DurationRiskAccepted = accepted;
        Confirmation = SubmitFingerprint();
    }
    public void ChangeCoverGeneration(bool enabled) { GenerateLibraryCover = enabled; PrepareSubmit(); }
    public async Task ConfirmSubmitAsync(string confirmation)
    {
        Guard.Require(Confirmation == confirmation
            && confirmation == SubmitFingerprint(),
            "media.confirmation", "Content or reviewed generation selection changed. Review Submit again.");
        Confirmation = null;
        Guard.Require(CoverSetupError is null, "cover.unconfigured", CoverSetupError ?? "Review the cover setup.");
        await ApproveAndGenerateAsync();
    }
    public void RefreshHistory()
    {
        History = HistoryStore.Read();
        CoverImages.Clear();
        foreach (var project in History.Artifacts.Select(a => a.Project).DistinctBy(p => (p.Id, p.Revision)))
            if (Covers.DataUri(project) is { } image) CoverImages[CoverKey(project)] = image;
    }
    public static string CoverKey(MediaProject project) => project.Id + "-r" + project.Revision;
    public ModelSelection? CoverSelection => Providers.State.ChatIllustration ?? Providers.State.Authoring ?? Providers.State.General;
    public async Task GenerateCoverAsync(MediaLibraryArtifact artifact)
    {
        Guard.Require(!Busy && !SourcesBusy, "cover.busy", "Finish the current authoring operation before generating a cover.");
        Guard.Require(HistoryStore.Read().Artifacts.Any(a => a.AttemptId == artifact.AttemptId
            && a.Kind == artifact.Kind && a.Sha256 == artifact.Sha256), "cover.stale", "Refresh this retained artifact before generating its cover.");
        var routed = Providers.State.RoutedIllustration && RoutedIllustrationAvailable;
        var selection = routed ? null : CoverSelection ?? throw new InvalidOperationException("Select a chat model in Settings to generate a cover.");
        if (Covers.Read(artifact.Project) is not null) { RefreshHistory(); return; }
        string source, node, font, fontSha;
        if (reviewedConfiguration?.Video is { } video)
        {
            var tools = Json.Read<Tools>(video.ToolsFile);
            source = tools.SourceRoot; node = tools.Node; font = video.FontFile; fontSha = video.FontSha256;
        }
        else if (reviewedConfiguration?.Verification is { } display)
        {
            source = Directory.GetCurrentDirectory(); node = Files.FindExecutable("node");
            font = display.FontFile; fontSha = display.FontSha256;
        }
        else throw new InvalidOperationException("Cover rendering needs the demo's configured Node renderer and pinned font. Audio production itself does not require them.");
        var scratch = Path.Combine(StateRoot, "cover-scratch");
        Directory.CreateDirectory(scratch); PrivateFiles.Protect(scratch, directory: true);
        Busy = true; CoverGenerating = true; Changed?.Invoke();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        authoring = timeout;
        try
        {
            using var chat = selection is null ? RoutedChat(MediaStudioAIKeys.Illustration)! : Providers.Chat(selection);
            using var generator = new ChatSvgImageGenerator(chat, selection?.Model ?? MediaStudioAIKeys.Illustration,
                new(source, node, scratch, font, fontSha));
            await Covers.GenerateAsync(artifact.Project, generator, timeout.Token);
            RefreshHistory();
        }
        finally { authoring = null; Busy = false; CoverGenerating = false; Changed?.Invoke(); }
    }
    private void AdoptConfiguration(string file)
    {
        var configuration = PrivateFiles.Read<MediaProductionConfiguration>(file);
        Guard.Require(configuration.SchemaVersion == 1
            && string.Equals(Path.GetFullPath(configuration.StateRoot), StateRoot, StringComparison.OrdinalIgnoreCase),
            "media.config", "Production configuration must bind this exact private project/attempt root.");
        configurationFile = baseConfigurationFile = file;
        reviewedConfiguration = configuration;
        GenerateLibraryCover = configuration.Verification is null && (defaultProduction is null || CoverSelection is not null);
        judgements?.Dispose();
        judgements = (Providers.NativeDecisions() ?? configuration.Decisions) is { } decisions ? new(decisions) : null;
    }
    /// <summary>
    /// Without an explicit <c>--media-config</c>, (re)derives the default production setup from the saved settings and adopts it
    /// once every prerequisite is present; otherwise records the actionable <see cref="ProductionProblem"/> and keeps generation off.
    /// </summary>
    private void RefreshDefaultProduction()
    {
        if (defaultProduction is null) return;
        DefaultProductionResult result;
        try { result = defaultProduction(); }
        catch (Exception error) when (error is VideoException or IOException or UnauthorizedAccessException or InvalidDataException
                                          or InvalidOperationException or System.Text.Json.JsonException)
        {
            ProductionProblem = "Generation setup could not be derived from your settings. Your storyboard is saved. " + error.Message;
            configurationFile = baseConfigurationFile = null; reviewedConfiguration = null;
            return;
        }
        if (!result.Ready)
        {
            ProductionProblem = result.Problem;
            configurationFile = baseConfigurationFile = null; reviewedConfiguration = null;
            return;
        }
        ProductionProblem = null;
        if (baseConfigurationFile != result.ConfigurationFile || result.Written) AdoptConfiguration(result.ConfigurationFile);
    }
    public void ReviewSetup()
    {
        if (Busy) throw new InvalidOperationException("Finish the current authoring/admission operation before changing setup.");
        if (baseConfigurationFile is null) RefreshDefaultProduction();
        if (baseConfigurationFile is null) throw new InvalidOperationException(ProductionProblem ?? "No production configuration was supplied.");
        var configuration = PrivateFiles.Read<MediaProductionConfiguration>(baseConfigurationFile);
        Guard.Require(configuration.SchemaVersion == 1
            && string.Equals(Path.GetFullPath(configuration.StateRoot), StateRoot, StringComparison.OrdinalIgnoreCase),
            "media.config", "The production configuration must bind this exact state root.");
        if (configuration.Video is { } video && Providers.State.NativeImages)
        {
            var selectedImage = Providers.State.ImageFallback!;
            var connection = Providers.Connection(selectedImage);
            Guard.Require(connection.Authentication == "azure-cli", "image.authentication",
                "The production snapshot uses SDK Entra authentication. A user-secret image connection needs an explicit supported production credential configuration.");
            configuration = configuration with {
                Video = video with {
                    Illustration = new(
                new(connection.Endpoint, selectedImage.Model, null, AllowRemote: true, Authentication: "azure-cli"),
                NativeImage: true)
                }
            };
        }
        // The Copilot illustration runtime lives in the app process only; background production keeps the saved setup then.
        else if (configuration.Video is { } routedVideo && providerSettings is not null && RoutedIllustrationAvailable
            && providerSettings.Agents.Illustration.Runtime != MediaStudio.Configuration.AgentRuntime.Copilot
            && (Providers.State.RoutedIllustration
                || Providers.State.ChatIllustration is null && Providers.State.LocalImage is null && routedVideo.Illustration is null
                    && Working.Storyboard?.Scenes.Any(s => s.IllustrationPrompt is not null) == true))
            configuration = configuration with {
                Video = IllustrationProviders.RouteThroughSettings(routedVideo, providerSettings, StateRoot,
                GroundingPassages())
            };
        else if (configuration.Video is { } chatVideo && Providers.State.ChatIllustration is not null)
            configuration = configuration with { Video = chatVideo with { Illustration = new(Providers.ChatIllustrationConfiguration()) } };
        else if (configuration.Video is { } localVideo && Providers.State.LocalImage is { } local)
            configuration = configuration with { Video = localVideo with { Illustration = new(local) } };
        else if (configuration.Video is { Illustration.NativeImage: true } unselectedNative)
            configuration = configuration with { Video = unselectedNative with { Illustration = null } };
        if (Providers.NativeDecisions() is { } selected)
        {
            configuration = configuration with { Decisions = selected };
        }
        InstallReviewedConfiguration(configuration);
        judgements?.Dispose();
        judgements = configuration.Decisions is { } decisions ? new(decisions) : null;
    }
    /// <summary>
    /// The draft's sources as grounding passages: the text chunks already extracted into each source's projection (by the
    /// consent-gated document router when the source was read), so production never re-uploads a document.
    /// </summary>
    private VideoProduction.Providers.Illustrations.SourcePassage[] GroundingPassages() => providerSettings?.Images.Grounding == true
        ? SourceList.SelectMany(s => VideoProduction.Providers.Illustrations.SourceIndex.FromChunks(s.Id, s.Projection.Chunks.Select(c => c.Text)))
            .Take(IllustrationProviders.MaximumGroundingPassages).ToArray()
        : [];
    private void InstallReviewedConfiguration(MediaProductionConfiguration configuration)
    {
        if (Files.Fingerprint(configuration) != Files.Fingerprint(PrivateFiles.Read<MediaProductionConfiguration>(baseConfigurationFile!)))
        {
            var path = Path.Combine(StateRoot, "settings", "production-" + Files.Fingerprint(configuration) + ".json");
            if (File.Exists(path))
                Guard.Require(Files.Fingerprint(PrivateFiles.Read<MediaProductionConfiguration>(path)) == Files.Fingerprint(configuration),
                    "media.config", "An immutable future-work configuration was altered.");
            else { Json.Write(path, configuration); PrivateFiles.Protect(path); }
            configurationFile = path;
        }
        else configurationFile = baseConfigurationFile;
        reviewedConfiguration = configuration; DurationRiskAccepted = false;
    }
    public void CancelProduction()
    {
        if (Status is null) throw new InvalidOperationException("There is no accepted production to cancel.");
        Status = Production.Cancel(Status.AttemptId);
    }
    public string? PreviewUri(string kind)
    {
        if (Status is not { } status || VerifiedOutputs?.Outputs.SelectMany(o => o.Artifacts).Any(a => a.Kind == kind) != true) return null;
        return $"/hybrid-result/{status.AttemptId}/{(kind == "audio-master" ? "audio.wav" : kind == "captions-vtt" ? "captions.vtt" : "movie.mp4")}";
    }
    public (Stream? Stream, string ContentType) OpenPreview(Uri uri)
    {
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || parts[0] != "hybrid-result") return (null, "application/octet-stream");
        var attemptId = parts[1];
        var outputs = Production.MediaArtifacts(attemptId, observeOnly: true);
        var kind = parts[2] == "audio.wav" ? "audio-master" : parts[2] == "movie.mp4" ? "candidate-mp4"
            : parts[2] == "captions.vtt" ? "captions-vtt" : null;
        if (kind is null) return (null, "application/octet-stream");
        var artifact = outputs.Outputs.SelectMany(o => o.Artifacts).SingleOrDefault(a => a.Kind == kind);
        if (artifact is null) return (null, "application/octet-stream");
        var root = Path.Combine(StateRoot, "attempts", attemptId);
        if (kind == "audio-master") _ = AudioProduction.Verify(Path.GetDirectoryName(Files.Under(root, artifact.Path))!);
        var stream = File.Open(Files.Under(root, artifact.Path), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        try
        {
            Guard.Require(Convert.ToHexString(SHA256.HashData(stream)).Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase),
                "attempt.artifact-hash", "Preview bytes differ from the committed candidate.");
            stream.Position = 0;
            return (stream, kind == "audio-master" ? "audio/wav" : kind == "candidate-mp4" ? "video/mp4" : "text/vtt; charset=utf-8");
        }
        catch { stream.Dispose(); throw; }
    }
    public async ValueTask DisposeAsync()
    {
        authoring?.Cancel();
        sourceCancellation?.Cancel();
        Voice.Dispose();
        if (sourceCompletion is { } completion) await completion.Task;
        Sources.Dispose();
        ocrChat?.Dispose();
        await Voice.DisposeAsync();
        judgements?.Dispose();
        agentClient?.Dispose();
        if (author is not null) await author.DisposeAsync();
    }
}
