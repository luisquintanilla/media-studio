using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace VideoProduction.Providers.Illustrations;

/// <summary>
/// Repairs model-authored SVG into the inert <see cref="SafeSvg"/> subset before validation: keeps only the
/// <c>&lt;svg&gt;…&lt;/svg&gt;</c> markup, drops scripts, foreign objects, images, links, style sheets, paths and every
/// other element or attribute outside the allow-list (with their subtrees), resolves simple inline styles and color
/// names, rescales a different viewBox onto the requested canvas and enforces the element/depth/size bounds.
/// The result is always re-checked by <see cref="SafeSvg.Validate"/>; anything that cannot be repaired is rejected.
/// </summary>
public static partial class SvgSanitizer
{
    /// <summary>Largest raw model output accepted for repair.</summary>
    public const int MaxInputCharacters = 64_000;
    public const int MaxElements = 128;
    public const int MaxDepth = 4;
    public const int MaxTextCharacters = 240;
    private static readonly XNamespace Ns = "http://www.w3.org/2000/svg";

    private static readonly Dictionary<string, string> NamedColors = new(StringComparer.OrdinalIgnoreCase) {
        ["black"] = "#000000", ["white"] = "#FFFFFF", ["red"] = "#FF0000", ["green"] = "#008000", ["lime"] = "#00FF00",
        ["blue"] = "#0000FF", ["navy"] = "#000080", ["yellow"] = "#FFFF00", ["orange"] = "#FFA500", ["purple"] = "#800080",
        ["gray"] = "#808080", ["grey"] = "#808080", ["lightgray"] = "#D3D3D3", ["lightgrey"] = "#D3D3D3",
        ["darkgray"] = "#A9A9A9", ["darkgrey"] = "#A9A9A9", ["silver"] = "#C0C0C0", ["teal"] = "#008080",
        ["cyan"] = "#00FFFF", ["aqua"] = "#00FFFF", ["magenta"] = "#FF00FF", ["fuchsia"] = "#FF00FF", ["maroon"] = "#800000",
        ["olive"] = "#808000", ["brown"] = "#A52A2A", ["pink"] = "#FFC0CB", ["gold"] = "#FFD700", ["skyblue"] = "#87CEEB",
        ["steelblue"] = "#4682B4", ["darkblue"] = "#00008B", ["lightblue"] = "#ADD8E6", ["darkgreen"] = "#006400",
        ["lightgreen"] = "#90EE90", ["darkred"] = "#8B0000", ["coral"] = "#FF7F50", ["tomato"] = "#FF6347",
        ["indigo"] = "#4B0082", ["violet"] = "#EE82EE", ["beige"] = "#F5F5DC", ["ivory"] = "#FFFFF0",
        ["whitesmoke"] = "#F5F5F5", ["slategray"] = "#708090", ["slategrey"] = "#708090", ["darkslategray"] = "#2F4F4F",
        ["darkslategrey"] = "#2F4F4F", ["dimgray"] = "#696969", ["dimgrey"] = "#696969", ["transparent"] = "none", ["none"] = "none"
    };

