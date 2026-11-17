namespace MediaStudio.Configuration;

/// <summary>Per-user locations for settings and downloaded models. Nothing here lives in the repository.</summary>
public static class MediaStudioPaths
{
    /// <summary>Environment variable that relocates the whole per-user root (useful for tests and portable installs).</summary>
    public const string HomeVariable = "MEDIASTUDIO_HOME";

    public static string Root
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable(HomeVariable);
            return !string.IsNullOrWhiteSpace(overridden) && Path.IsPathFullyQualified(overridden)
                ? Path.GetFullPath(overridden)
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaStudio");
        }
    }

    public static string SettingsDirectory => Path.Combine(Root, "settings");
    public static string SettingsFile => Path.Combine(SettingsDirectory, ProviderSettingsStore.FileName);
    public static string ModelsRoot => Path.Combine(Root, "models");
}
