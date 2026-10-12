#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
make -C "$root/sdk/angelscript/projects/gnuc" -j"${JOBS:-2}"
mkdir -p "$root/csharp/UnityValidation/bin"
"${CXX:-c++}" -std=c++11 -Wall -Wextra \
  -I"$root/sdk/angelscript/include" \
  -I"$root/sdk/add_on/scriptarray" -I"$root/sdk/add_on/scriptstdstring" -I"$root/sdk/add_on/scriptdictionary" \
  "$root/csharp/UnityValidation/Tools~/reference_validation.cpp" \
  "$root/sdk/add_on/scriptarray/scriptarray.cpp" "$root/sdk/add_on/scriptstdstring/scriptstdstring.cpp" \
  "$root/sdk/add_on/scriptdictionary/scriptdictionary.cpp" \
  "$root/sdk/angelscript/lib/libangelscript.a" -pthread -o "$root/csharp/UnityValidation/bin/validation-reference"
