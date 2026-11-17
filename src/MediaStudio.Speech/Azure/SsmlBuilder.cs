using System.Text;
using System.Xml;

namespace MediaStudio.Speech.Azure;

/// <summary>Builds Azure Speech SSML with <see cref="XmlWriter"/> (text is always XML-escaped; never string-concatenated).</summary>
public static class SsmlBuilder
{
    public const string SynthesisNamespace = "http://www.w3.org/2001/10/synthesis";
    public const string MsttsNamespace = "https://www.w3.org/2001/mstts";

    public static string Build(string text, string voice, string language, string? style = null, string? rate = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(voice);
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        var builder = new StringBuilder();
        using (var writer = XmlWriter.Create(builder, new XmlWriterSettings { OmitXmlDeclaration = true, Indent = false }))
        {
            writer.WriteStartElement("speak", SynthesisNamespace);
            writer.WriteAttributeString("version", "1.0");
            writer.WriteAttributeString("xmlns", "mstts", null, MsttsNamespace);
            writer.WriteAttributeString("xml", "lang", null, language);
            writer.WriteStartElement("voice", SynthesisNamespace);
            writer.WriteAttributeString("name", voice);
            if (!string.IsNullOrWhiteSpace(style))
            {
                writer.WriteStartElement("mstts", "express-as", MsttsNamespace);
                writer.WriteAttributeString("style", style);
            }
            if (!string.IsNullOrWhiteSpace(rate))
            {
                writer.WriteStartElement("prosody", SynthesisNamespace);
                writer.WriteAttributeString("rate", rate);
            }
            writer.WriteString(text);
            var open = 1 + (string.IsNullOrWhiteSpace(style) ? 0 : 1) + (string.IsNullOrWhiteSpace(rate) ? 0 : 1);
            for (var i = 0; i < open; i++) writer.WriteEndElement();
            writer.WriteEndElement();
        }
        return builder.ToString();
    }

    /// <summary>Maps a speed multiplier to an SSML prosody rate (<c>1.0</c> → null, <c>1.2</c> → <c>+20%</c>).</summary>
    public static string? RateFor(float? speed)
    {
        if (speed is not { } value || !float.IsFinite(value) || Math.Abs(value - 1f) < 0.005f) return null;
        var percent = (int)Math.Round((Math.Clamp(value, 0.5f, 2f) - 1f) * 100);
        return percent >= 0 ? $"+{percent}%" : $"{percent}%";
    }
}
