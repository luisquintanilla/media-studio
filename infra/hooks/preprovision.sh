#!/bin/sh
# azd preprovision hook: validates the target region before anything is created.
# Override the region check with: azd env set MEDIASTUDIO_SKIP_REGION_CHECK true
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
finder="$script_dir/../scripts/Find-FoundryRegion.ps1"

if [ -z "${AZURE_PRINCIPAL_TYPE:-}" ]; then
  AZURE_PRINCIPAL_TYPE=User
  export AZURE_PRINCIPAL_TYPE
  if command -v azd >/dev/null 2>&1; then azd env set AZURE_PRINCIPAL_TYPE User >/dev/null; fi
  echo "AZURE_PRINCIPAL_TYPE not set; using User (set ServicePrincipal for CI)."
fi

if [ "${DEPLOY_CLAUDE:-false}" = "true" ] && [ -z "${CLAUDE_ORGANIZATION_NAME:-}" ]; then
  echo "ERROR: DEPLOY_CLAUDE=true requires the Marketplace attestation: azd env set CLAUDE_ORGANIZATION_NAME \"<your org>\" (optional: CLAUDE_COUNTRY_CODE, CLAUDE_INDUSTRY)." >&2
  exit 1
fi

if [ "${MEDIASTUDIO_SKIP_REGION_CHECK:-false}" = "true" ]; then
  echo "WARNING: MEDIASTUDIO_SKIP_REGION_CHECK=true: skipping the region check."
  exit 0
fi

if ! command -v pwsh >/dev/null 2>&1; then
  echo "WARNING: PowerShell 7 (pwsh) not found, so the region check cannot run."
  echo "         Install pwsh (https://aka.ms/powershell) or set MEDIASTUDIO_SKIP_REGION_CHECK=true to silence this."
  exit 0
fi

if [ -z "${AZURE_LOCATION:-}" ]; then
  echo "AZURE_LOCATION is not set; scanning candidate regions."
  pwsh -NoProfile -File "$finder" || true
  echo "ERROR: set a region first: azd env set AZURE_LOCATION <region>" >&2
  exit 1
fi

echo "Checking that $AZURE_LOCATION can host every enabled deployment ..."
code=0
pwsh -NoProfile -File "$finder" -Location "$AZURE_LOCATION" || code=$?
if [ "$code" -eq 1 ]; then
  echo "ERROR: region $AZURE_LOCATION cannot host every enabled deployment (see the matrix above)." >&2
  echo "       Pick the recommended region with 'azd env set AZURE_LOCATION <region>', or override with 'azd env set MEDIASTUDIO_SKIP_REGION_CHECK true'." >&2
  exit 1
fi
if [ "$code" -ne 0 ]; then
  echo "ERROR: region check failed to run (exit $code). Run 'az login', or override with 'azd env set MEDIASTUDIO_SKIP_REGION_CHECK true'." >&2
  exit 1
fi
exit 0