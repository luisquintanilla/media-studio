using System.Diagnostics;
using VideoProduction;

internal static class ProcessRegressionTests
{
    public static async Task<int> Run(string root)
    {
        await TrustedArgvStdinAndSanitizedEnvironment(root);
        await ConcurrentBoundedStreamsRetainObservationTiming(root);
        await TypedWaitRetainsExitCodesAndCancellation(root);
        await TimeoutReapsOwnedTree(root);
        return 4;
    }

    private static void Require(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
    }

    private static async Task TrustedArgvStdinAndSanitizedEnvironment(string root)
    {
        var node = Files.FindExecutable("node");
        const string argument = "literal spaces \"quotes\" ; & | $(not-a-command) \u00e9";
        var previous = Environment.GetEnvironmentVariable("MEDIA_UNTRUSTED_ENV");
        try
        {
            Environment.SetEnvironmentVariable("MEDIA_UNTRUSTED_ENV", "must-not-reach-child");
            var output = await Processes.Run(node, ["-e",
                "let s='';process.stdin.setEncoding('utf8');process.stdin.on('data',x=>s+=x);process.stdin.on('end',()=>console.log(JSON.stringify([process.argv[1],s,process.env.MEDIA_UNTRUSTED_ENV??null,process.env.NO_COLOR])));",
                argument], root, default, standardInput: "bounded stdin\n\u00e9");
            var values = System.Text.Json.JsonSerializer.Deserialize<string?[]>(output.Stdout)!;
            Require(values.SequenceEqual(new[] { argument, "bounded stdin\n\u00e9", null, "1" }),
                nameof(TrustedArgvStdinAndSanitizedEnvironment) + ": " + output.Stdout);
        }
        finally { Environment.SetEnvironmentVariable("MEDIA_UNTRUSTED_ENV", previous); }
    }

    private static async Task ConcurrentBoundedStreamsRetainObservationTiming(string root)
    {
        var node = Files.FindExecutable("node");
        var result = await Processes.Run(node, ["-e",
            "process.stdout.write('out-start');process.stderr.write('err-start');setTimeout(()=>{process.stdout.write('out-end');process.stderr.write('err-end')},60)"],
            root, default, maxOutputBytes: 32);
        Require(result.Stdout == "out-startout-end" && result.Stderr == "err-starterr-end"
            && result.Events.Select(e => e.Stream).Distinct().Count() == 2
            && result.Events.All(e => e.ElapsedTicks >= 0) && result.Frequency == Stopwatch.Frequency
            && result.Events.Select(e => e.ElapsedTicks).SequenceEqual(result.Events.Select(e => e.ElapsedTicks).Order()),
            nameof(ConcurrentBoundedStreamsRetainObservationTiming));
        try
        {
            await Processes.Run(node, ["-e",
                "process.stdout.write('x'.repeat(60));process.stderr.write('y'.repeat(60));setInterval(()=>{},1000)"],
                root, default, maxOutputBytes: 100);
            throw new Exception("Aggregate stdout/stderr bound not enforced");
        }
        catch (VideoException error) when (error.Code == "process.output-limit") { }
    }

    private static async Task TypedWaitRetainsExitCodesAndCancellation(string root)
    {
        var node = Files.FindExecutable("node");
        var result = await Processes.Run(node, ["-e", "process.exit(23)"], root, default, requireSuccess: false);
        Require(result.ExitCode == 23, nameof(TypedWaitRetainsExitCodesAndCancellation));
        try
        {
            await Processes.Run(node, ["-e", "process.exit(23)"], root, default);
            throw new Exception("Nonzero exit accepted");
        }
        catch (VideoException error) when (error.Code == "process.failed") { }
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var marker = Path.Combine(root, "precanceled-process-started");
        try
        {
            await Processes.Run(node, ["-e", "require('node:fs').writeFileSync(process.argv[1],'unexpected')", marker],
                root, canceled.Token);
            throw new Exception("Pre-canceled runner started");
        }
        catch (OperationCanceledException error) when (error.CancellationToken == canceled.Token) { }
        Require(!File.Exists(marker), nameof(TypedWaitRetainsExitCodesAndCancellation));
    }

    private static async Task TimeoutReapsOwnedTree(string root)
    {
        var node = Files.FindExecutable("node");
        var path = Path.Combine(root, "timeout-owned-pids.json");
        var running = Processes.Run(node, ["-e", """
            const child=require('node:child_process').spawn(process.execPath,['-e','setInterval(()=>{},1000)'],{stdio:'ignore'});
            require('node:fs').writeFileSync(process.argv[1],JSON.stringify([process.pid,child.pid]));
            setInterval(()=>{},1000);
            """, path], root, default, timeoutSeconds: 2);
        var deadline = Stopwatch.StartNew();
        while (!File.Exists(path) && !running.IsCompleted && deadline.Elapsed < TimeSpan.FromSeconds(1))
            await Task.Delay(10);
        Require(File.Exists(path), nameof(TimeoutReapsOwnedTree));
        var pids = Json.Read<int[]>(path);
        using var parent = Process.GetProcessById(pids[0]);
        using var descendant = Process.GetProcessById(pids[1]);
        try { await running; throw new Exception("Timeout not enforced"); }
        catch (VideoException error) when (error.Code == "process.timeout") { }
        Require(parent.HasExited && descendant.HasExited, nameof(TimeoutReapsOwnedTree));
    }
}
