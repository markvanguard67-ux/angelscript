# Correctness and completeness audit

**Date:** 2026-10-12
**Verdict:** **not complete, not fully AngelScript-conformant, and not yet verified in Unity/Burst.**

The implementation is a usable experimental primitive compiler/VM foundation, not the full C# AngelScript implementation originally requested. Restricting it to primitives was an implementation decision, not a user-approved replacement of the original scope. Passing its regular regression suite is not evidence of complete language or Unity compatibility.

## Verification performed

The audit compared execution and source acceptance with the **original C++ engine in this repository**, inspected compiler/VM/native-owner paths, expanded the numeric/type/evaluation-order corpus, and corrected confirmed defects. The native SDK sources were not modified.

| Check | Observed result |
| --- | --- |
| Original regression baseline, 2,000 seeds | 81 groups passed; 2,060 native result comparisons passed |
| First audit regression suite, 2,000 seeds | 96 groups passed; 2,075 native result comparisons passed |
| **Second audit regression suite, 5,000 seeds** | **110 groups passed, 0 failed; 5,088 native result comparisons passed** |
| First audit conformance matrix | 685 matched, 69 mismatched, out of 754 cases |
| **Second audit expanded conformance matrix** | **8,350 matched, 83 mismatched, out of 8,433 cases**; conformance command correctly exits **1** |
| Second audit side-effect matrix | **All 7,260 global/local operand comparisons matched** after correcting global capture |
| Malformed-source fuzz | **10,000 seeded inputs** through the real compiler, no escaped CLR exceptions |
| Warmed VM preparation/execution allocation test | Zero managed bytes allocated in the tested workload |
| Core/compiler/test compilation | Separate actual assemblies compiled with warnings as errors using cached Roslyn/CoreCLR |
| Unity results-XML validator | **8 portable tests passed**; these validate tooling, not Unity |
| Actual Unity Editor/Burst/job execution | **Not run: Unity Editor unavailable** |
| IL2CPP/AOT player execution and target-platform safety checks | **Not run** |
| Committed .NET 8 SDK projects / GitHub Actions execution | **Not run locally: .NET 8 SDK unavailable**; manual source compilation is not an SDK-project build |

The increased mismatch count in the second audit reflects a **larger matrix**, including additional documented deviations, not failures in the fixed global-capture cases. The matrix is a finite set of probes, not the full upstream regression suite. It compares accepted/rejected compilation, runtime-fault status, and successful raw return bits. It does not compare every warning, exception message, engine option, or floating platform behavior. A native crash/unsupported oracle exit is treated as a tooling failure, not as a language runtime fault.

## Remaining blockers and confirmed differences

### Blocking: full language/runtime/API implementation is missing

Missing features include script classes/interfaces/mixins/inheritance, registered object/value types, constructors/destructors and dispatch, object handles/null/identity, references and in/out parameters, arrays/indexing, strings/characters, templates, enums/typedefs/namespaces, funcdefs/delegates/lambdas, module imports, named/default arguments, garbage collection/refcounting, the native engine/context API, native bytecode serialization and SDK add-ons.

The execution model intentionally gives each lane private globals and uses fixed native scratch capacities. Global initialization is limited to constant expressions. These are useful design choices, but not drop-in replacements for the full C++ engine. There is no implemented native heap/object-lifetime model on which the missing object features could run.

### High: numeric overload resolution is not equivalent

`UnityPackage/Compiler/FunctionBinder.cs` uses summed exact/widen/narrow costs, not AngelScript's candidate ranking. This is **not limited to extreme numeric values**: ordinary literals can select the wrong function, reject a valid call, or accept an ambiguous call.

Valid in C++, rejected as ambiguous here:

```angelscript
int choose(int8 x) { return 1; }
int choose(float x) { return 2; }
int test() { return choose(1); }
```

C++ rejects the following as ambiguous; this implementation chooses the `double` overload and returns 2:

```angelscript
int choose(float x) { return 1; }
int choose(double x) { return 2; }
int test() { return choose(1); }
```

The expanded matrix finds **67 mismatches in 315 two-overload cases**. Explicit casts can avoid these examples but do not fix compatibility. Multi-parameter dominance, complete conversion ranking, and the original engine's options require additional work.

### Medium: constant uint64-to-floating conversion differs

On this checkout's C++ engine, these constant casts return -1. The C# port returns the positive unsigned magnitude, consistent with its runtime conversion:

