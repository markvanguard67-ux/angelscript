# Unity validation laboratory

A **separate development-only Unity package** for investigating the C# port. It creates a playable validation scene, compares the actual compiler/VM/Burst jobs against checked-in **original C++ engine results**, and keeps a visible full-port gap ledger. It is not a C++ plugin or a fake Burst implementation.

## Open the scene

1. Open **`csharp/UnityTestProject`** in a licensed Unity **2022.3+** Editor. Allow package import and use a patched Editor release.
2. Enable **Jobs → Burst → Enable Compilation** and collection safety checks. The project references both local AngelScript packages and Unity Test Framework.
3. Open **`Assets/Scenes/AngelScriptValidation.unity`** and press **Play**.
4. The scene runs automatically. Inspect **PASS / FAIL / BLOCKED**, select a row for source/diagnostics/return bits, or rerun/cancel/export JSON. Lane bars show native job outputs.

To create/recreate the scene in another project, install the main package **and** this validation package from disk, then choose:

**AngelScript → Validation → Create and open test scene**

The creator asks before replacing an unsaved scene. The checked-in scene is also available immediately in the test project; the menu can regenerate it with a camera and directional light.

### What runs

- **246 native-oracle fixtures**: 214 current-subset cases, 5 conformance regressions, 27 future-port probes/driver requirements.
- **16 integration scenarios**: actual Burst function pointer, registered generic/AOT host jobs, typed/ordered host ABI, host failures, budget resumption, infinite-script bounds, lane fault isolation, private/persistent/reset globals, concurrent code readers, dependency chains, synchronous/deferred disposal, fixed capacities, invalid bytecode, preparation guards, allocation/layout checks, diagnostics, and repeatability.
- Every successfully compiled oracle fixture runs through the **real managed core and parallel jobs**. An entry `BurstDiscard` marker must prove real Burst execution on **every lane**. Native C++ return bits, managed bits and Burst bits must match; floating drift is reported, not tolerated silently.
- The corpus covers all ten numeric types in both local/global storage scopes, side-effect-producing operands, 32 seeded arithmetic recipes, primitive control flow and numerical regressions. Sources rejected by C++ must also be rejected here.

Default plan: **262 result rows**, 32 lanes. This is finite evidence, not every upstream engine test.

## Honest gates

**Supported subset** requires the full planned run, all current-required rows, and the real-Burst kernel probe to pass. Missing/duplicate/zero-test execution, cancellation and managed fallback cannot report success.

**Full port** additionally requires every conformance/future row to pass. Current unsupported classes, references, arrays, strings, handles, GC/API drivers etc. are **BLOCKED**, not passing negative tests. Known overload/conversion/source-acceptance differences are **FAIL**. Full-port readiness is expected to be false today.

The source-based future probes are valid scripts verified by the original engine. Capability rows identify missing **host-side** observers/drivers (GC accounting, native registered layouts, multi-module imports, serialization, debugger/build pipeline/add-ons). They are not implemented or claimed as tested just because a row exists. See [coverage and extension work](Documentation~/coverage.md).

## Reports an agent can inspect

Each run exports UTF-8 JSON to:

`Application.persistentDataPath/AngelScriptValidation/latest.json`

The scene prints the absolute path. It includes:

- Unity/platform/backend/build identity, job-worker count, safety flags and Burst-enabled state;
- native source and corpus recipe/asset SHA-256 fingerprints;
- complete planned case IDs, per-case status/timing/source/expectation/observation/detail;
- lane counts and proven Burst execution, subset/full-port gates, visible gaps and cancellation state.

Copy/attach this JSON and the Unity log when asking for the next correctness audit. This environment cannot see your local Editor results unless you provide them. Do not paste a success headline without the report/environment details.

An independent gate recomputes required coverage rather than trusting headline booleans:

```sh
python3 csharp/scripts/verify_validation_report.py path/to/latest.json
python3 csharp/scripts/verify_validation_report.py path/to/latest.json --full-port
python3 csharp/scripts/verify_validation_report.py path/to/player.json --player
```

`--full-port` currently must fail on the unresolved ledger. `--player` requires an **executed IL2CPP player**, not Editor/Mono evidence.

## Automated Editor and scene tests

```sh
# Existing core integration probes:
UNITY_EDITOR=/path/to/Unity ./csharp/scripts/test-unity.sh

# New Editor scene/corpus/report tests + PlayMode scene and cancellation tests:
UNITY_EDITOR=/path/to/Unity ./csharp/scripts/test-unity-validation-scene.sh

# Run the same laboratory suite headlessly and independently verify its JSON:
UNITY_EDITOR=/path/to/Unity ./csharp/scripts/run-unity-validation.sh
# Strict full-port gate (expected nonzero until conversion gaps are fixed):
UNITY_EDITOR=/path/to/Unity ./csharp/scripts/run-unity-validation.sh --full-port
```

Python 3 is required for the result gates. Scripts delete stale reports and reject missing/skipped/failed tests. Unity must be installed/licensed with the required packages. None of these commands simulate Unity.

## IL2CPP/AOT player checks

Select a standalone target with its **IL2CPP build module** installed, then choose:

**AngelScript → Validation → Build IL2CPP validation player**

The builder includes only the validation scene, uses a development build, and restores your previous scripting backend afterward. It does not replace the project's build-scene list. **Building is not execution evidence.** Run the produced player:

```sh
/path/to/AngelScriptValidation --angelscript-validation --angelscript-report /absolute/path/player.json
python3 csharp/scripts/verify_validation_report.py /absolute/path/player.json --player
```

On Windows use the `.exe` with the same arguments. Add `--angelscript-full-port` for a strict ledger gate. `--angelscript-release-checks` explicitly permits a safety-disabled release run and records that environment; do not use it to replace a safety-enabled development run.

The player exits 0 only when the selected gate passes; otherwise 1. IL2CPP generic host registration lives in the runtime validation assembly, not only in Editor test code. WebGL/mobile console/AOT runners and release-build configurations need target-specific additions.

## Native corpus provenance and regeneration

The small `Resources/AngelScriptValidation/Corpus.json` asset is necessary so a standalone player can compare against C++ **without starting a native process**. It is generated from the repository engine and source recipes, never from C# output:

```sh
./csharp/scripts/build-validation-reference.sh
python3 csharp/UnityValidation/Tools~/generate_corpus.py
python3 csharp/UnityValidation/Tools~/generate_corpus.py --check
```

The test-only oracle registers generic C++ scalar math and the original array/stdstring/dictionary add-ons where a recipe needs them. The primitive `inout` reference recipe explicitly selects `asEP_ALLOW_UNSAFE_REFERENCES`; this native configuration is recorded in the recipe. These registrations are not new C# features.

Regeneration fails on a native crash/unknown exit, timeout, invalid future syntax or missing return bits; these are not mislabeled runtime faults. Sources/add-on hashes and recipe hashes detect stale expectations. Review changed goldens when native code or recipes change. Generated native binaries remain ignored in `bin/`; only the compact portable expectations are checked in.

## Verification boundary

**Unity Editor is unavailable in the development sandbox. The scene import, actual Burst/jobs, PlayMode suite and IL2CPP player have NOT been executed here.** The corpus generator has been run against the real C++ engine; current-subset sources have also been compared with the actual C# core/compiler. Temporary Unity API-signature compilation and portable report-tool tests are syntax/tooling evidence only—not Unity execution.

Keep this package and its test assets out of production game dependencies unless intentionally shipping a diagnostic build. The full C# conversion still requires implementation and systematic conformance/heap/API/platform work; the laboratory makes that work inspectable, not complete.
