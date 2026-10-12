# AngelScript in C# for Unity Burst

An **experimental, unofficial C# implementation of the primitive AngelScript language subset**, alongside the original C++ SDK. This is a working compiler/interpreter foundation, **not a complete port of AngelScript 2.39**, and not the upstream AngelCode library. The original engine in `sdk/` is unchanged.

## Architecture

```text
AngelScript source
    │ managed C# lexer, parser, type checker, bytecode compiler
    ▼
Immutable CompiledModule                 Compile in the Editor or on the main thread
    │ upload once
    ▼
Read-only NativeScriptProgram            Share between batches
    │
    ├── private stack / locals / globals / call frames / registers for lane 0
    ├── private stack / locals / globals / call frames / registers for lane 1
    └── ...
          │ unmanaged VM inside a Burst-compiled IJobParallelFor
          ▼
    Finished / Suspended / Faulted + typed return value
```

Burst compiles the **interpreter and native host dispatch**, not arbitrary scripts into new machine-code functions. The managed compiler never runs inside a Burst job. Execution uses no managed objects, allocations, reflection, delegates, exceptions, native C++ plugin, or dynamically generated C# assemblies. Calls/recursion use an explicit preallocated call stack, not CLR recursion.

### What works now

- All primitive types: `bool`, signed/unsigned 8/16/32/64-bit integers, `float`, `double`, `void` returns; `int32` / `uint32` aliases.
- Global functions, forward calls, recursion, numeric overloads, by-value parameters, typed returns.
- Scoped locals, `const`, per-instance primitive globals with constant initializers.
- Arithmetic, power, bitwise operations, comparisons, explicit numeric casts, assignments, compound assignments, prefix/postfix increment/decrement.
- Lazy `&&` / `and`, `||` / `or`, `!` / `not`, logical `xor` / `^^`, and ternary expressions.
- `if` / `else`, `for`, `while`, `do` / `while`, `switch` / fallthrough / `default`, `break`, `continue`.
- Comments, scientific notation, radix literals, digit separators, positioned compiler diagnostics.
- Optional scalar math intrinsics and native host function registration by numeric ID.
- Bounded, resumable execution; explicit arithmetic/stack/type faults; independent parallel VM lanes.

**Not implemented:** classes, interfaces, object handles, reference parameters, arrays, strings, GC, templates, namespaces, enums, function handles/delegates, imports, add-ons, named/default arguments, or the C++ engine's public API/bytecode format. Unsupported features fail compilation. Numeric overload ranking and edge-case floating-point behavior are not a promise of full C++ equivalence; see the [compatibility contract](UnityPackage/Documentation~/compatibility.md).

## Unity installation

Target: **Unity 2022.3 LTS or newer**, with **Burst 1.8.18+** and **Collections 2.1.4+**. Use a patched Editor release. The package supplies minimum dependency versions and separate assembly definitions for the unmanaged core, managed compiler, and Unity adapter.

1. In Package Manager choose **Add package from disk**.
2. Select [`UnityPackage/package.json`](UnityPackage/package.json) from this checkout.
3. Enable Burst compilation. Import **Parallel simulation** from the package's Samples section for a complete MonoBehaviour + native-host example.

No changes to the C++ SDK and no native plugin are needed. The package can also be installed by a Git URL with `?path=csharp/UnityPackage` after these changes have been pushed to a reachable commit/branch.

### Minimal parallel execution

```csharp
using AngelScript.Burst;
using AngelScript.Burst.Compilation;
using AngelScript.Burst.Unity;

var module = new ScriptCompiler().Compile(@"
    float distance = 0;
    float tick(float velocity, float dt) {
        distance += velocity * dt;
        return distance;
    }");

int tick = module.FindFunction("tick", ScriptType.Float, ScriptType.Float);
using (var program = new NativeScriptProgram(module))
using (var batch = new NativeScriptBatch(program, 1024,
    new VmLimits { StackCapacity = 32, LocalCapacity = 32, CallCapacity = 8 }))
{
    // Prepare is managed-side setup. Globals are preserved between invocations.
    batch.PrepareAll(tick, Value.FromFloat(2), Value.FromFloat(0.5f));
    batch.Schedule(instructionBudget: 1000).Complete();

    ExecutionState state = batch.GetState(0);
    if (state.Status == ExecutionStatus.Finished) {
        float distance = state.ReturnValue.AsFloat; // 1.0f
    }
}
```

