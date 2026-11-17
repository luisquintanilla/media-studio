using System.Security.Cryptography;
using System.Text;

namespace VideoProduction.Decisions.Experimental.Julia;

/// <summary>Exact authorized public Julia asset set; no downloads or native loading.</summary>
public static class JuliaModelAssets
{
    public const string Repository = "SupersonicLabs/Julia-1-ONNX";
    public const string Revision = "82a2fadf8fccfccdc5fd4e1009ba8f1a265eb7a8";
    public const string ModelSha256 = "97141d0cfb1da6204e9f8f24d581af72eaeb82cda21149d83eaa6df7160fbcd9";
    public const string WeightsSha256 = "fd915be810d7ebfb80fb05a48dd33c9484d17ae1b6bcb9e1f544cbaaa913ded1";
    public const string TokenizerSha256 = "609d8f4c067cd3950f88594c5a802616cea245823836ef5848ee4fc40aab5b6f";
    public const string ContractRevision = "e87f1126cf22108fb27f6391c7826fb91c25138e";
    public const string ProviderRevision = "ce28e92b338b0edfbc22cca3c62101fa75c008f7";

    /// <summary>Canonical UTF-8 manifest, LF terminated, ordinal asset order. Apache-2.0 model card.</summary>
    public const string Manifest = Repository + "@" + Revision + "\n"
        + "model.onnx " + ModelSha256 + "\n"
        + "model.onnx.data " + WeightsSha256 + "\n"
        + "tokenizer.json " + TokenizerSha256 + "\n";

    /// <summary>SHA-256 of <see cref="Manifest"/>; computation reads no files and loads no native library.</summary>
    public static string Fingerprint { get; } =
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Manifest)));

    /// <summary>Verifies every required asset before native loading. Returns the absolute directory.</summary>
    public static string VerifyDirectory(string modelDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);
        string directory = Path.GetFullPath(modelDirectory);
        Verify(Path.Combine(directory, "model.onnx"), ModelSha256);
        Verify(Path.Combine(directory, "model.onnx.data"), WeightsSha256);
        Verify(Path.Combine(directory, "tokenizer.json"), TokenizerSha256);
        return directory;
    }

    private static void Verify(string path, string expected)
    {
        using FileStream stream = File.OpenRead(path);
        string actual = Convert.ToHexStringLower(SHA256.HashData(stream));
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new InvalidDataException($"Julia asset '{Path.GetFileName(path)}' SHA-256 mismatch: expected {expected}, observed {actual}.");
    }
}
