#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
make -C "$root/sdk/angelscript/projects/gnuc" -j"${JOBS:-2}"
mkdir -p "$root/csharp/Tests/bin"
"${CXX:-c++}" -std=c++11 -Wall -Wextra \
  -I"$root/sdk/angelscript/include" \
  "$root/csharp/Tests/Reference/runner.cpp" "$root/sdk/angelscript/lib/libangelscript.a" \
  -pthread -o "$root/csharp/Tests/bin/reference-runner"
printf 'Reference runner: %s\n' "$root/csharp/Tests/bin/reference-runner"
