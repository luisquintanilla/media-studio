using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.AI;

namespace VideoProduction.Providers;

public sealed record SvgRasterConfiguration(string SourceRoot, string Node, string ScratchRoot,
    string FontFile, string FontSha256);
public sealed record SvgImageEvidence(string Kind, string Prompt, string ChatClientType, string ConfiguredModel,
    string? ReturnedModel, string Svg, string SvgSha256, string PngSha256, string RasterizerSha256,
    string FontSha256, string BrowserVersion, bool NativeImageModel = false,
    string? FontStatus = null, int TextElementCount = 0, bool PinnedFontApplied = false,
    string? SvgPolicyRevision = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ChatRouteReceipt? Routing = null);
public sealed record ChatSvgAttempt(string Prompt, string ChatClientType, string ConfiguredModel,
    string? ReturnedModel, string Output, long? InputTokens, long? OutputTokens, long? TotalTokens);

public static partial class SafeSvg
{
    public const string PolicyRevision = "bounded-svg-comments-normalized-v2";
    private static readonly XNamespace Ns = "http://www.w3.org/2000/svg";
    [GeneratedRegex(@"^(?:#[0-9a-fA-F]{6}|none)$", RegexOptions.CultureInvariant)]
    private static partial Regex Color();
    private static readonly Dictionary<string, string[]> Allowed = new(StringComparer.Ordinal) {
        ["svg"] = ["width", "height", "viewBox"],
        ["g"] = ["fill", "stroke", "stroke-width", "opacity"],
        ["rect"] = ["x", "y", "width", "height", "rx", "fill", "stroke", "stroke-width", "opacity"],
        ["circle"] = ["cx", "cy", "r", "fill", "stroke", "stroke-width", "opacity"],
        ["line"] = ["x1", "y1", "x2", "y2", "stroke", "stroke-width", "opacity"],
        ["text"] = ["x", "y", "fill", "font-size", "text-anchor", "opacity"],
        ["polyline"] = ["points", "fill", "stroke", "stroke-width", "opacity"],
        ["polygon"] = ["points", "fill", "stroke", "stroke-width", "opacity"]
    };
    internal static string AuthoringContract => string.Join("\n", Allowed.Select(pair =>
        pair.Key + ": " + string.Join(",", pair.Value)));
    internal static bool AllowsElement(string name) => Allowed.ContainsKey(name);
    internal static bool AllowsAttribute(string element, string attribute) =>
        Allowed.TryGetValue(element, out var attributes) && attributes.Contains(attribute);
    public static string Validate(string input, int width = 1920, int height = 1080)
    {
        Guard.Require(input.Length is > 0 and <= 32000, "image.svg", "SVG must be bounded markup, not a document/renderer program.");
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new StringReader(input), new XmlReaderSettings {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 32000,
                IgnoreComments = true, IgnoreProcessingInstructions = false
            });
            document = XDocument.Load(reader);
        }
        catch (XmlException) { throw new VideoException("image.svg", "SVG is malformed or contains a forbidden document declaration."); }
        Guard.Require(document.Declaration is null && document.Root?.Name == Ns + "svg"
            && document.Nodes().All(n => n is XElement), "image.svg", "Only a bare SVG root is accepted.");
        var elements = document.Root!.DescendantsAndSelf().ToArray();
        Guard.Require(elements.Length <= 128 && elements.All(e => e.Ancestors().Count() <= 4),
            "image.svg", "SVG element/depth bound exceeded.");
        foreach (var element in elements)
        {
            var name = element.Name.LocalName;
            Guard.Require(element.Name.Namespace == Ns && Allowed.ContainsKey(name)
                && (element == document.Root || name != "svg"), "image.svg",
                "SVG accepts only inert svg/g/rect/circle/line/polyline/polygon/text elements.");
            Guard.Require(element.Nodes().All(n => n is XElement || n is XText text && n is not XCData
                    && (name == "text" || string.IsNullOrWhiteSpace(text.Value)))
                && (name is "svg" or "g" || !element.Elements().Any()),
                "image.svg", "SVG CDATA/instructions and mixed/active content are forbidden.");
            foreach (var attribute in element.Attributes())
            {
                if (attribute.IsNamespaceDeclaration)
                {
                    Guard.Require(element == document.Root && attribute.Name.LocalName == "xmlns"
                        && attribute.Value == Ns.NamespaceName, "image.svg", "Additional namespaces are forbidden.");
                    continue;
                }
                Guard.Require(attribute.Name.Namespace == XNamespace.None && Allowed[name].Contains(attribute.Name.LocalName),
                    "image.svg", "SVG contains unsupported attributes, events, styles, links or resources.");
                var key = attribute.Name.LocalName;
                var value = attribute.Value;
                if (key is "fill" or "stroke")
                    Guard.Require(Color().IsMatch(value), "image.svg", "Only six-digit hex/none SVG colors are supported.");
                else if (key == "text-anchor")
                    Guard.Require(value is "start" or "middle" or "end", "image.svg", "Unsupported text anchor.");
                else if (key == "viewBox")
                    Guard.Require(value == $"0 0 {width} {height}", "image.svg", "SVG viewBox must exactly match the requested bounded canvas.");
                else if (key == "points")
                {
                    var parts = value.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
                    Guard.Require(parts.Length is >= 4 and <= 64 && parts.Length % 2 == 0,
                        "image.svg", "Polyline/polygon points must be bounded coordinate pairs.");
                    foreach (var part in parts) Number(part, 1920);
                }
                else Number(value, key == "opacity" ? 1 : key == "font-size" ? 100 : key == "stroke-width" ? 16 : 1920);
            }
            if (name == "text")
                Guard.Require(element.Value.Length is > 0 and <= 240 && !element.Value.Any(char.IsControl),
                    "image.svg", "SVG text must be bounded plain text.");
        }
        Guard.Require((string?)document.Root.Attribute("width") == width.ToString(CultureInfo.InvariantCulture)
            && (string?)document.Root.Attribute("height") == height.ToString(CultureInfo.InvariantCulture)
            && (string?)document.Root.Attribute("viewBox") == $"0 0 {width} {height}",
            "image.svg", "SVG dimensions must explicitly match the requested bounded canvas.");
        return document.Root.ToString(SaveOptions.DisableFormatting);
    }
    private static void Number(string text, double max) => Guard.Require(text.Length <= 12
        && double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number)
        && double.IsFinite(number) && number >= 0 && number <= max,
        "image.svg", "SVG numbers must be finite, nonnegative bounded decimal values.");
}

