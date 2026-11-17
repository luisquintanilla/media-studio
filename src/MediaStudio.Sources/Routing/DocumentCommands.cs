using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using MediaStudio.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;
using VideoProduction.Decisions.Experimental;

namespace MediaStudio.Sources;

/// <summary>Model clients the document commands need; supplied by the host so Sources does not reference Providers.</summary>
/// <param name="Vision">Local (or configured) vision chat client for the <c>vlm</c> candidate, or <see langword="null"/>.</param>
/// <param name="Decider">Routing decision client, or <see langword="null"/> to use routing rules.</param>
/// <param name="VisionIsRemote">The vision client runs on a cloud connection.</param>
public sealed record DocumentCommandServices(IChatClient? Vision, IDecisionClient? Decider, bool VisionIsRemote = false);

/// <summary>
/// <c>documents probe &lt;file&gt;</c> prints the local probe features and the routing plan without extracting;
/// <c>bench documents &lt;files...&gt;</c> extracts with the Auto router and every configured extractor and reports time and size.
/// Cloud extractors run only with <c>--allow-cloud</c> (which is the upload consent) and when <c>privacy.allowCloud</c> is on.
/// </summary>
public static class DocumentCommands
{
    public const string Usage = """
        Usage:
          documents probe <file> [--extract] [--allow-cloud] [--settings <file>]
          bench documents <file>... [--local-only] [--allow-cloud] [--settings <file>]

        probe reads cheap local features (text layer, pages, scanned ratio, tables, language) and shows which extractor
        Auto would try first. --extract also runs the routed extraction. Nothing is uploaded unless --allow-cloud is passed
        and privacy.allowCloud is on.
        """;

    public static bool Handles(string[] args) => args.Length > 0
        && (args[0] == "documents" || args[0] == "bench" && args.Length > 1 && args[1] == "documents");

    public static async Task<int> RunAsync(string[] args, TextWriter output, Func<ProviderSettings, DocumentCommandServices> services,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(services);
        var bench = args[0] == "bench";
        var rest = args.Skip(bench ? 2 : 1).ToList();
        if (!bench && (rest.Count == 0 || rest[0] != "probe") || rest.Any(a => a is "-h" or "--help"))
        {
            output.WriteLine(Usage);
            return rest.Any(a => a is "-h" or "--help") ? 0 : 2;
        }
        if (!bench) rest.RemoveAt(0);
        string? settingsFile = null;
        var files = new List<string>();
        var flags = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < rest.Count; i++)
        {
            if (rest[i] == "--settings" && i + 1 < rest.Count) settingsFile = rest[++i];
            else if (rest[i] is "--extract" or "--allow-cloud" or "--local-only") flags.Add(rest[i]);
            else if (rest[i].StartsWith("--", StringComparison.Ordinal)) { output.WriteLine($"Unknown option {rest[i]}."); output.WriteLine(Usage); return 2; }
            else files.Add(rest[i]);
        }
        if (files.Count == 0 || !bench && files.Count != 1) { output.WriteLine(Usage); return 2; }
        foreach (var file in files)
            if (!File.Exists(file)) { output.WriteLine($"File not found: {file}"); return 2; }

