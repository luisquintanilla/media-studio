using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaStudio.Configuration;
using VideoProduction;
using VideoProduction.Providers;

namespace MediaStudio.Tests.Configuration;

public sealed class DefaultProductionConfigurationTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "default-production-tests", Guid.NewGuid().ToString("N")[..8]);
    private readonly string _state;
    private readonly string _source;
    private readonly ModelAssets _assets;
    private static string Fonts => Path.Combine(TestPaths.RepoRoot, "assets", "fonts");
    private static string? AllTools(string name) => @"C:\tools\" + name + ".exe";
    private static string? Espeak() => @"C:\tools\espeak-ng.exe";

    public DefaultProductionConfigurationTests()
    {
        _state = Path.Combine(_root, "state");
        _source = Path.Combine(_root, "source");
        Directory.CreateDirectory(_state);
        Directory.CreateDirectory(Path.Combine(_source, "workers", "browser", "node_modules", "playwright"));
        File.WriteAllText(Path.Combine(_source, "workers", "browser", "worker.mjs"), "// worker");
        _assets = FakeKokoroAssets(Path.Combine(_root, "models"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static ProviderSettings Local => ProviderSettingsDefaults.Local(HardwareTier.M,
        ModelManifest.Load(Path.Combine(TestPaths.RepoRoot, "assets", ModelManifest.FileName)));

    private static readonly ConnectionSettings Azure = new() {
        Id = "azure", Kind = ConnectionKind.Foundry, Endpoint = "https://example-account.services.ai.azure.com/", Auth = AuthMode.Entra
    };

    private DefaultProductionResult Write(ProviderSettings settings, Func<string, string?>? tools = null) =>
        DefaultProductionConfiguration.Write(settings, _state, _assets, _source, tools ?? AllTools, Espeak, Fonts);

    [Fact]
    public void LocalSettingsDeriveKokoroNarrationBundledFontAndPathTools()
    {
        var result = Write(Local);

        Assert.True(result.Ready, result.Problem);
        Assert.True(result.Written);
        var configuration = PrivateFiles.Read<MediaProductionConfiguration>(result.ConfigurationFile);
        Assert.Equal(Path.GetFullPath(_state), configuration.StateRoot);
        Assert.Equal(new SpeechConfig("kokoro", "af_heart", "en-us", 1, "quantized"), configuration.Audio!.Speech);
        Assert.Equal(24000, configuration.Audio.SampleRate);
        Assert.Equal("kokoro", configuration.Audio.Identity.Provider);
        Assert.Equal(BundledFont.Sha256, configuration.Video!.FontSha256);
        Assert.Equal(Files.Hash(Path.Combine(Fonts, BundledFont.LicenseFileName)), configuration.Video.FontLicenseSha256);
        Assert.Null(configuration.Video.Illustration);
        Assert.Null(configuration.Decisions!.JuliaModelDirectory);
        var tools = PrivateFiles.Read<Tools>(configuration.Video.ToolsFile);
        Assert.Equal(("node", "ffmpeg", "ffprobe"), (tools.Node, tools.Ffmpeg, tools.Ffprobe));
        Assert.Equal(Path.GetFullPath(_source), tools.SourceRoot);
        Assert.Equal(configuration.Audio.Speech, tools.Speech);
        var produce = PrivateFiles.Read<FileProductionConfiguration>(result.FileProductionConfigurationFile);
        Assert.Equal(configuration.Video, produce.Generation);
    }

    [Fact]
    public void AzdCloudSpeechDerivesAzureSpeechWithoutSecrets()
    {
        const string secret = "sk-test-secret-value-1234567890";
        var settings = ProviderSettingsDefaults.WithAzure(Local, Azure with { Auth = AuthMode.ApiKey }, cloudSpeech: true);
        var variable = CredentialFactory.ApiKeyEnvironmentVariable(Azure.Id);
        Environment.SetEnvironmentVariable(variable, secret);
        try
        {
            var result = Write(settings);

            Assert.True(result.Ready, result.Problem);
            var speech = result.Configuration!.Audio!.Speech;
            Assert.Equal(("azure-speech", "en-US-Harper:MAI-Voice-2.1-Flash", "en-US"), (speech.Provider, speech.Voice, speech.Language));
            foreach (var file in Directory.GetFiles(Path.GetDirectoryName(result.ConfigurationFile)!))
            {
                var text = File.ReadAllText(file);
                Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
                Assert.DoesNotContain("example-account", text, StringComparison.Ordinal);
            }
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    [Fact]
    public void CloudSpeechWithoutCloudConsentIsReportedNotDowngraded()
    {
        var settings = ProviderSettingsDefaults.WithAzure(Local, Azure, cloudSpeech: true);
        settings = settings with { Privacy = settings.Privacy with { AllowCloud = false } };

        var result = Write(settings);

        Assert.False(result.Ready);
        Assert.Null(result.Configuration);
        Assert.Contains("allowCloud", result.Problem, StringComparison.Ordinal);
        Assert.False(File.Exists(result.ConfigurationFile));
    }

    [Fact]
    public void MissingFfmpegAndKokoroAssetsNameTheFixCommands()
    {
        var result = DefaultProductionConfiguration.Build(Local, _state, new ModelAssets(_assets.Manifest, Path.Combine(_root, "empty-models")),
            _source, name => name == "ffmpeg" ? null : AllTools(name), Espeak, Fonts);

        Assert.False(result.Ready);
        Assert.Equal(["kokoro", "ffmpeg"], result.Missing.Select(m => m.Name));
        Assert.Contains(@"scripts\Install-Prerequisites.ps1", result.Problem, StringComparison.Ordinal);
        Assert.Contains("assets ensure --id kokoro-v1.0-q8", result.Problem, StringComparison.Ordinal);
        Assert.Contains("storyboard is saved", result.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void RewritesOnlyWhenTheDerivedConfigurationChanges()
    {
        Assert.True(Write(Local).Written);
        Assert.False(Write(Local).Written);

        var faster = Local with { Speech = Local.Speech with { Tts = Local.Speech.Tts with { Voice = "bf_emma", Speed = 1.25 } } };
        var changed = Write(faster);

        Assert.True(changed.Written);
        var speech = PrivateFiles.Read<MediaProductionConfiguration>(changed.ConfigurationFile).Audio!.Speech;
        Assert.Equal(("bf_emma", "en-gb", 1.25f), (speech.Voice, speech.Language, speech.Speed));
    }

    [Fact]
    public void ExplicitMediaConfigWinsOverTheDerivedDefault()
    {
        Assert.Null(DefaultProductionConfiguration.ForLaunch(Path.Combine(_root, "explicit.json"), _state, () => throw new InvalidOperationException()));

        var derive = DefaultProductionConfiguration.ForLaunch(null, _state, () => Local, _assets, _source, AllTools, Espeak, Fonts);

        Assert.NotNull(derive);
        Assert.True(derive().Ready);
    }

    [Fact]
    public void CommandPreviewsByDefaultAndWritesOnRequest()
    {
        var settingsFile = Path.Combine(_root, "settings", "model-providers.json");
        new ProviderSettingsStore(settingsFile).Save(Local);
        var manifest = Path.Combine(_root, "manifest.json");
        var models = Path.Combine(_root, "models");
        string[] Args(params string[] extra) => ["configure", "production", "--state-root", _state, "--settings", settingsFile,
            "--manifest", manifest, "--models", models, .. extra];

        using var preview = new StringWriter();
        Assert.Equal(0, DefaultProductionCommand.Run(Args(), preview, null, AllTools, Espeak, _source, Fonts));
        Assert.Contains("Preview only", preview.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(DefaultProductionConfiguration.Folder(_state), DefaultProductionConfiguration.ConfigurationFileName)));

        using var written = new StringWriter();
        Assert.Equal(0, DefaultProductionCommand.Run(Args("--write"), written, null, AllTools, Espeak, _source, Fonts));
        Assert.Contains("Ready to generate", written.ToString(), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(DefaultProductionConfiguration.Folder(_state), DefaultProductionConfiguration.ConfigurationFileName)));

        using var missing = new StringWriter();
        Assert.Equal(1, DefaultProductionCommand.Run(Args(), missing, null, name => name == "node" ? null : AllTools(name), Espeak, _source, Fonts));
        Assert.Contains("node is not on PATH", missing.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A one-file stand-in for the pinned Kokoro asset, verified so <see cref="ModelAssets.GetPath"/> reports it installed.</summary>
    private ModelAssets FakeKokoroAssets(string models)
    {
        var content = Encoding.UTF8.GetBytes("{}");
        var manifestFile = Path.Combine(_root, "manifest.json");
        File.WriteAllText(manifestFile, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            assets = new[]
            {
                new
                {
                    id = "kokoro-v1.0-q8", displayName = "Kokoro (test)", license = "Apache-2.0", source = "https://example.invalid/kokoro",
                    revision = "test", dir = "kokoro",
                    files = new[] { new { path = "tokenizer.json", url = "https://example.invalid/test/tokenizer.json", size = content.Length,
                        sha256 = Convert.ToHexStringLower(SHA256.HashData(content)) } }
                }
            }
        }));
        var assets = new ModelAssets(ModelManifest.Load(manifestFile), models);
        Directory.CreateDirectory(assets.GetDirectory("kokoro-v1.0-q8"));
        File.WriteAllBytes(Path.Combine(assets.GetDirectory("kokoro-v1.0-q8"), "tokenizer.json"), content);
        assets.VerifyAsync("kokoro-v1.0-q8").GetAwaiter().GetResult();
        return assets;
    }
}