/// <summary>Pipeline options for <see cref="ChatSvgImageGenerator"/>; null keeps the strict legacy contract.</summary>
public sealed record ChatSvgOptions
{
    /// <summary>Repair model output with <see cref="Illustrations.SvgSanitizer"/> before strict validation.</summary>
    public bool Sanitize { get; init; } = true;
    /// <summary>Prompt bound; grounded prompts carry source facts and exemplar SVGs on top of the 2,000-character brief.</summary>
    public int MaxPromptCharacters { get; init; } = 16_000;
    /// <summary>Fresh attempts after output that cannot be repaired into valid SVG (small local models occasionally slip).</summary>
    public int InvalidOutputRetries { get; init; } = 1;
}

/// <summary>
/// <see cref="IImageGenerator"/> over an <see cref="IChatClient"/>: the model authors constrained SVG that is validated by
/// <see cref="SafeSvg"/> and rasterized offline to PNG. Without a <see cref="SvgRasterConfiguration"/> the validated SVG is
/// returned as <c>image/svg+xml</c>. With <see cref="ChatSvgOptions"/> the output is repaired first and option keys
/// prefixed <c>mediastudio.</c> (used by the illustration decorators) are accepted.
/// </summary>
public sealed class ChatSvgImageGenerator(IChatClient chat, string model, SvgRasterConfiguration? raster,
    Action<ChatSvgAttempt>? observeAttempt = null, ChatSvgOptions? svgOptions = null) : IImageGenerator
{
    public const string SvgMediaType = "image/svg+xml";
    /// <summary>Prefix of the <see cref="ImageGenerationOptions.AdditionalProperties"/> keys accepted in pipeline mode.</summary>
    public const string PropertyPrefix = "mediastudio.";

    public string MediaType => raster is null ? SvgMediaType : "image/png";

    public async Task<ImageGenerationResponse> GenerateAsync(ImageGenerationRequest request, ImageGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var size = ValidateRequest(request, options);
        cancellationToken.ThrowIfCancellationRequested();
        for (var attempt = 0; ; attempt++)
        {
            var response = await GenerateSvgAsync(request, size, cancellationToken);
            string svg;
            try
            {
                svg = svgOptions?.Sanitize == true
                    ? Illustrations.SvgSanitizer.Sanitize(response.Text, size.Width, size.Height)
                    : SafeSvg.Validate(response.Text, size.Width, size.Height);
            }
            catch (VideoException error) when (error.Code == "image.svg" && attempt < (svgOptions?.InvalidOutputRetries ?? 0))
            {
                continue;
            }
            return await RenderAsync(request.Prompt!, response, svg, size, cancellationToken);
        }
    }

    /// <summary>Validates and renders an existing SVG (for example a reused exemplar) without a chat call.</summary>
    public Task<ImageGenerationResponse> RenderSvgAsync(string svg, string prompt, System.Drawing.Size size,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        return RenderAsync(prompt, null, SafeSvg.Validate(svg, size.Width, size.Height), size, cancellationToken);
    }

    private System.Drawing.Size ValidateRequest(ImageGenerationRequest request, ImageGenerationOptions? options)
    {
        ArgumentNullException.ThrowIfNull(request);
        var size = options?.ImageSize ?? new System.Drawing.Size(1920, 1080);
        Guard.Require(!string.IsNullOrWhiteSpace(request.Prompt) && request.Prompt.Length <= (svgOptions?.MaxPromptCharacters ?? 2000)
            && !request.Prompt.Contains('\0') && request.OriginalImages is null,
            "image.request", "Provide a bounded illustration prompt, not image editing/original images.");
        Guard.Require(!string.IsNullOrWhiteSpace(model) && (options is null
            || (options.Count is null or 1) && size.Width is >= 128 and <= 1920 && size.Height is >= 128 and <= 1080
            && (options.MediaType is null || options.MediaType == MediaType) && (options.ModelId is null || options.ModelId == model)
            && (options.ResponseFormat is null or ImageGenerationResponseFormat.Data)
            && options.StreamingCount is null && options.RawRepresentationFactory is null
            && (options.AdditionalProperties is null || svgOptions is not null
                && options.AdditionalProperties.Keys.All(k => k.StartsWith(PropertyPrefix, StringComparison.Ordinal)))),
            "image.options", "Only one bounded128..1920by128..1080PNG data response is supported; unsupported image/model/native options are rejected.");
        return size;
    }

    private async Task<ChatResponse> GenerateSvgAsync(ImageGenerationRequest request, System.Drawing.Size size,
        CancellationToken cancellationToken)
    {
        ChatResponse response;
        try
        {
            response = await chat.GetResponseAsync(
                [new(ChatRole.System, $$"""
                    Author one simple technical vector illustration, not a screenshot or photorealistic image.
                    Return bare SVG only, no markdown/declaration/comments. Root xmlns="http://www.w3.org/2000/svg"
                    width="{{size.Width}}" height="{{size.Height}}" viewBox="0 0 {{size.Width}} {{size.Height}}".
                    Only svg,g,rect,circle,line,polyline,polygon,text; <=128 elements, <=4 nesting levels.
                    Only coordinates, width/height/r/rx, points, fill/stroke (#RRGGBB or none), stroke-width<=16,
                    opacity0..1, font-size<=100, text-anchor=start|middle|end. No transforms/styles/fonts/ids/links,
                    paths, scripts, images, resources, URLs, animation or foreignObject. Plain text<=240characters/element.
                    The complete per-element attribute allowlist follows. Every unlisted attribute is forbidden,
                    including stroke-linecap, stroke-linejoin and font-family. Prefer no XML comments.
                    Bounded XML comments are discarded structurally before validation; no other content is repaired.
                    {{SafeSvg.AuthoringContract}}
                    Respect the user's layout/background brief. For1080pixel-tall video assets, keep the bottom216pixels empty
                    for caption-safe composition. If any text is requested, it uses the host's pinned font.
                    """), new(ChatRole.User, request.Prompt)],
                new ChatOptions { ModelId = model, MaxOutputTokens = 4096 }, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (VideoException) { throw; }
        catch (Exception error)
        {
            var code = error switch {
                Azure.Identity.CredentialUnavailableException => "image.credential-unavailable",
                Azure.Identity.AuthenticationFailedException => "image.authentication",
                System.ClientModel.ClientResultException http => "image.http-" + http.Status,
                HttpRequestException => "image.transport",
                _ => "image.provider"
            };
            throw new VideoException(code, "The explicitly injected chat provider failed (" + error.GetType().Name
                + "); no raw credential/provider diagnostics or fallback are exposed.");
        }
        Guard.Require(response.Messages.All(m => m.Contents.All(c => c is TextContent or TextReasoningContent)),
            "image.output", "Chat illustration output contains unsupported content: "
                + string.Join(", ", response.Messages.SelectMany(m => m.Contents)
                    .Where(c => c is not (TextContent or TextReasoningContent)).Select(c => c.GetType().Name).Distinct())
                + ". Only SVG answer text and standard reasoning metadata are supported; no tool calls or resources.");
        Guard.Require(response.Text.Length <= (svgOptions?.Sanitize == true ? Illustrations.SvgSanitizer.MaxInputCharacters : 32000),
            "image.output", "Chat illustration response exceeds the bounded SVG limit.");
        observeAttempt?.Invoke(new(request.Prompt!, chat.GetType().FullName!, RouteReceipt(response)?.SelectedModel ?? model, response.ModelId, response.Text,
            response.Usage?.InputTokenCount, response.Usage?.OutputTokenCount, response.Usage?.TotalTokenCount));
        return response;
    }

    private static ChatRouteReceipt? RouteReceipt(ChatResponse response) =>
        response.AdditionalProperties?.TryGetValue(DecisionRoutingChatClient.ReceiptKey, out var value) == true
            && value is ChatRouteReceipt receipt ? receipt : null;

    private async Task<ImageGenerationResponse> RenderAsync(string prompt, ChatResponse? response, string svg,
        System.Drawing.Size size, CancellationToken cancellationToken)
    {
        if (raster is null)
        {
            var routing = response is null ? null : RouteReceipt(response);
            var evidence = new SvgImageEvidence(response is null ? "reused-validated-svg" : "chat-authored-validated-svg", prompt,
                chat.GetType().FullName!, routing?.SelectedModel ?? model, response?.ModelId, svg, Files.HashText(svg), "", "", "", "",
                SvgPolicyRevision: SafeSvg.PolicyRevision, Routing: routing);
            return new ImageGenerationResponse([new DataContent(System.Text.Encoding.UTF8.GetBytes(svg), SvgMediaType)]) {
                RawRepresentation = evidence, Usage = response?.Usage
            };
        }
        return await RasterizeAsync(prompt, response, svg, size, raster, cancellationToken);
    }

    private async Task<ImageGenerationResponse> RasterizeAsync(string prompt, ChatResponse? response, string svg,
        System.Drawing.Size size, SvgRasterConfiguration raster, CancellationToken cancellationToken)
    {
        var scratch = PrivateFiles.LocalPath(raster.ScratchRoot);
        Guard.Require(Directory.Exists(scratch), "image.scratch", "Explicit private image scratch directory must exist.");
        var work = Path.Combine(scratch, "svg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        PrivateFiles.Protect(work, directory: true);
        try
        {
            var worker = Path.Combine(PrivateFiles.LocalPath(raster.SourceRoot), "workers", "browser", "illustration.mjs");
            var font = PrivateFiles.LocalPath(raster.FontFile);
            Guard.Require(Files.ValidHash(raster.FontSha256) && Files.Hash(font) == raster.FontSha256,
                "image.font", "Explicit raster font differs from its configured pin.");
            var configFile = Path.Combine(work, "raster.json");
            var output = Path.Combine(work, "image.png");
            Json.Write(configFile, new
            {
                svg, width = size.Width, height = size.Height,
                fontPath = font, fontSha256 = raster.FontSha256, outputFile = output
            });
            var process = await Processes.Run(Files.FindExecutable(raster.Node), [worker, configFile], work, cancellationToken, 60);
            using var info = JsonDocument.Parse(process.Stdout);
            var bytes = await File.ReadAllBytesAsync(output, cancellationToken);
            ValidatePng(bytes, size.Width, size.Height);
            var routing = response is null ? null : RouteReceipt(response);
            var evidence = new SvgImageEvidence(response is null ? "reused-validated-svg-raster" : "chat-authored-validated-svg-raster",
                prompt, chat.GetType().FullName!, routing?.SelectedModel ?? model, response?.ModelId, svg, Files.HashText(svg), Files.Hash(output), Files.Hash(worker), raster.FontSha256,
                info.RootElement.GetProperty("browserVersion").GetString()!, FontStatus: info.RootElement.GetProperty("fontStatus").GetString(),
                TextElementCount: info.RootElement.GetProperty("textElementCount").GetInt32(),
                PinnedFontApplied: info.RootElement.GetProperty("pinnedFontApplied").GetBoolean(),
                SvgPolicyRevision: SafeSvg.PolicyRevision, Routing: routing);
            return new ImageGenerationResponse([new DataContent(bytes, "image/png")]) {
                RawRepresentation = evidence, Usage = response?.Usage
            };
        }
        finally { Directory.Delete(work, recursive: true); }
    }
    public static void ValidatePng(byte[] bytes, int width = 1920, int height = 1080) => Guard.Require(bytes.Length is > 32 and <= 8_388_608
        && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
        && System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)) == width
        && System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)) == height,
        "image.png", "Rasterizer must return a bounded PNG at the exact requested dimensions.");
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    }
    public void Dispose() { } // The injector owns the chat client.
}
