using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Configuration;

namespace MediaStudio.Configuration;

/// <summary>
/// The only place credentials are created. Entra uses <see cref="DefaultAzureCredential"/> (az / azd / VS / env /
/// managed identity, never an interactive browser). API keys come ONLY from the user's environment or
/// user-secrets; they are never written to settings or the repository, and there is no silent fallback.
/// </summary>
public static class CredentialFactory
{
    public const string UserSecretsId = "MediaStudio.Sample.Development";

    /// <summary>Creates the Entra credential used for every Azure call.</summary>
    public static TokenCredential CreateEntra(string? tenantId = null) =>
        new DefaultAzureCredential(new DefaultAzureCredentialOptions {
            ExcludeInteractiveBrowserCredential = true,
            TenantId = string.IsNullOrWhiteSpace(tenantId) ? null : tenantId
        });

    public static TokenCredential CreateTokenCredential(ConnectionSettings connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.Auth != AuthMode.Entra)
            throw new InvalidOperationException($"Connection '{connection.Id}' uses '{connection.Auth}' authentication, not Entra.");
        return CreateEntra(connection.TenantId);
    }

    /// <summary><c>MEDIASTUDIO__CONNECTIONS__&lt;ID&gt;__APIKEY</c> with the id upper-cased and '-' replaced by '_'.</summary>
    public static string ApiKeyEnvironmentVariable(string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        return "MEDIASTUDIO__CONNECTIONS__" + connectionId.ToUpperInvariant().Replace('-', '_') + "__APIKEY";
    }

    /// <summary><c>MediaStudio:Connections:&lt;id&gt;:ApiKey</c> in user-secrets id <see cref="UserSecretsId"/>.</summary>
    public static string ApiKeyUserSecretName(string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        return $"MediaStudio:Connections:{connectionId}:ApiKey";
    }

    /// <summary>Resolves the API key for an api-key connection: environment variable first, then user-secrets.</summary>
    /// <exception cref="InvalidOperationException">No key is set; the message names the exact variable and secret.</exception>
    public static string GetApiKey(string connectionId, Func<string, string?>? environment = null, IConfiguration? userSecrets = null)
    {
        var variable = ApiKeyEnvironmentVariable(connectionId);
        var secret = ApiKeyUserSecretName(connectionId);
        var key = (environment ?? Environment.GetEnvironmentVariable)(variable);
        if (string.IsNullOrWhiteSpace(key))
        {
            userSecrets ??= LoadUserSecrets();
            key = userSecrets[secret];
        }
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException(
                $"Connection '{connectionId}' uses api-key auth but no key is set. Set the environment variable {variable}, " +
                $"or run: dotnet user-secrets set \"{secret}\" <key> --id {UserSecretsId}. No other credential is tried.");
        key = key.Trim();
        if (key.Length > 4096 || key.Any(char.IsControl))
            throw new InvalidOperationException($"The API key in {variable} / {secret} is malformed.");
        return key;
    }

    public static bool HasApiKey(string connectionId, Func<string, string?>? environment = null, IConfiguration? userSecrets = null)
    {
        try { _ = GetApiKey(connectionId, environment, userSecrets); return true; }
        catch (InvalidOperationException) { return false; }
    }

    private static IConfiguration LoadUserSecrets() =>
        new ConfigurationBuilder().AddUserSecrets(UserSecretsId, reloadOnChange: false).Build();
}
