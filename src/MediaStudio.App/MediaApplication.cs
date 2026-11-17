using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.AspNetCore.Components.WebView;
using Microsoft.AspNetCore.Components.WebView.Wpf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Web.WebView2.Core;
using VideoProduction;

namespace MediaStudio.App;

/// <summary>A scripted check that runs inside a real Media Studio window (see <see cref="AppDiagnostics"/>).</summary>
/// <param name="Banner">Text shown above the web view while the check runs, or null for none.</param>
/// <param name="RunAsync">Runs once the web view is ready; the window closes when it returns.</param>
internal sealed record WindowDiagnostic(string? Banner, Func<CoreWebView2, Window, MediaWorkspace, TextBlock, Task> RunAsync);

public static class MediaApplication
{
    /// <summary>The app's default private state root (projects, attempts and the derived production setup).</summary>
    public static string DefaultStateRoot => Path.Combine(AppContext.BaseDirectory, "private-media");

    public static int Run(HybridOptions options) => Run(options, null);

    internal static int Run(HybridOptions options, WindowDiagnostic? diagnostic)
    {
        var root = options.StateRoot ?? DefaultStateRoot;
        var normalLaunch = options.StateRoot is null && diagnostic is null;
        if (normalLaunch) SetupState.SeedFromSettings(root);
        // Without --media-config the app derives its production setup from the saved settings (an explicit file always wins).
        var defaultProduction = diagnostic is null
            ? VideoProduction.Providers.DefaultProductionConfiguration.ForLaunch(options.MediaConfig, root,
                () => new MediaStudio.Configuration.ProviderSettingsStore().TryLoad() ?? new MediaStudio.Configuration.ProviderSettings())
            : null;
        var workspace = new MediaWorkspace(root, options.MediaConfig, options.Authoring, options.VoiceConfig, null, options.SourceConfig,
            options.NewProject, defaultProduction: defaultProduction);
        var collection = new ServiceCollection().AddSingleton(workspace).AddSingleton(new SetupState(normalLaunch));
        collection.AddMediaStudioSpeech();
        collection.AddWpfBlazorWebView();
        var providerSettings = normalLaunch ? new MediaStudio.Configuration.ProviderSettingsStore().TryLoad() : null;
        if (providerSettings is not null)
        {
            MediaStudio.Agents.Copilot.CopilotServiceCollectionExtensions.AddMediaStudioCopilot(collection, providerSettings);
            VideoProduction.Providers.ProviderFactory.AddMediaStudioAI(collection, providerSettings);
            collection.AddSingleton<MediaStudio.Sources.IUploadConsent>(workspace.UploadConsent);
            MediaStudio.Sources.DocumentServiceCollectionExtensions.AddMediaStudioDocuments(collection, providerSettings);
            workspace.DocumentExtractorChoices = [.. MediaStudio.Sources.DocumentExtractorFactory.Available(providerSettings).Where(c => c != MediaStudio.Configuration.DocumentCandidates.PdfPig)];
        }
        using var services = collection.BuildServiceProvider();
        workspace.DocumentExtractors = key => services.GetKeyedService<Microsoft.Extensions.DocumentExtraction.DocumentExtractionClient>(key);
        workspace.AttachServices(services, providerSettings);
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var initialized = false;
        var browser = new VerifiedResultWebView(workspace.OpenPreview) {
            HostPage = Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html"), Services = services
        };
        browser.BlazorWebViewInitializing += (_, args) => args.UserDataFolder = Path.Combine(root, "webview-profile");
        browser.UrlLoading += (_, args) =>
        {
            if (args.Url.Scheme != "https" || args.Url.Host != "0.0.0.1")
                args.UrlLoadingStrategy = UrlLoadingStrategy.CancelLoad;
        };
        browser.BlazorWebViewInitialized += (_, args) =>
        {
            initialized = true;
            args.WebView.CoreWebView2.Environment.BrowserProcessExited += (_, _) => stopped.TrySetResult();
        };
        browser.RootComponents.Add(new RootComponent { Selector = "#app", ComponentType = typeof(MediaHome) });
        var banner = new TextBlock { Margin = new Thickness(8), Text = diagnostic?.Banner ?? "" };
        var content = new DockPanel();
        if (diagnostic?.Banner is not null)
        {
            DockPanel.SetDock(banner, Dock.Top);
            content.Children.Add(banner);
        }
        content.Children.Add(browser);
        var window = new Window { Title = "Media Studio", MinWidth = 800, MinHeight = 600, Width = 1180, Height = 800, Content = content };
        var exitCode = 0;
        window.Closed += async (_, _) =>
        {
            try
            {
                await browser.DisposeAsync();
                await workspace.DisposeAsync();
                if (initialized) await stopped.Task.WaitAsync(TimeSpan.FromSeconds(15));
            }
            catch (Exception error) { Console.Error.WriteLine("Media window cleanup failed: " + error); exitCode = 1; }
            application.Shutdown(exitCode);
        };
        if (diagnostic is not null)
            browser.BlazorWebViewInitialized += async (_, args) =>
            {
                try { await diagnostic.RunAsync(args.WebView.CoreWebView2, window, workspace, banner); }
                catch (Exception error) { exitCode = 1; Console.Error.WriteLine(error); }
                window.Close();
            };
        application.Run(window);
        return exitCode;
    }
}