    [GeneratedRegex(@"\s[A-Za-z_][\w.-]*:[\w.-]+\s*=\s*(?:""[^""]*""|'[^']*')", RegexOptions.CultureInvariant)]
    private static partial Regex PrefixedAttribute();
    [GeneratedRegex(@"^#([0-9a-fA-F])([0-9a-fA-F])([0-9a-fA-F])$", RegexOptions.CultureInvariant)]
    private static partial Regex ShortHex();
    [GeneratedRegex(@"^#[0-9a-fA-F]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex LongHex();
    [GeneratedRegex(@"^\s*(-?[0-9]*\.?[0-9]+(?:[eE][-+]?[0-9]+)?)\s*(?:px)?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex Length();

    /// <summary>Returns validated, inert SVG at exactly <paramref name="width"/>×<paramref name="height"/>.</summary>
    /// <exception cref="VideoException">Code <c>image.svg</c> when no usable SVG can be recovered.</exception>
    public static string Sanitize(string raw, int width = 1920, int height = 1080)
    {
        ArgumentNullException.ThrowIfNull(raw);
        Guard.Require(raw.Length is > 0 and <= MaxInputCharacters, "image.svg", "SVG output is empty or larger than the repair bound.");
        Guard.Require(width is >= 1 and <= 1920 && height is >= 1 and <= 1080, "image.svg", "Requested SVG canvas is out of bounds.");
        var start = raw.IndexOf("<svg", StringComparison.OrdinalIgnoreCase);
        var end = raw.LastIndexOf("</svg>", StringComparison.OrdinalIgnoreCase);
        Guard.Require(start >= 0, "image.svg", "The model did not return an <svg> element.");
        // A missing </svg> (truncated output) is closed by the lexical repair below.
        var markup = PrefixedAttribute().Replace(end > start ? raw[start..(end + "</svg>".Length)] : raw[start..], "");
        XElement source;
        try
        {
            using var reader = XmlReader.Create(new StringReader(markup), new XmlReaderSettings {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxInputCharacters,
                IgnoreComments = true, IgnoreProcessingInstructions = true
            });
            source = XElement.Load(reader);
        }
        catch (XmlException)
        {
            // Small models often emit near-XML (an attribute without a value, a stray close tag): rebuild it lexically.
            try { source = XElement.Parse(Relex(markup), LoadOptions.None); }
            catch (XmlException) { throw new VideoException("image.svg", "SVG output is not well-formed XML."); }
        }
        Guard.Require(source.Name.LocalName == "svg", "image.svg", "The model did not return an <svg> root.");

        var (scaleX, scaleY) = Scale(source, width, height);
        var context = new Context(scaleX, scaleY) { Elements = 1 };
        var root = new XElement(Ns + "svg",
            new XAttribute("width", width.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("height", height.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("viewBox", $"0 0 {width} {height}"));
        foreach (var child in source.Elements()) if (Clean(child, depth: 1, context) is { } clean) root.Add(clean);
        Guard.Require(root.HasElements, "image.svg", "The SVG has no drawable elements after removing unsupported content.");
        return SafeSvg.Validate(root.ToString(SaveOptions.DisableFormatting), width, height);
    }

    [GeneratedRegex(@"<!--.*?-->|<\?.*?\?>|<!\[CDATA\[.*?\]\]>|<![^>]*>", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex Markup();
    [GeneratedRegex(@"<(/?)([A-Za-z][\w.-]*)([^<>]*?)(/?)>|([^<]+)|<", RegexOptions.CultureInvariant)]
    private static partial Regex Token();
    [GeneratedRegex(@"([A-Za-z_][\w.-]*)\s*=\s*(?:""([^""<]*)""|'([^'<]*)')", RegexOptions.CultureInvariant)]
    private static partial Regex WellFormedAttribute();

    /// <summary>Rebuilds tag soup as well-formed XML: keeps only well-formed attributes, balances tags, escapes text.</summary>
    private static string Relex(string markup)
    {
        var builder = new StringBuilder();
        var open = new List<string>();
        foreach (Match token in Token().Matches(Markup().Replace(markup, " ")))
        {
            if (token.Groups[5].Success)
            {
                if (open.Count > 0) builder.Append(System.Security.SecurityElement.Escape(System.Net.WebUtility.HtmlDecode(token.Value)));
                continue;
            }
            if (!token.Groups[2].Success) continue;
            var name = token.Groups[2].Value;
            if (token.Groups[1].Length > 0)
            {
                var at = open.LastIndexOf(name);
                if (at < 0) continue;
                for (var i = open.Count - 1; i >= at; i--) builder.Append("</").Append(open[i]).Append('>');
                open.RemoveRange(at, open.Count - at);
                if (open.Count == 0) break;
                continue;
            }
            if (open.Count == 0 && name != "svg") continue;
            builder.Append('<').Append(name);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match attribute in WellFormedAttribute().Matches(token.Groups[3].Value))
                if (seen.Add(attribute.Groups[1].Value))
                    builder.Append(' ').Append(attribute.Groups[1].Value).Append("=\"")
                        .Append(System.Security.SecurityElement.Escape(System.Net.WebUtility.HtmlDecode(
                            attribute.Groups[2].Success ? attribute.Groups[2].Value : attribute.Groups[3].Value))).Append('"');
            if (token.Groups[4].Length > 0 || token.Groups[3].Value.TrimEnd().EndsWith('/')) builder.Append("/>");
            else { builder.Append('>'); open.Add(name); }
        }
        for (var i = open.Count - 1; i >= 0; i--) builder.Append("</").Append(open[i]).Append('>');
        return builder.ToString();
    }

    private sealed class Context(double scaleX, double scaleY)
    {
        public double ScaleX { get; } = scaleX;
        public double ScaleY { get; } = scaleY;
        public int Elements { get; set; }
    }

    private static (double X, double Y) Scale(XElement svg, int width, int height)
    {
        var box = ((string?)svg.Attribute("viewBox"))?.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
        if (box is { Length: 4 } && box.Select(Parse).ToArray() is [{ } x, { } y, { } w, { } h] && x == 0 && y == 0 && w > 0 && h > 0)
            return (width / w, height / h);
        if (Parse((string?)svg.Attribute("width")) is { } sw && Parse((string?)svg.Attribute("height")) is { } sh && sw > 0 && sh > 0)
            return (width / sw, height / sh);
        return (1, 1);
    }

    private static XElement? Clean(XElement element, int depth, Context context)
    {
        var name = element.Name.LocalName;
        if (depth > MaxDepth || context.Elements >= MaxElements || name == "svg" || !SafeSvg.AllowsElement(name)) return null;
        var clean = new XElement(Ns + name);
        foreach (var (key, value) in Attributes(element))
            if (SafeSvg.AllowsAttribute(name, key) && Value(key, value, context) is { } normalized)
                clean.SetAttributeValue(key, normalized);
        if (name is "polyline" or "polygon" && clean.Attribute("points") is null) return null;
        if (name == "text")
        {
            var text = new string(element.Value.Where(c => !char.IsControl(c)).ToArray()).Trim();
            if (text.Length == 0) return null;
            clean.Add(text.Length <= MaxTextCharacters ? text : text[..MaxTextCharacters]);
        }
        context.Elements++;
        if (name == "g")
            foreach (var child in element.Elements())
                if (Clean(child, depth + 1, context) is { } cleanChild) clean.Add(cleanChild);
        return clean;
    }

    private static IEnumerable<(string Key, string Value)> Attributes(XElement element)
    {
        var result = new List<(string Key, string Value)>();
        foreach (var attribute in element.Attributes())
        {
            if (attribute.IsNamespaceDeclaration || attribute.Name.Namespace != XNamespace.None) continue;
            if (attribute.Name.LocalName == "style")
            {
                foreach (var declaration in attribute.Value.Split(';', StringSplitOptions.RemoveEmptyEntries))
                    if (declaration.Split(':', 2) is [var key, var value])
                        result.Insert(0, (key.Trim().ToLowerInvariant(), value.Trim()));
                continue;
            }
            result.Add((attribute.Name.LocalName, attribute.Value));
        }
        // Inline style declarations come first so explicit presentation attributes win.
        return result.GroupBy(a => a.Key, StringComparer.Ordinal).Select(g => g.Last());
    }

    private static string? Value(string key, string value, Context context)
    {
        switch (key)
        {
            case "fill" or "stroke":
                return Color(value);
            case "text-anchor":
                return value.Trim() is "start" or "middle" or "end" ? value.Trim() : null;
            case "points":
                var numbers = value.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries).Select(Parse).ToArray();
                if (numbers.Any(n => n is null) || numbers.Length % 2 != 0 || numbers.Length < 4) return null;
                var pairs = Math.Min(numbers.Length, 64) / 2;
                var builder = new StringBuilder();
                for (var i = 0; i < pairs; i++)
                {
                    if (i > 0) builder.Append(' ');
                    builder.Append(Format(numbers[2 * i]!.Value * context.ScaleX, 1920)).Append(',')
                        .Append(Format(numbers[2 * i + 1]!.Value * context.ScaleY, 1920));
                }
                return builder.ToString();
            default:
                if (Parse(value) is not { } number) return null;
                var (scale, max) = key switch {
                    "x" or "x1" or "x2" or "cx" or "width" or "rx" => (context.ScaleX, 1920d),
                    "y" or "y1" or "y2" or "cy" or "height" => (context.ScaleY, 1920d),
                    "r" => (Math.Min(context.ScaleX, context.ScaleY), 1920d),
                    "stroke-width" => (Math.Min(context.ScaleX, context.ScaleY), 16d),
                    "font-size" => (Math.Min(context.ScaleX, context.ScaleY), 100d),
                    "opacity" => (1d, 1d),
                    _ => (1d, 1920d)
                };
                return Format(number * scale, max);
        }
    }

    private static string? Color(string value)
    {
        var trimmed = value.Trim();
        if (LongHex().IsMatch(trimmed)) return trimmed.ToUpperInvariant();
        if (ShortHex().Match(trimmed) is { Success: true } m)
            return ("#" + m.Groups[1].Value + m.Groups[1].Value + m.Groups[2].Value + m.Groups[2].Value
                + m.Groups[3].Value + m.Groups[3].Value).ToUpperInvariant();
        return NamedColors.GetValueOrDefault(trimmed);
    }

    private static double? Parse(string? text) =>
        text is not null && Length().Match(text) is { Success: true } m
        && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n : null;

    private static string Format(double value, double max) =>
        Math.Round(Math.Clamp(value, 0, max), 2).ToString("0.##", CultureInfo.InvariantCulture);
}
