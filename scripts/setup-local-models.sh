#!/usr/bin/env bash
# Pulls the local Ollama models and downloads the pinned speech/decision assets for this machine's tier, then writes
# local-first settings (`video-production configure local`) and probes them (`configure test`).
# Same steps and tier rules as scripts/Setup-LocalModels.ps1. Needs bash, curl and jq (scripts/install-prerequisites.sh).
#
# Usage: scripts/setup-local-models.sh [--tier S|M|L|XL] [--family qwen3.5|gemma4] [--fast TAG] [--balanced TAG]
#                                      [--vision TAG] [--allow-xl] [--skip-ollama] [--skip-julia] [--yes] [--dry-run]
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
manifest="$repo/assets/model-manifest.json"
ollama_url="http://localhost:11434"
tier="" family="qwen3.5" fast="" balanced="" vision="" allow_xl=0 skip_ollama=0 skip_julia=0 yes=0 dry_run=0

usage() { sed -n '2,8p' "$0" | sed 's/^# \{0,1\}//'; exit "${1:-0}"; }
while [ $# -gt 0 ]; do
  case "$1" in
    --tier) tier="${2:?--tier needs a value}"; shift 2 ;;
    --family) family="${2:?--family needs a value}"; shift 2 ;;
    --fast) fast="${2:?--fast needs a value}"; shift 2 ;;
    --balanced) balanced="${2:?--balanced needs a value}"; shift 2 ;;
    --vision) vision="${2:?--vision needs a value}"; shift 2 ;;
    --allow-xl) allow_xl=1; shift ;;
    --skip-ollama) skip_ollama=1; shift ;;
    --skip-julia) skip_julia=1; shift ;;
    --yes|-y) yes=1; shift ;;
    --dry-run) dry_run=1; shift ;;
    -h|--help) usage 0 ;;
    *) echo "Unknown option: $1" >&2; usage 2 ;;
  esac
done
case "$tier" in ""|S|M|L|XL) ;; *) echo "--tier must be S, M, L or XL" >&2; exit 2 ;; esac
case "$family" in qwen3.5|gemma4) ;; *) echo "--family must be qwen3.5 or gemma4" >&2; exit 2 ;; esac
command -v jq >/dev/null || { echo "jq is required (run scripts/install-prerequisites.sh)." >&2; exit 1; }
command -v curl >/dev/null || { echo "curl is required." >&2; exit 1; }

gb() { awk -v b="$1" 'BEGIN { printf "%.1f GB", b / 1073741824 }'; }
file_size() { if [ -f "$1" ]; then wc -c <"$1" | tr -d ' '; else echo -1; fi; }

# 1. Hardware and tier (mirrors MediaStudio.Configuration.TierSelector, 5 % slack).
if [ "$(uname -s)" = "Darwin" ]; then ram_bytes="$(sysctl -n hw.memsize)"
else ram_bytes="$(awk '/^MemTotal:/ { print $2 * 1024 }' /proc/meminfo)"; fi
vram_mib=0 gpu=""
if command -v nvidia-smi >/dev/null; then
  while IFS=, read -r mib name; do
    mib="${mib// /}"
    if [ -n "$mib" ] && [ "${mib%.*}" -gt "$vram_mib" ]; then vram_mib="${mib%.*}"; gpu="${name# }"; fi
  done < <(nvidia-smi --query-gpu=memory.total,name --format=csv,noheader,nounits 2>/dev/null || true)
fi
detected="$(awk -v ram="$ram_bytes" -v vram="$vram_mib" -v xl="$allow_xl" 'BEGIN {
  r = ram / 1073741824; v = vram / 1024
  if (xl && v >= 24 * 0.95) print "XL"; else if (r >= 32 * 0.95 && v >= 12 * 0.95) print "L";
  else if (r >= 16 * 0.95 || v >= 8 * 0.95) print "M"; else print "S" }')"