```angelscript
double test() { return double(uint64(0xffffffffffffffff)); }
float test() { return float(uint64(0xffffffffffffffff)); }
```

The native engine's variable version produces the positive magnitude, so its constant/runtime paths are themselves inconsistent at this boundary. The port does **not** emulate that constant-folding behavior. This is a confirmed conformance difference, not a claim that the mathematically unsigned conversion is incorrect. The conformance test records it as a mismatch rather than hiding it.

### Blocking verification gap: Unity/Burst/IL2CPP compatibility is unproven

The design separates managed compilation from unmanaged execution, uses native lane buffers, constrained unmanaged host calls, generic-job registration and dependency tracking. The Editor tests check real Burst execution with `BurstDiscard` markers, rather than treating managed fallback as success.

Those are architectural/source-level evidence only. Previous temporary Unity API-shape compilation was **not execution against Unity**, and cannot establish package import, Burst code generation, actual job safety/deallocation, generic host registration, or IL2CPP/AOT behavior. The Editor and target-platform player tests must run before declaring Unity compatibility or production readiness.

### Further conformance/safety limits

- Floating math/NaN/overflow and managed constant folding versus Burst can differ across platforms; strict float mode is not a determinism guarantee.
- Non-finite/out-of-range float-to-integer conversions deliberately fault instead of reproducing CPU-specific C++ results. Extreme integer-power behavior is not exhaustively equivalent.
- Zero-initialized locals, constant-only global initializers, private lane globals, return-flow analysis and unsupported engine configuration are documented deviations.
- The raw-pointer VM requires valid, non-overlapping, appropriately sized/lived buffers from trusted hosts. Offset/capacity checks are not a hardened memory sandbox or a validation of physical pointer ownership.
- Full native regression migration, fuzzing/security analysis, interpreter benchmarks, per-lane memory scaling, platform coverage and stable serialized assets/API remain unfinished.

## Confirmed bugs corrected during this audit

| Area | Before | Correction / regression coverage |
| --- | --- | --- |
| Primitive cast statements | `int(3.5);` and casts in a `for` initializer were misread as declarations | Type-followed-by-`(` is parsed as a cast expression; native-comparison cases added |
| Double power | `double ** int64` converted the exponent to double instead of narrowing to int32; large odd exponents could become even | Preserve the native mixed double/int32 power path, including compound assignment |
| Wide switch | A uint64 switch used 64-bit keys; e.g. 4294967296 failed to match `case 0` | Convert the expression and labels to int32; detect duplicate labels after narrowing |
| Signed real cast boundary | `int(-2147483648.5)` faulted before truncating to valid INT_MIN | Truncate before range validation |
| Narrow unsigned real casts | `uint8(-2.5)` incorrectly faulted | Use the native signed int32 intermediate, then narrow to 254; uint16 covered too |
| Decimal literal rounding | CLR parsing differed from the native scanner even for 0.75 | Reproduce native decimal accumulation; raw-bit regression for 0.75 + 0.75 |
| Shift counts | Valid floating counts were rejected | Permit numeric counts and convert to uint32 before masked shifting |
| Signed power boundary | `(-2) ** 31` / `int64(-8) ** 21` returned signed minimum where C++ faults | Preserve native conservative signed-minimum power overflow behavior |
| Source rejection | Empty switches, `1f`, returning a void expression, void casts and unbraced controlled declarations were accepted | Reject them explicitly; diagnostic regressions added |
| Unity verification script | Editor exit status could report success without executed/passing real-Burst probes | Delete stale XML, filter the integration fixture, require all ten passing cases including both Burst probes; missing/empty/skipped/failed results fail |

The existing actual-Burst function-pointer test was also extended to exercise the new truncation and mixed-type power paths. **That updated test has not been executed in Unity.**


## Second correctness pass (2026-10-12)

This pass used fresh global/local side-effect combinations rather than only repeating the original matching corpus. The ordinary baseline still passed before these checks, demonstrating why passing regression counts alone are insufficient.

### Additional corrections

