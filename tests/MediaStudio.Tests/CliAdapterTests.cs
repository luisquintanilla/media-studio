using System.Diagnostics;
using System.Text.Json;
using VideoProduction;

internal static class CliAdapterTests
{
    public static async Task<int> Run(string root)
    {
        var count = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); count++; }
        var cli = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "VideoProduction.Cli", "bin", new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name,
            new DirectoryInfo(AppContext.BaseDirectory).Name, "video-production.dll"));
        async Task<(int Exit, string Output, string Error)> Run(params string[] args)
        {
            using var process = new Process {
                StartInfo = new(Files.FindExecutable("dotnet")) {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root
                }
            };
            process.StartInfo.ArgumentList.Add(cli);
            foreach (var argument in args) process.StartInfo.ArgumentList.Add(argument);
            if (!process.Start()) throw new IOException("Could not start the actual CLI fixture.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (TimeoutException) { process.Kill(entireProcessTree: true); throw; }
            return (process.ExitCode, await output, await error);
        }
        foreach (var flag in new[] { "--version", "-V" })
        {
            var result = await Run(flag);
            Check(result.Exit == 0 && result.Output.Trim() == SelfDescription.Manifest().Manifest.Version && result.Error.Length == 0,
                "Version aliases remain deterministic stdout-only, without runtime setup.");
        }
        var manifest = await Run("manifest");
        using var document = JsonDocument.Parse(manifest.Output);
        Check(manifest.Exit == 0 && manifest.Error.Length == 0
            && document.RootElement.GetProperty("manifest").GetProperty("name").GetString() == "video-production",
            "Canonical machine manifest keeps the compatible adapter identity.");
        foreach (var capability in new[] { "audio-produce", "audio-check", "media-produce", "produce", "narrate" })
        {
            var help = await Run(capability, "--help");
            Check(help.Exit == 0 && help.Error.Length == 0 && help.Output.Contains(capability, StringComparison.Ordinal),
                "Capability help is runtime/model-independent and stdout-only.");
        }
        var response = Path.Combine(root, "must-not-expand.rsp");
        File.WriteAllText(response, "--directory " + root);
        foreach (var args in new[]
        {
            new[] { "audio-check", "@" + response },
            ["audio-check", "--directory", root, "--source", root],
            ["audio-check", "--directory", root, "--directory", root],
            ["media-produce", "--request"],
            ["status", "--unknown", root]
        })
        {
            var invalid = await Run(args);
            using var failure = JsonDocument.Parse(invalid.Error);
            Check(invalid.Exit == 1 && invalid.Output.Length == 0
                && failure.RootElement.GetProperty("code").GetString() == "argument.syntax",
                "Response files, ignored globals, duplicate scalars, missing values and unknown flags reject before runtime work with structured stderr.");
        }
        return count;
    }
}