[ -n "$tier" ] || tier="$detected"
if [ "$vram_mib" -gt 0 ]; then gpu_text="$(gb $((vram_mib * 1048576))) VRAM ($gpu)"; else gpu_text="no discrete GPU detected"; fi
echo "Hardware: $(gb "$ram_bytes") RAM, $gpu_text"
echo "Tier: $tier$([ "$tier" != "$detected" ] && echo " (detected $detected)")   Family: $family"

tier_value() { jq -r --arg t "$tier" --arg k "$1" '.ollamaTiers[$t][$k]' "$manifest"; }
default_fast="$(tier_value fast)" default_balanced="$(tier_value balanced)" default_vision="$(tier_value vision)"
embedding="$(tier_value embedding)" whisper_asset="$(tier_value whisperAsset)"
fast_tag="$default_fast" balanced_tag="$default_balanced" vision_tag="$default_vision"
if [ "$family" != "qwen3.5" ]; then
  family_value() { jq -r --arg f "$family" --arg t "$tier" --arg k "$1" '.ollamaFamilies[$f][$t][$k]' "$manifest"; }
  fast_tag="$(family_value fast)" balanced_tag="$(family_value balanced)" vision_tag="$(family_value vision)"
fi
[ -z "$fast" ] || fast_tag="$fast"
[ -z "$balanced" ] || balanced_tag="$balanced"
[ -z "$vision" ] || vision_tag="$vision"
echo "Models: fast=$fast_tag balanced=$balanced_tag vision=$vision_tag embedding=$embedding"
tags="$(printf '%s\n' "$fast_tag" "$balanced_tag" "$vision_tag" "$embedding" | awk '!seen[$0]++')"

# 2. Ollama state.
pulls=()
if [ "$skip_ollama" -eq 0 ]; then
  command -v ollama >/dev/null || { echo "Ollama is not installed. Run scripts/install-prerequisites.sh (or pass --skip-ollama)." >&2; exit 1; }
  installed="$(curl -fsS --max-time 3 "$ollama_url/api/tags" 2>/dev/null | jq -r '.models[].name' || true)"
  if [ -z "$installed" ] && ! curl -fsS --max-time 3 "$ollama_url/api/tags" >/dev/null 2>&1; then
    if [ "$dry_run" -eq 1 ]; then echo "  (Ollama is not running at $ollama_url; every tag is listed as a pull)"
    else
      echo "Starting the Ollama server..."
      nohup ollama serve >/dev/null 2>&1 &
      for _ in $(seq 1 20); do sleep 0.5; curl -fsS --max-time 2 "$ollama_url/api/tags" >/dev/null 2>&1 && break; done
      installed="$(curl -fsS --max-time 3 "$ollama_url/api/tags" | jq -r '.models[].name')" || { echo "Ollama did not answer at $ollama_url." >&2; exit 1; }
    fi
  fi
  while IFS= read -r tag; do
    with_latest="$tag"; [[ "$tag" == *:* ]] || with_latest="$tag:latest"
    if printf '%s\n' "$installed" | grep -qixF -e "$tag" -e "$with_latest"; then echo "  present  $tag"
    else echo "  pull     $tag"; pulls+=("$tag"); fi
  done <<<"$tags"
fi

# 3. Asset state (`assets ensure` re-uses files that are already present and verified).
asset_ids=("kokoro-v1.0-q8" "$whisper_asset")
[ "$skip_julia" -eq 1 ] || asset_ids+=("julia-1")
if [ "$(uname -s)" = "Darwin" ]; then data_home="$HOME/Library/Application Support"; else data_home="${XDG_DATA_HOME:-$HOME/.local/share}"; fi
models_root="${MEDIASTUDIO_HOME:-$data_home/MediaStudio}/models"
asset_bytes=0
for id in "${asset_ids[@]}"; do
  bytes=0
  while IFS=$'\t' read -r dir path size; do
    [ "$(file_size "$models_root/$dir/$path")" = "$size" ] || bytes=$((bytes + size))
  done < <(jq -r --arg id "$id" '.assets[] | select(.id == $id) | .dir as $d | .files[] | [$d, .path, .size] | @tsv' "$manifest")
  asset_bytes=$((asset_bytes + bytes))
  if [ "$bytes" -gt 0 ]; then echo "  download $id ($(gb "$bytes"))"; else echo "  present  $id"; fi
