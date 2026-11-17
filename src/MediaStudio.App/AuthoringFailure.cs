using System.ClientModel;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace MediaStudio.App;

public sealed record AuthoringExceptionDetail(string Type, int? HttpStatus, string? RequestId, string? Code,
    string? ArgumentField = null, string? ProviderCode = null, string? ProviderMessage = null);
public sealed record AuthoringFailure(int SchemaVersion, string RunId, DateTimeOffset At,
    string Stage, string[] Events, AuthoringExceptionDetail[] Exceptions)
{
    public string Notice
    {
        get
        {
            var status = Exceptions.FirstOrDefault(e => e.HttpStatus is not null)?.HttpStatus;
            var reason = status switch {
                400 => "The writing model rejected the request (HTTP 400).",
                401 or 403 => $"The writing connection denied access (HTTP {status}). Check its authentication in Settings.",
                429 => "The writing model is rate-limited (HTTP 429).",
                >= 500 => $"The writing service failed (HTTP {status}).",
                _ => "The writing stream failed."
            };
            if (Exceptions.Any(e => e.Code == "draft.part-output"))
                reason = "This project is set to Video. Select Audio or Both in Create for audio parts.";
            else if (Exceptions.Any(e => e.Code == "draft.provider-failed"))
                reason = Exceptions.Any(e => e.ProviderCode?.Contains("rate_limit", StringComparison.OrdinalIgnoreCase) == true)
                    ? "The writing model is rate-limited. Wait before retrying; earlier completed edits remain."
                    : "The writing service reported a failed response. Earlier completed edits remain; review the partial draft before continuing.";
            else if (Exceptions.Any(e => e.Code == "motion.pose"))
                reason = "The diagram edit used invalid positions or keyframes. Keep every shape inside the label-safe canvas, even when invisible, with increasing keyframes starting at 0% and ending at 100%. The invalid edit was not applied.";
            else if (Exceptions.Any(e => e.Code?.StartsWith("motion.", StringComparison.Ordinal) == true))
                reason = "The diagram edit exceeded the supported animation rules. The invalid edit was not applied; revise the shapes, labels or explanatory stages before trying again.";
            else if (Stage == "draft-tool-invocation" && Exceptions.Any(e => e.Type is "System.Text.Json.JsonException"
                or "System.Text.Json.JsonReaderException" or "System.ArgumentException"))
                reason = "The model sent an invalid draft-edit argument.";
            return reason + " Draft changes were kept; Undo is available. No generation started.";
        }
    }
    public static AuthoringFailure Capture(string run, string stage, IEnumerable<string> events, Exception error)
    {
        var details = new List<AuthoringExceptionDetail>();
        for (Exception? current = error; current is not null && details.Count < 8; current = current.InnerException)
        {
            int? status = current is ClientResultException client ? client.Status
                : current is HttpRequestException http && http.StatusCode is { } code ? (int)code : null;
            string? request = null;
            if (current is ClientResultException sdk && sdk.GetRawResponse() is { } response)
                foreach (var header in new[] { "x-request-id", "apim-request-id", "x-ms-request-id" })
                    if (response.Headers.TryGetValue(header, out var value) && value is not null && Safe(value))
                    { request = value; break; }
            details.Add(new(current.GetType().FullName ?? current.GetType().Name, status, request,
                current is AuthoringProviderException ? "draft.provider-failed"
                    : current is VideoProduction.VideoException validation ? validation.Code : null,
                current is System.Text.Json.JsonException json ? ContractField(json.Path) : null,
                (current as AuthoringProviderException)?.ProviderCode,
                (current as AuthoringProviderException)?.ProviderMessage));
        }
        return new(1, run, DateTimeOffset.UtcNow, stage, events.TakeLast(64).ToArray(), details.ToArray());
        static bool Safe(string value) => Regex.IsMatch(value, @"\A[A-Za-z0-9_.:-]{1,120}\z");
        static string? ContractField(string? path)
        {
            var last = path?.Split('.').LastOrDefault();
            string[] fields = ["transaction", "operationId", "draftId", "threadId", "runId",
                "baseVersion", "baseSha256", "operations", "summary", "kind", "itemId",
                "beforeId", "part", "parts", "id", "text", "pauseAfterSamples", "scene",
                "storyboard", "sceneIds", "direction", "targetSeconds", "title", "prompt",
                "audience", "outcome", "citations"];
            return fields.FirstOrDefault(field => field.Equals(last, StringComparison.OrdinalIgnoreCase));
        }
    }
}
