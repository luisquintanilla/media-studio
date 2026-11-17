namespace MediaStudio.App;

public static class Program
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool AttachConsole(int processId);

    [STAThread]
    public static int Main(string[] args)
    {
        if (args is ["--configure", .. var configureArgs])
        {
            if (!Console.IsOutputRedirected) AttachConsole(-1);
            if (configureArgs is ["production", ..])
                return VideoProduction.Providers.DefaultProductionCommand.Run(["configure", .. configureArgs], Console.Out, MediaApplication.DefaultStateRoot);
            return MediaStudio.Configuration.ConfigureCommands.RunAsync(configureArgs is ["assets", ..] ? configureArgs : ["configure", .. configureArgs], Console.Out,
                checks: [new MediaStudio.Agents.Copilot.CopilotConfigurationCheck(), new MediaStudio.Sources.DocumentConfigurationCheck()]).GetAwaiter().GetResult();
        }
        if (args is ["--diagnostics", .. var diagnosticArgs])
        {
            if (!Console.IsOutputRedirected) AttachConsole(-1);
            return AppDiagnostics.Run(diagnosticArgs, Console.Out, Console.Error);
        }
        return MediaApplication.Run(HybridOptions.Parse(args));
    }
}
