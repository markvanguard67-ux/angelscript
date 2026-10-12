#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
: "${UNITY_EDITOR:?Set UNITY_EDITOR to the Unity Editor executable (2022.3+).}"
if [[ ! -x "$UNITY_EDITOR" ]]; then
  printf 'Unity Editor executable not found: %s\n' "$UNITY_EDITOR" >&2
  exit 2
fi
if ! command -v python3 >/dev/null 2>&1; then
  printf 'Python 3 is required to verify Unity/Burst test results.\n' >&2
  exit 2
fi
project="$root/csharp/UnityTestProject"
results="$project/TestResults/results.xml"
mkdir -p "$project/TestResults"
# An Editor that exits without running tests must not inherit a previous passing result.
rm -f "$results"
# Do not add -quit: the test runner exits after running tests. Burst probes detect managed fallback.
"$UNITY_EDITOR" -batchmode -nographics -projectPath "$project" \
  -runTests -testPlatform EditMode \
  -testFilter AngelScript.Burst.Tests.BurstIntegrationTests \
  -testResults "$results" \
  -logFile "$project/TestResults/editor.log"
python3 "$root/csharp/scripts/verify_unity_results.py" "$results"
