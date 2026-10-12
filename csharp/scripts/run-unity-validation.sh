#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
: "${UNITY_EDITOR:?Set UNITY_EDITOR to the installed Unity 2022.3+ executable.}"
[[ -x "$UNITY_EDITOR" ]] || { printf 'Unity Editor not executable: %s\n' "$UNITY_EDITOR" >&2; exit 2; }
command -v python3 >/dev/null || { printf 'Python 3 required for the independent report gate.\n' >&2; exit 2; }
project="$root/csharp/UnityTestProject"
results="$project/TestResults/angelscript-validation-editor.json"
mkdir -p "$project/TestResults"
rm -f "$results" "$results.tmp"
args=()
if [[ "${1:-}" == "--full-port" ]]; then args+=(--angelscript-full-port); elif [[ $# -gt 0 ]]; then printf 'Usage: run-unity-validation.sh [--full-port]\n' >&2; exit 2; fi
# The executeMethod exits with its own status; do not add -quit or trust a stale JSON result.
"$UNITY_EDITOR" -batchmode -nographics -projectPath "$project" \
  -executeMethod AngelScript.Burst.Validation.Editor.ValidationSceneTools.RunBatch \
  --angelscript-report "$results" "${args[@]}" \
  -logFile "$project/TestResults/validation-editor.log"
python3 "$root/csharp/scripts/verify_validation_report.py" "$results" "${args[@]/--angelscript-full-port/--full-port}"