Always inspect `ExecutionStatus`. If it is `Suspended`, call `Schedule` again **without** `Prepare` to resume the same invocation. Preparation starts over, discarding any pending call/return, while preserving globals. A fault's `FaultInstruction` can be mapped with `module.GetInstructionLocation(...)` when the index is nonnegative.

Each lane's stack/local/call capacities are fixed for its lifetime. Choose them for your script's expression depth and sum of live locals across nested calls; exhaustion returns a fault instead of allocating. The general-purpose defaults are deliberately generous—use smaller explicit limits for large entity counts.

### Native host bindings

Register a signature and ID on the managed compiler, then supply an **unmanaged** struct implementing `IScriptHost` to `batch.Schedule(host, ...)`. Its fields may include `NativeArray`s. A host can access ECS/native data using `laneIndex`, but cannot touch a GameObject, managed service, string, or UnityEngine API from the job.

```csharp
// At assembly scope, outside any namespace/class:
[assembly: Unity.Jobs.RegisterGenericJobType(
    typeof(ScriptBatchJob<MyHost>))]

public unsafe struct MyHost : IScriptHost {
    public bool Invoke(int id, int lane, Value* args, int count, out Value value) {
        value = default(Value);
        if (id != 7 || count != 1) return false;
        value = Value.FromFloat(args[0].AsFloat * 2.0f);
        return true;
    }
}

// Main thread:
var compiler = new ScriptCompiler();
compiler.RegisterHostFunction("float scale(float value)", 7);
// Compile a script that calls scale(...), upload it, prepare its lanes, then:
// batch.Schedule(new MyHost(), instructionBudget: 1000).Complete();
```

Add one `RegisterGenericJobType` attribute for **each concrete host type** used in your player. It makes the closed generic job visible to Unity's job reflection/AOT/Burst compilation passes. See the [complete sample](UnityPackage/Samples~/ParallelSimulation/ParallelSimulation.cs).

Host arguments are in declaration order, even though the compiler evaluates them last-to-first like AngelScript. Return an exactly typed `Value`, or `default(Value)` for `void`; return `false` to fault the invocation. Do not retain or modify the argument pointer. Without a host, encountering a host call produces `HostFunctionFailed` rather than invoking a managed callback.

The native owners record job handles. Schedules on one batch are chained; different batches may read the same program concurrently. Preparation, global changes, result access, and synchronous disposal complete the batch's recorded work first. Deferred `Dispose(JobHandle)` is also available. Calling the owner's `Complete()` or synchronous `Dispose()` afterward waits for that queued cleanup; it does not make a disposed owner reusable. **Host buffers are caller-owned** and must outlive the returned handle. Use `Persistent` for long-lived contexts; `TempJob` still has Unity's four-frame lifetime restriction.

## Tests

### Standalone compiler / VM

.NET 8 SDK; no NuGet test framework or Unity installation is required:

```sh
dotnet run --project csharp/Tests/AngelScript.Tests.csproj -c Release
```

For comparison with the original C++ engine (Linux/macOS with a compatible GNU make toolchain):

```sh
./csharp/scripts/build-reference.sh
dotnet run --project csharp/Tests/AngelScript.Tests.csproj -c Release -- \
  --reference csharp/Tests/bin/reference-runner --seeds 500
```

The runner covers compiler diagnostics/limits, primitive arithmetic/precision, AngelScript evaluation order, loops, calls/recursion, overloads, native host suspension, budgets, private persistent globals, faults, malformed bytecode, unmanaged layouts, and **zero managed allocations in Prepare/Execute after warmup**. A seeded arithmetic corpus also compares result bits with the repository's C++ engine. A new GitHub Actions workflow runs this suite; it does not change the existing native workflow.

### Expanded conformance audit

The regular corpus is intentionally limited to matching/supported cases. It is **not** a full conformance certificate. A separate matrix exercises all primitive type pairs and numeric overload choices, including known differences:

```sh
dotnet run --project csharp/Tests/AngelScript.Tests.csproj -c Release -- \
  --reference csharp/Tests/bin/reference-runner --seeds 0 --conformance
```

