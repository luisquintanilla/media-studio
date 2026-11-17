using System.Diagnostics;
using System.Text.Json;

namespace MediaStudio.Configuration;

/// <summary>The subset of <c>azd env get-values</c> (infra/main.bicep outputs) that configures Media Studio. Contains no secrets.</summary>
public sealed record AzdOutputs(
    string FoundryEndpoint,
    string? ProjectEndpoint,
    string? Location,
    string? ResourceId,
    string? ResourceGroup,
    string? FoundryName,
    IReadOnlyDictionary<string, string> Deployments,
    bool ContentUnderstanding,
    string? ContentUnderstandingEmbeddingModel,
    bool Claude,
    bool OpenAIAudio,
    bool MistralOcr,
    string? TenantId = null);

/// <summary>Implements <c>configure azd</c>: parse azd outputs and merge them into settings. Never reads or writes keys.</summary>
public static class AzdEnvironmentImporter
{
    private static readonly string[] SecretMarkers = ["KEY", "SECRET", "PASSWORD", "TOKEN", "CONNECTION_STRING"];

    /// <summary>Parses <c>azd env get-values --output json</c> or the default dotenv (<c>NAME="value"</c>) output.</summary>
    public static AzdOutputs Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var values = ParseValues(output);
        string? Value(string name) => values.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
        bool Flag(string name) => Value(name) is { } v && (bool.TryParse(v, out var b) ? b : v == "1");

        var endpoint = Value("AZURE_FOUNDRY_ENDPOINT") ?? Value("AZURE_FOUNDRY_PROJECT_ENDPOINT")
            ?? throw new InvalidDataException("azd outputs do not include AZURE_FOUNDRY_ENDPOINT. Run `azd up` (or `azd provision`) from the repo root first, then retry.");
        if (!FoundryEndpoints.TryNormalizeRoot(endpoint, out var root, out var error))
            throw new InvalidDataException("AZURE_FOUNDRY_ENDPOINT is not usable: " + error);

        var deployments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Value("MEDIASTUDIO_DEPLOYMENTS") is { } json)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                foreach (var property in document.RootElement.EnumerateObject())
                    if (property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { Length: > 0 } name)
                        deployments[property.Name] = name;
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("MEDIASTUDIO_DEPLOYMENTS is not a JSON object of role → deployment name.", exception);
            }
        }

        return new AzdOutputs(
            root.AbsoluteUri,
            Value("AZURE_FOUNDRY_PROJECT_ENDPOINT"),
            Value("AZURE_LOCATION")?.ToLowerInvariant(),
            Value("AZURE_FOUNDRY_RESOURCE_ID"),
            Value("AZURE_RESOURCE_GROUP"),
            Value("AZURE_FOUNDRY_NAME"),
            deployments,
            Flag("MEDIASTUDIO_CU_ENABLED"),
            Value("MEDIASTUDIO_CU_EMBEDDING_MODEL"),
            Flag("MEDIASTUDIO_CLAUDE_ENABLED"),
            Flag("MEDIASTUDIO_OPENAI_AUDIO_ENABLED"),
            Flag("MEDIASTUDIO_MISTRAL_OCR_ENABLED"),
            Value("AZURE_TENANT_ID"));
    }

    /// <summary>Builds the <c>azure</c> connection (Entra) and merges tiers/documents into <paramref name="settings"/>; turns on allowCloud.</summary>
    public static ProviderSettings Apply(ProviderSettings settings, AzdOutputs outputs, string? tenantId = null, bool cloudSpeech = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(outputs);
        var existing = settings.FindConnection(ProviderSettingsDefaults.AzureConnectionId);
        var azure = new ConnectionSettings {
            Id = ProviderSettingsDefaults.AzureConnectionId,
            Kind = ConnectionKind.Foundry,
            Endpoint = outputs.FoundryEndpoint,
            Auth = existing?.Auth == AuthMode.ApiKey ? AuthMode.ApiKey : AuthMode.Entra,
            TenantId = tenantId ?? outputs.TenantId ?? existing?.TenantId,
            Region = outputs.Location,
            ResourceId = outputs.ResourceId
        };
        return ProviderSettingsDefaults.WithAzure(
            settings,
            azure,
            outputs.Deployments.Count > 0 ? outputs.Deployments : null,
            allowCloud: true,
            cloudSpeech: cloudSpeech,
            documentIntelligence: true,
            contentUnderstanding: outputs.ContentUnderstanding,
            mistralOcr: outputs.MistralOcr,
            claude: outputs.Claude);
    }

    /// <summary>Runs <c>azd env get-values --output json [--environment name]</c> in <paramref name="workingDirectory"/>.</summary>
    public static async Task<string> RunAzdAsync(string? environment = null, string? workingDirectory = null, CancellationToken cancellationToken = default)
    {
        var start = new ProcessStartInfo("azd") {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory
        };
        foreach (var argument in new[] { "env", "get-values", "--output", "json" }) start.ArgumentList.Add(argument);
        if (!string.IsNullOrWhiteSpace(environment)) { start.ArgumentList.Add("--environment"); start.ArgumentList.Add(environment); }
        Process process;
        try { process = Process.Start(start) ?? throw new InvalidOperationException("azd did not start."); }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new InvalidOperationException("The Azure Developer CLI (azd) was not found. Install it (winget install Microsoft.Azd) or pass --from-file with saved `azd env get-values` output.", exception);
        }
        using (process)
        {
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errors = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"`azd env get-values` failed ({process.ExitCode}): {(await errors.ConfigureAwait(false)).Trim()} Run it from the folder that contains azure.yaml, or pass --env <name>.");
            return await output.ConfigureAwait(false);
        }
    }

    internal static Dictionary<string, string> ParseValues(string output)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var trimmed = output.TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
        if (trimmed.StartsWith('{'))
        {
            using var document = JsonDocument.Parse(trimmed);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (IsSecretName(property.Name)) continue;
                values[property.Name] = property.Value.ValueKind switch {
                    JsonValueKind.String => property.Value.GetString() ?? "",
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => property.Value.GetRawText()
                };
            }
            return values;
        }
        foreach (var raw in trimmed.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("export ", StringComparison.Ordinal)) line = line[7..];
            var equals = line.IndexOf('=');
            if (equals <= 0) continue;
            var name = line[..equals].Trim();
            if (IsSecretName(name)) continue;
            values[name] = Unquote(line[(equals + 1)..].Trim());
        }
        return values;
    }

    private static bool IsSecretName(string name) => SecretMarkers.Any(marker => name.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            return value[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'') return value[1..^1];
        return value;
    }
}
