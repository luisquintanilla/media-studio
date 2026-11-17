using System.Text.Json;
using System.Text.Json.Serialization;

namespace VideoProduction;

public enum SceneKind { Title, Browser, Terminal, Code, Diagram, Media }
public enum TimingMaster { Narration, Action, Fixed }
public enum Evidence { TakeWindow, AsrSegment, HumanVerified }
public enum EffectKind { Highlight, Arrow, Fade }
public sealed record Effect(EffectKind Kind, long StartFrame, long EndFrame, string Text = "");
public sealed record Cue(long StartSample, long EndSample, string Text, Evidence Evidence, string? PolicyException = null);
public sealed record Narration(string AssetId, int SampleRate, long SampleCount, string DisplayText, Cue[] Cues,
    string? TranscriptAssetId = null, bool TranscriptPending = false, string? TranscriptAdoptionAssetId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ScriptCaptionAssetId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SpeechAnalysisAssetId = null);
public sealed record SampleSelection(long StartSample, long EndSample, string Reason);
public sealed record Scene(
    string Id, SceneKind Kind, string Title, string Text, string Script, TimingMaster Timing,
    long FixedFrames, bool AllowHold, string? VisualAssetId, Narration? Narration,
    Effect[] Effects, JellyfinProfile? Capture = null, FootageEdit? Edit = null,
    SampleSelection? AudioSelection = null, string? VisualDescription = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SceneTransition? TransitionIn = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] VisualTreatment? Visual = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MotionVisual? Motion = null);
public sealed record Project(
    int SchemaVersion, string Id, int Width, int Height, Rate Fps,
    Asset[] Assets, Scene[] Scenes, Chapter[]? Chapters = null, DeliveryCaptionBinding? DeliveryCaptions = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AuthoredNarrationPolicy? NarrationPolicy = null);
public sealed record ActionSpec(string Id, string Kind, string Executable, string ExecutableSha256,
    string[] Argv, string Workspace, string[] ReadFiles, string[] WriteFiles, string[] Origins,
    string Trust, int TimeoutSeconds, int MaxOutputBytes);
public sealed record ActionPlan(int SchemaVersion, string ProjectSha256, string InputsSha256,
    string WorkerSha256, ActionSpec[] Actions, string Fingerprint, ArtifactHash? PrivateInput = null);
public sealed record Approval(int SchemaVersion, string Fingerprint, string ApprovedBy, string Scope);
public sealed record ResolvedScene(string Id, SceneKind Kind, long StartFrame, long FrameCount,
    string NarrationAssetId, long AudioSamples, long DeliverySamples, string? VisualAssetId,
    long AudioStartSample = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int TransitionInFrames = 0);
public sealed record Timeline(int SchemaVersion, string ProjectSha256, string Fingerprint,
    Rate Fps, int SampleRate, long TotalFrames, ResolvedScene[] Scenes);
public sealed record ProcessEvent(long ElapsedTicks, string Stream, string Text);
public sealed record ProcessResult(int ExitCode, string Stdout, string Stderr, ProcessEvent[] Events, long Frequency);
public sealed record Review(int SchemaVersion, string MovieSha256, string Reviewer,
    bool Claims, bool Voice, bool Pace, bool Privacy, bool Captions, bool Attribution, bool ViewingSize);
