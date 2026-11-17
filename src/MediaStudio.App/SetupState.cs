using System.IO;
using MediaStudio.Configuration;

namespace MediaStudio.App;

/// <summary>First-run setup state. Proof and fixture launches never show the wizard or read the user's settings.</summary>
public sealed class SetupState(bool firstRunEligible, ProviderSettingsStore? store = null)
{
    public bool FirstRunEligible { get; } = firstRunEligible;
    public ProviderSettingsStore Store { get; } = store ?? new ProviderSettingsStore();
    public bool ShowOnStartup => FirstRunEligible && !Store.Exists;

    /// <summary>Seeds the app role state from settings v2 when the app has no connections yet (e.g. after <c>configure local</c>).</summary>
    public static void SeedFromSettings(string stateRoot)
    {
        try
        {
            if (new ProviderSettingsStore().TryLoad() is not { } settings) return;
            // The production store only adopts an empty root, so claim it before seeding writes settings into it.
            _ = new VideoProduction.Providers.FileProductionStore(stateRoot);
            var providers = new ModelProviders(stateRoot);
            if (providers.State.Connections.Length > 0) return;
            var projected = ProviderSettingsProjection.Apply(settings, providers.State);
            if (projected.Connections.Length > 0) providers.Save(projected);
        }
        catch (Exception error) when (error is InvalidDataException or IOException or InvalidOperationException or UnauthorizedAccessException
                                          or System.Text.Json.JsonException or ProviderSettingsValidationException)
        {
            Console.Error.WriteLine("Settings were not applied: " + error.Message + " Run `MediaStudio --configure test` or open Settings → Setup.");
        }
    }
}