        var settings = (settingsFile is not null ? new ProviderSettingsStore(settingsFile) : new ProviderSettingsStore()).TryLoad()
            ?? new ProviderSettings();
        if (flags.Contains("--local-only"))
            settings = settings with { Privacy = settings.Privacy with { AllowCloud = false } };
        var clients = services(settings);
        IUploadConsent consent = flags.Contains("--allow-cloud") ? FixedUploadConsent.Allow : FixedUploadConsent.Deny;
        var router = DocumentExtractorFactory.CreateRouter(settings, clients.Vision, clients.Decider, consent,
            visionIsRemote: clients.VisionIsRemote);
        return bench
            ? await BenchAsync(files, settings, clients, consent, router, output, cancellationToken).ConfigureAwait(false)
            : await ProbeAsync(files[0], router, flags.Contains("--extract"), output, cancellationToken).ConfigureAwait(false);
    }

    public static string MediaTypeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch {
        ".pdf" => "application/pdf",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".tif" or ".tiff" => "image/tiff",
        ".bmp" => "image/bmp",
        _ => "application/octet-stream"
    };

    private static async Task<int> ProbeAsync(string file, DecisionRoutingDocumentExtractionClient router, bool extract,
        TextWriter output, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
        var mediaType = MediaTypeOf(file);
        var probe = DocumentProbe.Analyze(bytes, mediaType);
        var plan = await router.PlanAsync(probe, cancellationToken).ConfigureAwait(false);
        var result = new Dictionary<string, object?> {
            ["file"] = Path.GetFileName(file),
            ["mediaType"] = mediaType,
            ["probe"] = probe.ToState(),
            ["plan"] = new
            {
                source = plan.Source,
                reason = plan.Reason,
                ranked = plan.Ranked.Select(c => new
                {
                    name = c.Name,
                    displayName = DocumentExtractorFactory.DisplayName(c.Name),
                    remote = c.IsRemote,
                    probability = plan.Probabilities?.GetValueOrDefault(c.Name)
                })
            },
            ["candidates"] = router.Candidates.Select(c => new { name = c.Name, remote = c.IsRemote, fits = c.Fits(probe) })
        };
        var exit = 0;
        if (extract)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                using var input = new MemoryStream(bytes, writable: false);
                var document = await router.ExtractAsync(input, Path.GetFileName(file), mediaType, null, cancellationToken).ConfigureAwait(false);
                result["extraction"] = Summary(document, watch.Elapsed);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                result["extraction"] = new { error = error.Message };
                exit = 1;
            }
        }
        output.WriteLine(JsonSerializer.Serialize(result, Indented));
        return exit;
    }

    private static async Task<int> BenchAsync(IReadOnlyList<string> files, ProviderSettings settings, DocumentCommandServices clients,
        IUploadConsent consent, DecisionRoutingDocumentExtractionClient router, TextWriter output, CancellationToken cancellationToken)
    {
        var extractors = new List<(string Name, DocumentExtractionClient Client)> { ("auto", router) };
        foreach (var candidate in router.Candidates)
            extractors.Add((candidate.Name, candidate.Client));
        var skipped = DocumentCandidates.All.Except(extractors.Select(e => e.Name)).ToArray();
        if (skipped.Length > 0) output.WriteLine($"Not configured: {string.Join(", ", skipped)}.");
        if (!CloudGuard.Allows(settings.Privacy.AllowCloud, true) && router.Candidates.Any(c => c.IsRemote))
            output.WriteLine("Cloud extractors are listed but privacy.allowCloud is off; they will be skipped.");
        output.WriteLine("file\textractor\tms\tchars\tpages\tresult");
        var failures = 0;
        foreach (var file in files)
        {
            var bytes = await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
            var mediaType = MediaTypeOf(file);
            var probe = DocumentProbe.Analyze(bytes, mediaType);
            foreach (var (name, client) in extractors)
            {
                var candidate = router.Candidates.FirstOrDefault(c => c.Name == name);
                string row;
                var watch = Stopwatch.StartNew();
                if (candidate is not null && !candidate.Fits(probe)) row = "0\t0\t0\tskipped (type, size or pages)";
                else if (candidate is { IsRemote: true } && consent == FixedUploadConsent.Deny) row = "0\t0\t0\tskipped (pass --allow-cloud)";
                else
                {
                    try
                    {
                        using var input = new MemoryStream(bytes, writable: false);
                        var document = await client.ExtractAsync(input, Path.GetFileName(file), mediaType, null, cancellationToken).ConfigureAwait(false);
                        var chosen = document.Metadata.TryGetValue(DocumentExtractionTags.Extractor, out var value) ? value?.ToString() : name;
                        var quality = ExtractionQuality.IsAcceptable(document, probe) ? "ok" : "low-quality";
                        row = string.Create(CultureInfo.InvariantCulture,
                            $"{watch.ElapsedMilliseconds}\t{ExtractionQuality.TextLength(document)}\t{document.SourcePages.Count}\t{quality}{(name == "auto" ? $" via {chosen}" : "")}");
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        failures++;
                        row = string.Create(CultureInfo.InvariantCulture, $"{watch.ElapsedMilliseconds}\t0\t0\tfailed: {error.Message.ReplaceLineEndings(" ")}");
                    }
                }
                output.WriteLine($"{Path.GetFileName(file)}\t{name}\t{row}");
            }
        }
        return failures == 0 ? 0 : 1;
    }

    private static object Summary(ExtractionDocument document, TimeSpan elapsed) => new
    {
        extractor = document.Metadata.TryGetValue(DocumentExtractionTags.Extractor, out var extractor) ? extractor : null,
        attempts = document.Metadata.TryGetValue(DocumentExtractionTags.Attempts, out var attempts) ? attempts : null,
        lowQuality = document.Metadata.ContainsKey(DocumentExtractionTags.LowQuality),
        pages = document.SourcePages.Count,
        chars = ExtractionQuality.TextLength(document),
        milliseconds = (long)elapsed.TotalMilliseconds
    };

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
}