1. **High — global numeric operands were read too late.** `BytecodeEmitter.TryNumericAlias` treated global variables/assignment results as deferred local references. In native AngelScript, globals are captured before later operand side effects:

   ```angelscript
   int g=1;
   int test() { return g + g++; } // Native: 2; before this correction: 3.
   ```

   The distinction also affects `(g=2)+(g=3)`, compound assignments, comparisons and mutating function calls. Deferred reads are now restricted to **locals**; globals and their assignment results are captured. Twelve direct native-comparison regressions were added. The expanded matrix compares all pairs of eleven operand forms, across ten numeric types, three operators and both global/local scopes: **7,260 cases, all matching** after this fix. Same-type casts are covered too.

2. **Medium — constant power overflow was accepted and postponed to runtime.** Examples `2**31`, `0**0`, `0.0f**-1.0f` and `2.0**10000` are rejected by the native compiler. The port previously emitted faulting runtime instructions. Constant folding now reports `InvalidConstant`; nonconstant power overflow remains a VM fault. Declarations/returns and floating/integer cases have regression coverage.

3. **Medium — switch cases accepted illegal unbraced declarations.** Native AngelScript requires a braced statement block for declarations within a case/default body. The binder now rejects bare declarations, while `{ int n=7; return n; }` inside a case remains valid and has a native-comparison regression.

4. **Cleanup-contract inconsistency — synchronous disposal could return before deferred cleanup finished.** Static inspection showed `Dispose()` returning immediately when an earlier `Dispose(JobHandle)` had marked the owner disposed, without completing its queued work. Both owners now complete recorded work before that early return; `NativeScriptBatch.Complete()` can also wait after deferred disposal, consistently with the program owner. Two Editor tests exercise owner completion and caller-owned host-buffer safety. **The contract/source correction is verified by inspection and temporary API-signature compilation only; actual Unity lifecycle behavior remains unverified.** The XML gate now requires **all ten** integration tests, including these additions and both real-Burst probes.

### Expanded matrix findings still open

The 83 mismatches consist of:

| Category | Count | Meaning |
| --- | ---: | --- |
| Numeric overload resolution | 67 | Existing incorrect/incomplete ranking |
| Unsigned constant-to-floating treatment | 4 | Native constant/runtime inconsistency; includes two additional mixed-arithmetic cases |
| Constant signed division boundary | 1 | Native `int64(MIN)/int64(-1)` constant-folds to 0 on this checkout; port preserves its overflow-fault policy |
| Uninitialized const local | 1 | Native accepts with a warning; port requires an initializer |
| Out-of-range cast policy | 1 | Native CPU-specific result versus the port's explicit conversion fault |
| Reference-producing lvalues | 5 | Assignment/compound/ternary lvalues are valid natively but not implemented by this variable-only binder |
| Return-flow analysis | 4 | Port proves paths that this native compiler conservatively rejects; already a documented deviation |

For example, `return (n=2)++;` and `(n=2)=3;` remain unsupported even though their components are primitive. These findings are now included in `--conformance` rather than omitted from compatibility checks. The full language/object model and actual Burst/IL2CPP verification remain blocking gaps.

The portable suite now permanently includes a 10,000-input deterministic malformed-source fuzz group. This checks diagnostic/error handling; it is **not** a proof of memory-sandbox security or native-pointer safety.

## Reproduce

Build the original reference runner and run the regular regression suite:

```sh
./csharp/scripts/build-reference.sh
dotnet run --project csharp/Tests/AngelScript.Tests.csproj -c Release -- \
  --reference csharp/Tests/bin/reference-runner --seeds 5000
```

Run the broader conformance matrix, which currently **must fail** because the differences above remain:

```sh
dotnet run --project csharp/Tests/AngelScript.Tests.csproj -c Release -- \
  --reference csharp/Tests/bin/reference-runner --seeds 0 --conformance
# Current matrix: 8,350 matched; 83 mismatched. Exit status 1.
```

Validate the XML gate independently (no Unity execution):

```sh
python3 csharp/Tests/test_unity_results.py
```

Run real Editor/Burst integration on a licensed installed Unity 2022.3+ Editor with Burst enabled and Python 3 available:

```sh
UNITY_EDITOR=/path/to/Unity ./csharp/scripts/test-unity.sh
```

Then verify IL2CPP/AOT builds, concrete host registration, native buffer safety/lifetimes and target-platform numerical behavior. See the [compatibility contract](UnityPackage/Documentation~/compatibility.md) and [full-port roadmap](UnityPackage/Documentation~/roadmap.md). Until the major feature gaps and verification gates are addressed, the correct label remains **experimental primitive subset**, not a correct and complete AngelScript port.
