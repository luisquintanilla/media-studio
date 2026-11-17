using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace VideoProduction;

public static partial class Files
{
    [GeneratedRegex("^[a-z][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();
    [GeneratedRegex("^[a-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex HashPattern();

    public static bool ValidId(string value) => Identifier().IsMatch(value);
    public static bool ValidHash(string value) => HashPattern().IsMatch(value);
    public static string Hash(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(file));
    }
    public static string HashText(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static string Fingerprint<T>(T value) => HashText(Json.Text(value));

    public static string Under(string root, string relative)
    {
        Guard.Require(!string.IsNullOrWhiteSpace(relative) && !Path.IsPathRooted(relative)
            && !relative.Contains('\\') && !relative.Contains(':') && !relative.Contains('\0'),
            "path.relative", "Project paths must use portable, slash-separated relative paths.");
        var parts = relative.Split('/');
        Guard.Require(parts.All(p => p.Length > 0 && p != "." && p != ".."
            && !p.EndsWith(' ') && !p.EndsWith('.') && p.IndexOfAny(['<', '>', '"', '|', '?', '*']) < 0
            && !IsDevice(p)), "path.unsafe", $"Unsafe project path: {relative}");
        var fullRoot = Path.GetFullPath(root);
        Guard.Require(!File.GetAttributes(fullRoot).HasFlag(FileAttributes.ReparsePoint),
            "path.link", "Project root cannot be a symbolic link/reparse point.");
        var path = fullRoot;
        foreach (var part in parts)
        {
            path = Path.Combine(path, part);
            if (File.Exists(path) || Directory.Exists(path))
                Guard.Require(!File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint),
                    "path.link", $"Links/reparse points are not allowed: {relative}");
        }
        return path;
    }

    private static bool IsDevice(string part)
    {
        var stem = part.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL"
            || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
            && char.IsAsciiDigit(stem[3]);
    }

    public static void VerifyAsset(string root, Asset asset)
    {
        Guard.Require(ValidId(asset.Id) && ValidHash(asset.Sha256), "asset.identity", $"Invalid asset identity: {asset.Id}");
        Guard.Require(!string.IsNullOrWhiteSpace(asset.Provenance) && !string.IsNullOrWhiteSpace(asset.License),
            "asset.provenance", $"Missing provenance/license: {asset.Id}");
        var path = Under(root, asset.Path);
        Guard.Require(File.Exists(path), "asset.missing", $"Missing asset: {asset.Id}");
        Guard.Require(Hash(path) == asset.Sha256, "asset.hash", $"Asset changed: {asset.Id}");
    }

    public static string FindExecutable(string name)
    {
        if (Path.IsPathRooted(name))
        {
            Guard.Require(File.Exists(name), "tool.missing", $"Missing executable: {name}");
            return Path.GetFullPath(name);
        }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            foreach (var suffix in OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", "" } : new[] { "" })
            {
                var candidate = Path.Combine(dir, name + suffix);
                if (File.Exists(candidate) && Path.GetExtension(candidate) != ".cmd") return Path.GetFullPath(candidate);
            }
        throw new VideoException("tool.missing", $"Executable not on PATH: {name}");
    }
}

public static class Atomic
{
    public static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, content, new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static bool IsCurrent(string root, string recordPath, string fingerprint)
    {
        if (!File.Exists(recordPath)) return false;
        var record = Json.Read<StageRecord>(recordPath);
        return record.Fingerprint == fingerprint && record.Outputs.Length > 0
            && record.Outputs.All(x => File.Exists(Files.Under(root, x.Path)) && Files.Hash(Files.Under(root, x.Path)) == x.Sha256);
    }

    public static void Commit(string root, string recordPath, string fingerprint, IEnumerable<string> outputs) =>
        Json.Write(recordPath, new StageRecord(fingerprint,
            outputs.Select(x => new ArtifactHash(x, Files.Hash(Files.Under(root, x)))).ToArray()));
}
