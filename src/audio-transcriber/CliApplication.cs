using System.Text.Json;
using System.Security;
using System.CommandLine;

namespace AudioTranscriber;

public sealed class CliApplication
{
    private readonly ITranscriptionEngine _engine;
    private readonly WavAudioReader _audioReader;

    public CliApplication(ITranscriptionEngine? engine = null, WavAudioReader? audioReader = null)
    {
        _engine = engine ?? new WhisperNetEngine();
        _audioReader = audioReader ?? new WavAudioReader();
    }

    public async Task<int> RunAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        string? repositoryRoot = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (args.Length == 0)
        {
            await error.WriteLineAsync(Usage()).ConfigureAwait(false);
            return 2;
        }

        try
        {
            if (args is ["-h"] or ["--help"])
            {
                await output.WriteAsync(SmartToolManifestService.Markdown()).ConfigureAwait(false);
                return 0;
            }

            if (args is ["transcribe", "-h" or "--help"])
                return await WriteTranscribeHelpAsync(output).ConfigureAwait(false);
            var root = new RootCommand("Local fixed-English transcription.");
            var manifest = new Command("manifest");
            manifest.SetAction((_, _) => WriteManifestAsync(output));
            var status = new Command("status");
            status.SetAction((_, _) => WriteStatusAsync(output));
            var doctor = new Command("doctor");
            doctor.SetAction((_, _) => WriteDoctorAsync(output, repositoryRoot ?? Directory.GetCurrentDirectory()));
            var transcribe = new Command("transcribe");
            var inputs = new Option<string[]>("--input") { Required = true, Arity = ArgumentArity.OneOrMore, AllowMultipleArgumentsPerToken = false };
            var format = new Option<string>("--format") { DefaultValueFactory = _ => "text" };
            format.Validators.Add(result =>
            {
                if (!TranscriptRenderers.TryParse(result.GetValueOrDefault<string>() ?? "", out _))
                    result.AddError("--format must be text, json, srt, or webvtt.");
            });
            var destination = new Option<string?>("--output");
            transcribe.Options.Add(inputs);
            transcribe.Options.Add(format);
            transcribe.Options.Add(destination);
            transcribe.SetAction(async (result, token) =>
            {
                if (!TranscriptRenderers.TryParse(result.GetValue(format)!, out var outputFormat))
                    throw new InvalidOperationException("The validated transcript format is missing.");
                return await TranscribeAsync(new(result.GetValue(inputs)!, outputFormat,
                    result.GetValue(destination) is { } path ? Path.GetFullPath(path) : null), output, token);
            });
            root.Subcommands.Add(manifest);
            root.Subcommands.Add(status);
            root.Subcommands.Add(doctor);
            root.Subcommands.Add(transcribe);
            var parsed = root.Parse(args, new ParserConfiguration { ResponseFileTokenReplacer = null });
            if (parsed.Errors.Count > 0)
            {
                foreach (var parseError in parsed.Errors) await error.WriteLineAsync("error: " + parseError.Message);
                if (args[0] == "transcribe" && !args.Contains("--input", StringComparer.Ordinal))
                    await error.WriteLineAsync("At least one --input WAV path is required.");
                await error.WriteLineAsync(Usage());
                return 2;
            }
            return await parsed.InvokeAsync(new InvocationConfiguration {
                Output = output, Error = error, EnableDefaultExceptionHandler = false,
                ProcessTerminationTimeout = null
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await error.WriteLineAsync("Operation cancelled.").ConfigureAwait(false);
            return 4;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            InvalidDataException or
            ArgumentException or
            InvalidOperationException or
            DllNotFoundException or
            EntryPointNotFoundException or
            BadImageFormatException or
            SecurityException)
        {
            await error.WriteLineAsync(
                $"error: {SafePathDisplay.RedactKnownPaths(
                    SafePathDisplay.RedactKnownPaths(
                        exception.Message,
                        SafePathDisplay.RepositoryToken,
                        Directory.GetCurrentDirectory()),
                    SafePathDisplay.ModelCacheToken,
                    SmartToolPaths.DefaultModelCacheDirectory)}")
                .ConfigureAwait(false);
            return 1;
        }
    }

    private static async Task<int> WriteManifestAsync(TextWriter output)
    {
        await output.WriteAsync(SmartToolManifestService.Markdown()).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> WriteStatusAsync(TextWriter output)
    {
        await output.WriteAsync(SmartToolManifestService.ToJson(SmartToolManifestService.Create())).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> WriteTranscribeHelpAsync(TextWriter output)
    {
        await output.WriteLineAsync(
            """
            Transcribe local 16 kHz mono WAV files with the configured local Whisper model.

            Usage:
              audio-transcriber transcribe --input <file.wav> [--input <file.wav>] [--format text|json|srt|webvtt] [--output <path>]

            The capability is model-backed and fails explicitly when the model integration
            is unavailable. It never prompts and never silently returns a degraded result.
            """).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> WriteDoctorAsync(TextWriter output, string repositoryRoot)
    {
        var report = new DoctorService().Run(repositoryRoot);
        await output.WriteLineAsync(JsonSerializer.Serialize(
            report,
            new JsonSerializerOptions {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            })).ConfigureAwait(false);
        return report.Healthy ? 0 : 1;
    }

    private async Task<int> TranscribeAsync(
        TranscribeOptions options,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var batch = await new BatchTranscriptionService(_engine, _audioReader)
            .TranscribeAsync(options.Inputs, cancellationToken)
            .ConfigureAwait(false);
        var rendered = TranscriptRenderers.Create(options.Format).Render(batch);
        if (options.OutputPath is null)
        {
            await output.WriteAsync(rendered).ConfigureAwait(false);
        }
        else
        {
            try
            {
                await File.WriteAllTextAsync(options.OutputPath, rendered, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new IOException(
                    $"Transcript output could not be written to '{SafePathDisplay.Basename(options.OutputPath)}'.",
                    exception);
            }
        }

        return 0;
    }

    private static async Task<int> WriteUsageErrorAsync(TextWriter error)
    {
        await error.WriteLineAsync(Usage()).ConfigureAwait(false);
        return 2;
    }

    private static string Usage() =>
        """
        Usage:
          audio-transcriber -h|--help
          audio-transcriber manifest
          audio-transcriber status
          audio-transcriber doctor
          audio-transcriber transcribe --input <file.wav> [--input <file.wav>] [--format text|json|srt|webvtt] [--output <path>]
        """;

    private sealed record TranscribeOptions(
        IReadOnlyList<string> Inputs,
        TranscriptOutputFormat Format,
        string? OutputPath);
}
