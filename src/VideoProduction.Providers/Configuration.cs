using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using VideoProduction;

namespace VideoProduction.Providers;

public sealed record VideoJobConfiguration(string StateRoot, int MaxPendingJobs = 8);
[JsonConverter(typeof(ChatProviderKindConverter))]
public enum ChatProviderKind { OpenAI, Anthropic }
public sealed class ChatProviderKindConverter() : JsonStringEnumConverter<ChatProviderKind>(
    namingPolicy: System.Text.Json.JsonNamingPolicy.CamelCase, allowIntegerValues: false);
public sealed record PlannerConfiguration(string Endpoint, string Model, string? ApiKeyFile, bool AllowRemote = false,
    string Authentication = "api-key", string? TenantId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Responses = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ApiKeySecret = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] ChatProviderKind Provider = ChatProviderKind.OpenAI);
public static class ProviderUserSecrets
{
    public const string Id = "MediaStudio.Sample.Development";
    public static string Read(string name)
    {
        Guard.Require(System.Text.RegularExpressions.Regex.IsMatch(name,
            @"\AMediaStudio:Providers:[a-z][a-z0-9-]{1,39}:ApiKey\z"),
            "planner.config", "Choose an explicit app provider key reference.");
        using var configuration = new ConfigurationManager();
        configuration.AddUserSecrets(Id, reloadOnChange: false);
        var key = configuration[name];
        if (string.IsNullOrWhiteSpace(key) || key.Length > 4096 || key.Any(char.IsControl))
            throw new InvalidOperationException($"Set user-secret '{name}' for this explicitly chosen connection. No alternate credential/provider was used.");
        return key;
    }
}
public sealed record GenerationConfiguration(string ToolsFile, string FontFile, string FontSha256,
    string FontLicenseFile, string FontLicenseSha256, string? TranscriberConfigFile = null, string? TranscriptDecisionFile = null,
    FrozenProjectConfiguration? FrozenNarration = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AuthoredNarrationPolicy? NarrationPolicy = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ImportedVisual[]? ImportedVisuals = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IllustrationConfiguration? Illustration = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RoutedIllustrationConfiguration? RoutedIllustration = null);
public sealed record IllustrationConfiguration(PlannerConfiguration Connection, bool NativeImage = false);
/// <summary>
/// Illustrations from the settings-routed keyed <c>"illustration"</c> image generator (grounding, critique and the chat-tier
/// router), built by the producer from a reviewed provider-settings snapshot pinned by SHA256. The optional pinned
/// <see cref="SourcesFile"/> holds the project's extracted source passages used for grounding.
/// </summary>
public sealed record RoutedIllustrationConfiguration(string SettingsFile, string SettingsSha256,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SourcesFile = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SourcesSha256 = null);
public sealed record ImportedVisual(string SceneId, string File, string Sha256, string Provenance, string License,
    bool AllowHold = false);
public sealed record FrozenProjectConfiguration(string ProjectFile, string ProjectSha256);
public sealed record BoundTranscriptDecision(string RequestSha256, string IdempotencyHash, string SceneId,
    string MasterSha256, string DerivativeSha256, string SegmentsSha256, string ModelSha256, string ArtifactsSha256,
    string IntendedScript, string ObservedText, string Reason);
public static class PrivateFiles
{
    public static string LocalPath(string path)
    {
        Guard.Require(Path.IsPathFullyQualified(path) && !path.StartsWith(@"\\", StringComparison.Ordinal),
            "config.path", "Configuration paths must be explicit trusted local absolute paths.");
        var full = Path.GetFullPath(path);
        for (string? entry = full; entry is not null; entry = Path.GetDirectoryName(entry))
            if (File.Exists(entry) || Directory.Exists(entry))
                Guard.Require(!File.GetAttributes(entry).HasFlag(FileAttributes.ReparsePoint),
                    "config.path", "Reparse-point configuration paths are not supported.");
        return full;
    }

    public static void Protect(string path, bool directory = false)
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User!;
            var owner = directory
                ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier))
                : new FileInfo(path).GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier));
            Guard.Require(OwnedByCurrentPrincipal(owner, identity), "config.permissions", "Only the current principal's own files/directories may be protected.");
            FileSystemSecurity acl = directory ? new DirectorySecurity() : new FileSecurity();
            acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var inheritance = directory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None;
            foreach (var sid in new[] { user, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
                acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, inheritance,
                    PropagationFlags.None, AccessControlType.Allow));
            Apply(acl);
            if (!user.Equals(owner))
            {
                // The DACL above grants the user WRITE_OWNER, so the user can now take ownership.
                // Best effort: an elevated Administrators owner is already accepted by OwnedByCurrentPrincipal.
                FileSystemSecurity ownerOnly = directory ? new DirectorySecurity() : new FileSecurity();
                ownerOnly.SetOwner(user);
                try { Apply(ownerOnly); }
                catch (UnauthorizedAccessException) { }
            }

            void Apply(FileSystemSecurity security)
            {
                if (directory) new DirectoryInfo(path).SetAccessControl((DirectorySecurity)security);
                else new FileInfo(path).SetAccessControl((FileSecurity)security);
            }
        }
        else File.SetUnixFileMode(path, directory ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            : UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    // Elevated administrators (and Windows Server by default) create objects owned by BUILTIN\Administrators, not the user.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static bool OwnedByCurrentPrincipal(IdentityReference? owner, WindowsIdentity identity) =>
        owner is SecurityIdentifier sid && (sid == identity.User
            || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)
                && new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator));

    public static void RequirePrivate(string path)
    {
        path = LocalPath(path);
        Guard.Require(File.Exists(path), "config.missing", "An explicitly configured private file is missing.");
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User!;
            var acl = new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
            Guard.Require(OwnedByCurrentPrincipal(acl.GetOwner(typeof(SecurityIdentifier)), identity), "config.permissions", "Private file must be owned by the current host principal.");
            var allowed = new[] { user.Value, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value };
            foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                Guard.Require(rule.AccessControlType != AccessControlType.Allow || allowed.Contains(rule.IdentityReference.Value),
                    "config.permissions", "Private files must not grant access to other principals.");
        }
        else Guard.Require((File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite
            | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) == 0,
            "config.permissions", "Private files must have owner-only Unix permissions.");
    }

    public static T Read<T>(string path)
    {
        RequirePrivate(path);
        return Json.Read<T>(path);
    }
}