Current result after the second check: **8,350 matched, 83 mismatched out of 8,433 cases; exit status 1**. All 7,260 global/local side-effect cases now match. Ordinary numeric overload resolution accounts for 67 remaining mismatches; the larger corpus also exposes documented constant-conversion, lvalue, initialization and return-flow differences. See [the correctness/completeness audit](AUDIT.md) for reproductions, fixes and remaining blockers. This mode must not be excluded when assessing AngelScript equivalence.

### Actual Unity / Burst / jobs

Open [`UnityTestProject`](UnityTestProject) in your installed 2022.3+ Editor; allow Unity to update its minimum-version project stamp. Enable Burst, open Test Runner, and run EditMode tests. Or use:

```sh
UNITY_EDITOR=/path/to/Unity ./csharp/scripts/test-unity.sh
```

The package tests include a synchronous Burst function-pointer probe and generic parallel-host jobs with a `BurstDiscard` marker that **fails if execution fell back to managed C#**. They also cover job budgets, private globals, lane fault isolation, concurrent batches, const protection and native buffer disposal. The two real-Burst probes are explicitly skipped when Burst is disabled, not reported as successful Burst execution. The script requires Python 3 and verifies fresh XML results: all ten integration tests, including both probes, must pass. Empty/missing results, skipped probes or failures cause a nonzero exit. Its XML gate has separate portable tests (`python3 csharp/Tests/test_unity_results.py`); those are not Unity tests.

### Runnable validation laboratory scene

A separate development-only package and checked-in scene now provide a visible validation laboratory:

1. Open `csharp/UnityTestProject` in Unity 2022.3+ with Burst enabled.
2. Open **`Assets/Scenes/AngelScriptValidation.unity`** and press **Play**.
3. Inspect results and export/attach the JSON report for the next audit.

The same scene can be generated with **AngelScript → Validation → Create and open test scene**. It runs **246 original-C++ oracle fixtures + 16 integration scenarios**, including real Burst markers, job/lifetime/host/fault/budget tests, primitive storage/type matrices and explicit future-port blockers. It distinguishes supported-subset success from full-port readiness; missing features never count as passed rejection tests.

See [the laboratory guide](UnityValidation/README.md) for Editor/PlayMode automation, native corpus regeneration, IL2CPP player builds and independent report gates. **The scene has not been imported/executed in Unity here; portable/native checks and API-signature compilation are not Unity evidence.**

### Validation status

After the second audit fixes, the compiler/core were built as separate C# assemblies and the standalone regression suite was run here: **110 groups passed, 0 failed, 5,088 C++ differential cases passed** (5,000 seeds plus the compatible language/culture corpus), including a 10,000-input malformed-source fuzz group. Prepare/Execute reported zero managed allocations after warmup in the tested workload. The broader 8,433-case matrix still reports **83 conformance mismatches**; the added global/local capture matrix passes all 7,260 cases. Local validation used a bootstrapped Roslyn/CoreCLR toolchain; the committed .NET 8 projects and GitHub Actions workflow have not been executed locally with a .NET 8 SDK. See [AUDIT.md](AUDIT.md) for the evidence and limits.

**Unity Editor is not installed in this environment. The real Unity/Burst tests are supplied but have not been run here. Burst and IL2CPP/AOT player validation must be completed in your target Editor/platform before production use.** Cross-platform floating-point bit determinism and complete AngelScript conformance are not claimed.

## Layout and next work

- `UnityPackage/Runtime/Core`: unmanaged values, bytecode, arithmetic, interpreter.
- `UnityPackage/Compiler`: managed syntax/type checking/emission; immutable modules.
- `UnityPackage/Runtime/Unity`: native owners and Burst jobs.
- `UnityPackage/Tests/Editor`, `UnityTestProject`: real Unity integration tests.
- `Tests`: executable portable suite and original-engine oracle.
- `AngelScript.Core`, `AngelScript.Compiler`: .NET Standard 2.1 projects over the same source, not a second implementation.

The [roadmap](UnityPackage/Documentation~/roadmap.md) describes the work needed for a full port. This work is isolated from the native library; it is not being submitted as an upstream contribution. See the package [license and origin notice](UnityPackage/LICENSE.md).