done

ollama_bytes=0 unknown=""
for tag in "${pulls[@]+"${pulls[@]}"}"; do
  name="${tag%%:*}" version="latest"; [[ "$tag" == *:* ]] && version="${tag#*:}"
  [[ "$name" == */* ]] || name="library/$name"
  size="$(curl -fsS --max-time 10 -H 'Accept: application/vnd.docker.distribution.manifest.v2+json' \
    "https://registry.ollama.ai/v2/$name/manifests/$version" 2>/dev/null | jq '[.layers[].size] | add' 2>/dev/null || true)"
  if [[ "$size" =~ ^[0-9]+$ ]]; then ollama_bytes=$((ollama_bytes + size)); else unknown=" + unknown"; fi
done
echo "Total download: $(gb $((ollama_bytes + asset_bytes)))$unknown (Ollama $(gb "$ollama_bytes"), assets $(gb "$asset_bytes"))"

if [ "$dry_run" -eq 1 ]; then echo "Dry run: nothing is downloaded or written. It would run:"
elif [ "$yes" -eq 0 ] && { [ "${#pulls[@]}" -gt 0 ] || [ "$asset_bytes" -gt 0 ]; }; then
  read -r -p "Continue? [y/N] " answer
  case "$answer" in y|Y|yes|YES) ;; *) echo "Cancelled."; exit 1 ;; esac
fi

# 4. Pull, ensure, configure, test.
run() { echo "> $*"; [ "$dry_run" -eq 1 ] || "$@"; }
for tag in "${pulls[@]+"${pulls[@]}"}"; do run ollama pull "$tag"; done

if [ -x "$repo/.dotnet/dotnet" ]; then dotnet="$repo/.dotnet/dotnet"; export DOTNET_ROOT="$repo/.dotnet"
elif command -v dotnet >/dev/null; then dotnet="$(command -v dotnet)"
elif [ -x "$HOME/.dotnet/dotnet" ]; then dotnet="$HOME/.dotnet/dotnet"; export DOTNET_ROOT="$HOME/.dotnet"
elif [ "$dry_run" -eq 1 ]; then dotnet="dotnet"
else echo "The .NET 11 SDK was not found. Run scripts/install-prerequisites.sh first." >&2; exit 1; fi
cli="$repo/src/VideoProduction.Cli/bin/Release/net11.0/video-production.dll"
run "$dotnet" build "$repo/src/VideoProduction.Cli/VideoProduction.Cli.csproj" -c Release --nologo -v q
vp() { run "$dotnet" "$cli" "$@"; }

if [ "$skip_julia" -eq 1 ]; then for id in "${asset_ids[@]}"; do vp assets ensure --id "$id"; done
else vp assets ensure --tier "$tier"; fi

configure=(configure local --tier "$tier")
[ "$tier" != "XL" ] || configure+=(--allow-xl)
[ "$fast_tag" = "$default_fast" ] || configure+=(--fast "$fast_tag")
[ "$balanced_tag" = "$default_balanced" ] || configure+=(--balanced "$balanced_tag")
[ "$vision_tag" = "$default_vision" ] || configure+=(--vision "$vision_tag")
vp "${configure[@]}"

if vp configure test; then
  [ "$dry_run" -eq 1 ] || echo "Local models are ready. The CLI is: dotnet $cli"
else
  echo "Some roles failed 'configure test'; see the fixes above." >&2; exit 1
fi
