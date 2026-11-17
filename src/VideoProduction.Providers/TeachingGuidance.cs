using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace VideoProduction.Providers;

public sealed record GuidanceIdentity(string Name, string Version, string Sha256);
public sealed record TeachingIntent(string LearningObjective, string PriorUnderstanding, string IntendedChange,
    string Mechanism, string EvidenceBoundary);

public static class TeachingGuidance
{
    public static GuidanceIdentity Identity { get; } = new("media-explanation", "1.0.0",
        "68b0991c2326baffa09c6098ff27bc1ba461f77952729336f6c1925dceeef7a3");
    private static readonly Lazy<string> source = new(() =>
    {
        using var stream = typeof(TeachingGuidance).Assembly.GetManifestResourceStream("MediaExplanation.Guidance")
            ?? throw new InvalidDataException("The canonical teaching guidance was not packaged.");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        var data = bytes.ToArray();
        Guard.Require(Convert.ToHexString(SHA256.HashData(data)).Equals(Identity.Sha256, StringComparison.OrdinalIgnoreCase),
            "guidance.integrity", "Packaged teaching guidance differs from the reviewed canonical source.");
        return Encoding.UTF8.GetString(data);
    });
    public static string Instructions => source.Value;

    public static void Validate(TeachingIntent? intent)
    {
        if (intent is null) return;
        Guard.Require(new[] { intent.LearningObjective, intent.PriorUnderstanding, intent.IntendedChange,
            intent.Mechanism, intent.EvidenceBoundary }.All(value => !string.IsNullOrWhiteSpace(value)
                && value.Length <= 2000 && !value.Contains('\0')),
            "teaching.intent", "Teaching metadata requires bounded objective, prior understanding, change, mechanism and evidence boundary.");
    }
}
