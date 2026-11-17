#!/bin/sh
# azd postprovision hook: configures Content Understanding, waits for RBAC, probes Decisions and
# MAI speech (warnings only), then writes the app settings with `configure azd`.
# Never prints tokens or keys. Requires az and curl.
set -u

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
repo_root=$(CDPATH= cd -- "$script_dir/../.." && pwd)
endpoint="${AZURE_FOUNDRY_ENDPOINT:-}"
if [ -z "$endpoint" ]; then echo "ERROR: AZURE_FOUNDRY_ENDPOINT is not set; did provisioning succeed?" >&2; exit 1; fi
case "$endpoint" in */) ;; *) endpoint="$endpoint/" ;; esac
region="${AZURE_LOCATION:-}"
resource_id="${AZURE_FOUNDRY_RESOURCE_ID:-}"
warnings=""
warn() { warnings="${warnings}WARNING: $1
"; }

get_token() {
  az account get-access-token --resource https://cognitiveservices.azure.com --query accessToken -o tsv --only-show-errors 2>/dev/null
}

# http_status METHOD URL AUTH_VALUE [CONTENT_TYPE BODY [EXTRA_HEADER...]] -> prints the HTTP status only.
http_status() {
  method=$1; url=$2; auth=$3; shift 3
  if [ $# -ge 2 ]; then
    ctype=$1; body=$2; shift 2
    printf '%s' "$body" | curl -sS -o /dev/null -w '%{http_code}' -X "$method" "$url" \
      -H "Authorization: Bearer $auth" -H "Content-Type: $ctype" "$@" --data-binary @- --max-time 60 2>/dev/null || echo 000
  else
    curl -sS -o /dev/null -w '%{http_code}' -X "$method" "$url" -H "Authorization: Bearer $auth" --max-time 60 2>/dev/null || echo 000
  fi
}

token=$(get_token)
if [ -z "$token" ]; then echo "ERROR: could not get a token with az account get-access-token (run az login)." >&2; exit 1; fi

# 1. RBAC propagation: poll the OpenAI v1 models list for up to 5 minutes.
echo "Waiting for data-plane access (RBAC propagation, up to 5 min) ..."
rbac_ok=false
i=0
while [ $i -lt 20 ]; do
  status=$(http_status GET "${endpoint}openai/v1/models" "$token")
  if [ "$status" = "200" ]; then rbac_ok=true; break; fi
  sleep 15
  token=$(get_token)
  i=$((i + 1))
done
if [ "$rbac_ok" = "true" ]; then echo "  data-plane access OK"; else warn "RBAC not effective after 5 min; wait a few minutes and run: dotnet run --project src/VideoProduction.Cli -- configure test"; fi

# 2. Content Understanding defaults.
if [ "${MEDIASTUDIO_CU_ENABLED:-false}" = "true" ]; then
  embedding_model="${MEDIASTUDIO_CU_EMBEDDING_MODEL:-text-embedding-3-small}"
  echo "Configuring Content Understanding default model deployments ..."
  if az rest --method patch --resource https://cognitiveservices.azure.com \
       --url "${endpoint}contentunderstanding/defaults?api-version=2025-11-01" \
       --headers "Content-Type=application/json" \
       --body "{\"modelDeployments\":{\"gpt-6-luna\":\"chat\",\"gpt-5.4-mini\":\"mini\",\"${embedding_model}\":\"embedding\"}}" \
       --only-show-errors >/dev/null 2>&1; then
    supported=$(az rest --method get --resource https://cognitiveservices.azure.com \
       --url "${endpoint}contentunderstanding/analyzers/prebuilt-documentSearch?api-version=2025-11-01" \
       --query "supportedModels" -o json --only-show-errors 2>/dev/null || echo "")
    if [ -n "$supported" ] && [ "$supported" != "null" ] && ! printf '%s' "$supported" | grep -q "\"$embedding_model\""; then
      warn "prebuilt-documentSearch does not list $embedding_model in supportedModels. Fix: azd env set CU_EMBEDDING_MODEL text-embedding-3-large; azd provision"
    else
      echo "  Content Understanding defaults set"
    fi
  else
    warn "Content Understanding defaults could not be set; the app falls back to other document paths."
  fi
fi

# 3. Decisions probe (gpt-6-luna deployment "chat").
status=$(http_status POST "${endpoint}openai/v1/decisions" "$token" application/json \
  '{"model":"chat","input":"The invoice total is 42 dollars.","questions":[{"name":"mentions_amount","type":"predicate","instructions":"Does the text mention a monetary amount?"}]}')
if [ "$status" = "200" ]; then echo "Decisions API: OK"; else warn "Decisions API not available here (HTTP $status); the app uses Julia or the heuristic decider."; fi

# 4. MAI-Voice probe: plain bearer on the custom subdomain, then the regional host with aad#.
ssml="<speak version='1.0' xml:lang='en-US'><voice name='en-US-Harper:MAI-Voice-2.1-Flash'>Hello</voice></speak>"
speech_ok=false
status=$(http_status POST "${endpoint}tts/cognitiveservices/v1" "$token" application/ssml+xml "$ssml" \
  -H "X-Microsoft-OutputFormat: audio-16khz-32kbitrate-mono-mp3" -H "User-Agent: media-studio-postprovision")
if [ "$status" = "200" ]; then
  speech_ok=true; echo "Speech (MAI-Voice): OK via the Foundry endpoint"
elif [ -n "$region" ] && [ -n "$resource_id" ]; then
  status=$(http_status POST "https://${region}.tts.speech.microsoft.com/cognitiveservices/v1" "aad#${resource_id}#${token}" application/ssml+xml "$ssml" \
    -H "X-Microsoft-OutputFormat: audio-16khz-32kbitrate-mono-mp3" -H "User-Agent: media-studio-postprovision")
  if [ "$status" = "200" ]; then speech_ok=true; echo "Speech (MAI-Voice): OK via the regional endpoint"; fi
fi
if [ "$speech_ok" != "true" ]; then warn "Speech (MAI) not reachable - the app will use Kokoro/Whisper locally; see docs/troubleshooting.md#speech"; fi
token=""

# 5. Write the app settings.
configure_cmd="dotnet run --project src/VideoProduction.Cli -- configure azd"
dotnet_cmd=""
if [ -x "$repo_root/.dotnet/dotnet" ]; then dotnet_cmd="$repo_root/.dotnet/dotnet"
elif command -v dotnet >/dev/null 2>&1; then dotnet_cmd="dotnet"; fi
if [ -n "$dotnet_cmd" ] && [ -d "$repo_root/src/VideoProduction.Cli" ]; then
  echo "Running: $configure_cmd"
  if ! (cd "$repo_root" && "$dotnet_cmd" run --project src/VideoProduction.Cli -- configure azd); then
    warn "configure azd failed; re-run: $configure_cmd"
  fi
else
  warn "dotnet not found; run this to write the app settings: $configure_cmd"
fi

# 6. Summary.
echo ""
echo "=== Media Studio on Azure ==="
echo "Resource group : ${AZURE_RESOURCE_GROUP:-}"
echo "Region         : $region"
echo "Foundry        : $endpoint"
echo "Project        : ${AZURE_FOUNDRY_PROJECT_ENDPOINT:-}"
echo "Deployments    : ${MEDIASTUDIO_DEPLOYMENTS:-}"
echo 'Cost           : $0 idle - all deployments are pay-per-use GlobalStandard'
printf '%s' "$warnings"
echo ""
echo "Next: ./Start-MediaStudio.ps1 (pwsh)"
echo "Teardown (also purges the soft-deleted account and frees quota): azd down --purge"
exit 0