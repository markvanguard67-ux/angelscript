#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
: "${UNITY_EDITOR:?Set UNITY_EDITOR to the installed Unity 2022.3+ executable.}"
[[ -x "$UNITY_EDITOR" ]] || { printf 'Unity Editor not executable: %s\n' "$UNITY_EDITOR" >&2; exit 2; }
command -v python3 >/dev/null || { printf 'Python 3 required to verify test XML.\n' >&2; exit 2; }
project="$root/csharp/UnityTestProject"
mkdir -p "$project/TestResults"
for mode in Editor PlayMode; do
  platform=EditMode
  fixture=ValidationHarnessTests
  if [[ "$mode" == PlayMode ]]; then platform=PlayMode; fixture=ValidationSceneTests; fi
  results="$project/TestResults/validation-$mode.xml"
  rm -f "$results"
  "$UNITY_EDITOR" -batchmode -nographics -projectPath "$project" \
    -runTests -testPlatform "$platform" \
    -testFilter "AngelScript.Burst.Validation.Tests.$fixture" \
    -testResults "$results" -logFile "$project/TestResults/validation-$mode.log"
  python3 "$root/csharp/scripts/verify_validation_xml.py" "$results" "$mode"
done
