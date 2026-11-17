#!/usr/bin/env bash
# Makes the repository's media-studio-* agent skills visible to Claude Code (.claude/skills) and OpenAI Codex
# (.agents/skills) by symlinking them to .github/skills. Opt-in; the links are ignored by git. Same behaviour as
# scripts/Link-AgentSkills.ps1. GitHub Copilot already reads .github/skills, so Copilot users don't need this.
#
# Usage: scripts/link-agent-skills.sh [--agent claude|codex]... [--remove] [--dry-run]
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source_dir="$repo/.github/skills"
agents=() remove=0 dry_run=0

usage() { sed -n '2,6p' "$0" | sed 's/^# \{0,1\}//'; exit "${1:-0}"; }
while [ $# -gt 0 ]; do
  case "$1" in
    --agent)
      case "${2:-}" in claude|codex) agents+=("$2") ;; *) echo "--agent must be claude or codex" >&2; usage 2 ;; esac
      shift 2 ;;
    --remove) remove=1; shift ;;
    --dry-run) dry_run=1; shift ;;
    -h|--help) usage 0 ;;
    *) echo "Unknown option: $1" >&2; usage 2 ;;
  esac
done
[ ${#agents[@]} -eq 0 ] && agents=(claude codex)

run() { if [ "$dry_run" -eq 1 ]; then echo "  would run: $*"; else "$@"; fi; }

shopt -s nullglob
skills=()
for dir in "$source_dir"/media-studio-*/; do
  [ -f "$dir/SKILL.md" ] && skills+=("$(basename "$dir")")
done
[ ${#skills[@]} -eq 0 ] && { echo "No media-studio-* skills found under $source_dir." >&2; exit 1; }

for agent in "${agents[@]}"; do
  case "$agent" in claude) root="$repo/.claude/skills" ;; codex) root="$repo/.agents/skills" ;; esac
  echo "$agent -> $root"
  for name in "${skills[@]}"; do
    link="$root/$name"
    target="../../.github/skills/$name"
    if [ "$remove" -eq 1 ]; then
      if [ -L "$link" ] && [ "$(readlink "$link")" = "$target" ]; then
        run rm "$link"
        [ "$dry_run" -eq 1 ] || echo "  removed $name"
      fi
      continue
    fi
    if [ -L "$link" ] && [ "$(readlink "$link")" = "$target" ]; then echo "  ok      $name"; continue; fi
    if [ -e "$link" ] || [ -L "$link" ]; then
      echo "warning: $link already exists and is not a link to $target; leaving it alone." >&2
      continue
    fi
    run mkdir -p "$root"
    if [ "$dry_run" -eq 1 ]; then echo "  would link $name -> $target"; continue; fi
    (cd "$root" && ln -s "$target" "$name")
    echo "  linked  $name"
  done
  if [ "$remove" -eq 1 ]; then
    for dir in "$root" "$(dirname "$root")"; do
      if [ -d "$dir" ] && [ -z "$(ls -A "$dir")" ]; then run rmdir "$dir"; fi
    done
  fi
done
[ "$remove" -eq 0 ] && echo "Restart the agent (or start a new session) so it picks up the skills."
exit 0
