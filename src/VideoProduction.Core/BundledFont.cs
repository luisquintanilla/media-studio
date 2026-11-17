namespace VideoProduction;

/// <summary>
/// The default render font: Inter 4.1 Regular (https://github.com/rsms/inter, SIL Open Font License 1.1).
/// It ships in <c>assets/fonts/</c> next to its license and is copied to every app/CLI output directory.
/// </summary>
public static class BundledFont
{
    public const string FileName = "Inter-Regular.ttf";
    public const string LicenseFileName = "OFL.txt";
    /// <summary>SHA-256 of <c>extras/ttf/Inter-Regular.ttf</c> from the official Inter-4.1.zip release.</summary>
    public const string Sha256 = "40d692fce188e4471e2b3cba937be967878f631ad3ebbbdcd587687c7ebe0c82";
    public const string Description = "Inter 4.1 Regular (rsms/inter); bundled default render font";
    public const string License = "SIL Open Font License 1.1; adjacent OFL.txt";

    public static string Directory => Path.Combine(AppContext.BaseDirectory, "assets", "fonts");
    public static string FontFile => Path.Combine(Directory, FileName);
    public static string LicenseFile => Path.Combine(Directory, LicenseFileName);

    /// <summary>Returns the bundled font path after checking that the font and its license are present and unmodified.</summary>
    public static string RequireFontFile()
    {
        Guard.Require(File.Exists(FontFile) && File.Exists(LicenseFile), "font.bundled",
            $"The bundled font is missing from '{Directory}'. Rebuild so assets/fonts is copied to the output.");
        Guard.Require(Files.Hash(FontFile) == Sha256, "font.bundled", $"The bundled font at '{FontFile}' does not match its pinned SHA-256.");
        return FontFile;
    }
}
