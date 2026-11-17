using System.Text.RegularExpressions;

namespace VideoProduction;

public sealed record JellyfinResult(string Id, string Name, string Type);
public sealed record JellyfinProfile(int SchemaVersion, string Origin, string ServerId, string ServerVersion,
    string WebVersion, string PluginIdentity, string CorpusSha256, string UserId, string[] LibraryIds,
    string Query, JellyfinResult ExpectedResult, string SearchEndpoint,
    string[] AllowedApiPaths, string[] AllowedWebsocketPaths, int ResultHoldMilliseconds = 2000,
    ArtifactHash[]? WebAssets = null);

public static partial class JellyfinCapture
{
    [GeneratedRegex("^[a-fA-F0-9]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex ItemId();

    public static void Validate(JellyfinProfile profile)
    {
        Guard.Require(profile.SchemaVersion == 1 && Uri.TryCreate(profile.Origin, UriKind.Absolute, out var origin)
            && origin.Scheme == "http" && origin.Host == "127.0.0.1" && origin.Port is > 1024 and <= 65535
            && origin.AbsolutePath == "/" && origin.Query.Length == 0 && origin.Fragment.Length == 0
            && origin.UserInfo.Length == 0 && profile.Origin == origin.GetLeftPart(UriPartial.Authority),
            "capture.origin", "Jellyfin capture requires an exact dynamic HTTP IPv4 loopback origin without credentials/path/query.");
        Guard.Require(!string.IsNullOrWhiteSpace(profile.ServerId) && !string.IsNullOrWhiteSpace(profile.ServerVersion)
            && !string.IsNullOrWhiteSpace(profile.WebVersion) && !string.IsNullOrWhiteSpace(profile.PluginIdentity)
            && Files.ValidHash(profile.CorpusSha256), "capture.identity", "Pin app/web/plugin/corpus identities.");
        Guard.Require(ItemId().IsMatch(profile.UserId) && profile.LibraryIds.Length > 0
            && profile.LibraryIds.All(i => ItemId().IsMatch(i)) && profile.LibraryIds.Distinct().Count() == profile.LibraryIds.Length,
            "capture.scope", "Pin dedicated nonadmin user and explicit library IDs.");
        Guard.Require(profile.Query.Length is >= 2 and <= 40 && !profile.Query.Any(char.IsControl)
            && ItemId().IsMatch(profile.ExpectedResult.Id) && profile.ExpectedResult.Name.Length is > 0 and <= 200
            && profile.ExpectedResult.Type is "Episode" or "Audio" or "Movie" or "Series" or "Book",
            "capture.query", "Query and expected real native result identity/type are required.");
        Guard.Require(profile.SearchEndpoint is "/Items" or "/Search/Hints" || profile.SearchEndpoint == $"/Users/{profile.UserId}/Items",
            "capture.endpoint", "Only explicitly observed native Items or Search/Hints search endpoints are supported.");
        Guard.Require(profile.WebAssets is { Length: > 0 and <= 5000 }
            && profile.WebAssets.Select(a => a.Path).Distinct(StringComparer.Ordinal).Count() == profile.WebAssets.Length
            && profile.WebAssets.Any(a => a.Path == "web/index.html")
            && profile.WebAssets.All(a => Files.ValidHash(a.Sha256) && a.Path.StartsWith("web/", StringComparison.Ordinal)
                && !a.Path.Contains("..", StringComparison.Ordinal) && !a.Path.Contains("//", StringComparison.Ordinal)
                && Regex.IsMatch(a.Path, "^[a-zA-Z0-9_@./-]+$", RegexOptions.CultureInvariant)
                && !new[] { ".map", ".har", ".log" }.Contains(Path.GetExtension(a.Path), StringComparer.OrdinalIgnoreCase)),
            "capture.web-assets", "Pin an explicit finite list of actual app web assets and SHA-256 hashes, including web/index.html.");
        Guard.Require(profile.AllowedApiPaths.Length is > 0 and <= 128
            && profile.AllowedApiPaths.All(SafeApiPath) && profile.AllowedApiPaths.Contains(profile.SearchEndpoint)
            && profile.AllowedWebsocketPaths.All(p => p == "/socket")
            && profile.ResultHoldMilliseconds is >= 1000 and <= 10000,
            "capture.permissions", "Endpoint permissions must be bounded exact paths or typed item/image prefixes, including the native search endpoint.");
    }

    private static bool SafeApiPath(string path) =>
        new[] { "/System/Info/Public", "/System/Info", "/Branding/Configuration", "/Branding/Css",
            "/Users/Me", "/Users/AuthenticateByName", "/Sessions/Capabilities/Full", "/Sessions/Logout",
            "/Items", "/Search/Hints", "/DisplayPreferences/usersettings", "/Plugins/SemanticDiscovery/Status",
            "/Users/Public", "/QuickConnect/Enabled", "/UserViews", "/SyncPlay/List",
            "/System/Endpoint", "/Playback/BitrateTest", "/UserItems/Resume", "/Shows/NextUp",
            "/Items/Latest", "/Artists", "/Persons", "/Studios" }.Contains(path, StringComparer.OrdinalIgnoreCase)
        || Regex.IsMatch(path, "^/Users/[a-fA-F0-9]{32}(/(Items|Views|Items/Latest|GroupingOptions|Configuration))?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)
        || Regex.IsMatch(path, "^/Users/[a-fA-F0-9]{32}/Items/[a-fA-F0-9]{32}$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)
        || Regex.IsMatch(path, "^/Items/[a-fA-F0-9]{32}(/(Ancestors|ThemeMedia|Collections|Similar)|/Images/(Primary|Backdrop|Thumb|Logo)(/0)?)?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)
        || Regex.IsMatch(path, "^/Shows/[a-fA-F0-9]{32}/(Episodes|Seasons)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static string Scope(Project project) => project.Scenes.Any(s => s.Capture is not null)
        ? "reviewed-jellyfin-loopback" : "trusted-bundled-fixture-only";
}
