using System.Runtime.InteropServices;

namespace MediaStudio.Agents.Copilot;

/// <summary>
/// Finds the GitHub Copilot CLI runtime. Order: the <c>MEDIASTUDIO_COPILOT_CLI</c> override, the runtime the
/// GitHub.Copilot.SDK package copies next to the app (<c>runtimes/&lt;rid&gt;/native/copilot[.exe]</c>), then
/// <c>copilot</c> on PATH. Returns null when none exists; the app then reports Copilot as not installed.
/// </summary>
public static class CopilotCliLocator
{
    public const string OverrideVariable = "MEDIASTUDIO_COPILOT_CLI";

    public static string BinaryName => OperatingSystem.IsWindows() ? "copilot.exe" : "copilot";

    public static string? Find(string? baseDirectory = null, Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        if (environment(OverrideVariable) is { Length: > 0 } explicitPath)
            return File.Exists(explicitPath) ? Path.GetFullPath(explicitPath) : null;
        var root = baseDirectory ?? AppContext.BaseDirectory;
        var bundled = Path.Combine(root, "runtimes", RuntimeIdentifier(), "native", BinaryName);
        if (File.Exists(bundled)) return bundled;
        foreach (var directory in (environment("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim('"'), BinaryName);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    internal static string RuntimeIdentifier()
    {
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        return os + "-" + arch;
    }
}
