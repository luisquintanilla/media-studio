using AudioTranscriber;

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
return await new CliApplication().RunAsync(
    args,
    Console.Out,
    Console.Error,
    Directory.GetCurrentDirectory(),
    cancel.Token);
