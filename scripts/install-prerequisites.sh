#!/usr/bin/env bash
# Installs the Media Studio CLI prerequisites on macOS/Linux (idempotent): .NET 11 SDK (official dotnet-install
# into ./.dotnet), Ollama, eSpeak NG, FFmpeg, jq and Node.js 24 (Homebrew on macOS, apt on Debian/Ubuntu).
# The desktop app is Windows-only; on macOS/Linux these cover the CLI and scripts/setup-local-models.sh.
# Usage: scripts/install-prerequisites.sh [--dry-run] [--skip <dotnet|ollama|espeak-ng|ffmpeg|node|jq|browser-worker>]...
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dry_run=0
skip=" "
while [ $# -gt 0 ]; do
  case "$1" in
    --dry-run) dry_run=1 ;;
    --skip) skip="$skip$2 "; shift ;;
    -h|--help) sed -n '2,5p' "$0"; exit 0 ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
  shift
done

skipped() { case "$skip" in *" $1 "*) return 0 ;; *) return 1 ;; esac; }
run() {
  if [ "$dry_run" -eq 1 ]; then echo "  would run: $*"; else "$@"; fi
}
have() { command -v "$1" >/dev/null 2>&1; }
status=()
missing_required=0
report() { status+=("$(printf '%-15s %-14s %s' "$1" "$2" "$3")"); }

os="$(uname -s)"
pkg=""
if [ "$os" = "Darwin" ] && have brew; then pkg=brew
elif have apt-get; then pkg=apt
fi
sudo_cmd=""
if [ "$pkg" = apt ] && [ "$(id -u)" -ne 0 ]; then sudo_cmd=sudo; fi

pkg_install() { # $1 = brew formula, $2 = apt package
  case "$pkg" in
    brew) run brew install "$1" ;;
    apt) run $sudo_cmd apt-get install -y "$2" ;;
    *) echo "  no supported package manager (brew/apt); install $1 manually" >&2; return 1 ;;
  esac
}

if [ "$pkg" = apt ] && [ "$dry_run" -eq 0 ]; then $sudo_cmd apt-get update -qq || true; fi

dotnet_sdk() {
  for d in "$repo/.dotnet/dotnet" "$(command -v dotnet 2>/dev/null || true)"; do
    if [ -n "$d" ] && [ -x "$d" ]; then
      v="$("$d" --list-sdks 2>/dev/null | awk '/^11\./ {print $1}' | tail -n 1)"
      if [ -n "$v" ]; then echo "$v"; return 0; fi
    fi
  done
  return 1
}

# component name, detect command, install function, required(1/0), manual hint
component() {
  local name="$1" detect="$2" install="$3" required="$4" hint="$5" found
  if skipped "$name"; then report "$name" skipped ""; return; fi
  found="$($detect || true)"
  if [ -z "$found" ]; then
    $install || true
    if [ "$dry_run" -eq 1 ]; then report "$name" "would install" "$hint"; return; fi
    hash -r
    found="$($detect || true)"
  fi
  if [ -n "$found" ]; then report "$name" ok "$found"
  elif [ "$required" -eq 1 ]; then report "$name" MISSING "$hint"; missing_required=1
  else report "$name" missing "$hint"
  fi
}

install_dotnet() {
  if [ "$dry_run" -eq 1 ]; then echo "  would run: curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 11.0 --install-dir $repo/.dotnet"; return; fi
  curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 11.0 --install-dir "$repo/.dotnet"
}
detect_ollama() { have ollama && ollama --version 2>&1 | sed -e '/could not connect/d' -e 's/^Warning: *//' | head -n 1; }
install_ollama() {
  if [ "$pkg" = brew ]; then run brew install ollama; return; fi
  if [ "$dry_run" -eq 1 ]; then echo "  would run: curl -fsSL https://ollama.com/install.sh | sh"; return; fi
  curl -fsSL https://ollama.com/install.sh | sh
}
detect_espeak() { have espeak-ng && espeak-ng --version 2>/dev/null | head -n 1 | cut -c1-40; }
detect_ffmpeg() { have ffmpeg && ffmpeg -version 2>/dev/null | head -n 1 | sed 's/ Copyright.*//'; }
detect_jq() { have jq && jq --version; }
detect_node() {
  have node || return 1
  v="$(node --version)"
  major="${v#v}"; major="${major%%.*}"
  if [ "$major" -eq 24 ]; then echo "$v"
  elif [ "$major" -gt 24 ]; then echo "$v (workers/browser declares Node 24; newer majors are untested)"
  else return 1
  fi
}
install_node() {
  if [ "$pkg" = brew ]; then run brew install node@24; echo "  note: brew's node@24 is keg-only; run 'brew link --overwrite node@24' or add it to PATH"; return; fi
  echo "  install Node.js 24 via NodeSource (https://github.com/nodesource/distributions) or nvm: nvm install 24" >&2
  return 1
}

component dotnet dotnet_sdk install_dotnet 1 "https://dotnet.microsoft.com/download/dotnet/11.0"
component jq detect_jq "pkg_install jq jq" 1 "https://jqlang.org/download/ (used by setup-local-models.sh)"
component ffmpeg detect_ffmpeg "pkg_install ffmpeg ffmpeg" 0 "https://ffmpeg.org/download.html (video rendering)"
component node detect_node install_node 0 "Node.js 24 from https://nodejs.org/ (browser worker)"
component espeak-ng detect_espeak "pkg_install espeak-ng espeak-ng" 0 "https://github.com/espeak-ng/espeak-ng (local Kokoro narration)"
component ollama detect_ollama install_ollama 0 "https://ollama.com/download (local models)"

if ! skipped browser-worker; then
  if [ -d "$repo/workers/browser/node_modules/playwright" ]; then report browser-worker ok "node_modules present"
  elif ! have npm; then report browser-worker missing "npm not found; install Node.js 24 first"
  elif [ "$dry_run" -eq 1 ]; then report browser-worker "would install" "npm ci; npx playwright install --with-deps chromium"
  elif (cd "$repo/workers/browser" && npm ci --no-audit --no-fund && npx playwright install --with-deps chromium); then report browser-worker ok "npm ci + playwright chromium"
  else report browser-worker missing "npm ci failed"
  fi
fi

echo
printf '%s\n' "${status[@]}"
if [ -x "$repo/.dotnet/dotnet" ] && ! have dotnet; then echo "note: .NET was installed to $repo/.dotnet; export PATH=\"$repo/.dotnet:\$PATH\" DOTNET_ROOT=\"$repo/.dotnet\""; fi
if [ "$missing_required" -eq 1 ]; then echo "Some required components are missing; see the links above." >&2; exit 1; fi
if [ "$dry_run" -eq 0 ]; then echo "Next: scripts/setup-local-models.sh"; fi
