# Runtime and integration architecture

## Managed compilation boundary

`ScriptCompiler` lexes a source string, builds syntax, binds variables/functions/types, checks return flow, folds primitive constants and emits immutable bytecode tables plus managed source/name metadata. It is reusable sequentially; it is not thread-safe for concurrent registration/compilation. Use separate compiler instances for concurrent builds. Source/depth/token/instruction/function/local/parameter limits are configurable and snapshotted at construction.

`TryCompile` reports the first diagnostic and returns no module on failure. `Compile` throws a managed `ScriptCompilationException`. These APIs are intentionally **outside Burst**: use them in editor tooling or on a managed thread before scheduling work. They also work in an IL2CPP application as ordinary managed-side APIs; runtime source compilation does not use Reflection.Emit/JIT/dynamic assemblies.

`CompiledModule` holds private arrays and read-only metadata collections. Its `Copy*` methods produce copies for a native owner. Original C++ modules, engine state and bytecode are not involved.

## Unmanaged core

`Value` is a 16-byte explicit-layout primitive/tag union. Integer payloads retain exact width and 64-bit precision; integer operations never route through double. Floating bit reinterpretation uses union fields. `Instruction`, `FunctionInfo`, `CallFrame`, `ExecutionState`, `ProgramView`, and `VmMemory` contain only values or pointers.

The VM reads a `ProgramView` and mutates a private `VmMemory` plus registers. Its reachable execution path has no managed allocation, strings, delegate calls, boxing, reflection, object references, or `throw`. Arithmetic returns `VmError`; capacities and table/slot/jump/stack/type bounds are checked before access. Bounds checks protect correctly described buffers and compiled bytecode—they cannot validate forged raw pointers or guarantee an unsafe caller actually allocated the advertised lengths.

The raw API's owner must:

- Keep every referenced program and scratch buffer alive throughout execution, suspension and pending host calls.
- Use correctly aligned buffers with the advertised capacities and valid immutable tables.
- Give each concurrent instance disjoint writable stack/local/frame/global memory and registers; scratch buffers must not overlap each other.
- Keep `Prepare` arguments valid for the call and separate from the local buffer it initializes.
- Initialize globals once before preparing, or reset them explicitly when desired.
- Avoid changing immutable program tables or const globals while in use.

No C++ code is called at runtime. The standalone oracle links C++ only to test source-level behavior.

## Execution and limits

`Prepare` resets registers, sets the entry frame, checks exact argument types and initializes locals. It preserves globals. `Execute` returns:

| Status | Meaning |
| --- | --- |
| `Ready` | Prepared or a host call has just been completed |
| `Suspended` | The instruction slice is exhausted; resume without preparing |
| `HostCallPending` | Core ABI yields a host function ID/arguments; the caller must dispatch it |
| `Finished` | Entry invocation returned; typed `ReturnValue` is available |
| `Faulted` | Inspect `Error` and `FaultInstruction`; re-prepare to start a new invocation |
| `Unprepared` | Initial/default registers; not executable until preparation |

`Execute` on a finished/faulted invocation is a no-op. A zero budget produces suspension. The Unity adapter requires positive budgets. Counts include the host-call instruction, not arbitrary work performed by a host; instruction budgeting is not a wall-clock deadline. Host implementations must also do bounded work.

Recursive script calls allocate no memory: frames and locals are pushed into fixed buffers. Stack limits are per lane and apply across calls. No growing/throwing stack is introduced when a capacity is exceeded.

## Host ABI

`CallHost` reverses the already evaluated argument span into declaration order, records the ID/count/return type and yields. The span lives at the top of the private operand stack. `CompleteHostCall` consumes it, checks the exact return type and resumes. Its pointers must not be retained, mutated or used after completion/preparation.

The Unity runner specializes `IScriptHost.Invoke` for a concrete **unmanaged** host struct. Constrained generic dispatch avoids managed virtual/interface dispatch. Declare native host inputs read-only when appropriate, partition host outputs by lane, and use explicit dependency handles for any shared writes. Register each concrete `ScriptBatchJob<Host>` with `RegisterGenericJobType` for reflection/AOT discovery. Hosts have no GameObject/UnityEngine/managed-delegate access inside jobs; translate access into primitive IDs/native buffers/commands on the managed side instead.

Math functions are explicit compiler registrations, not an automatic language standard library. Burst recognizes the supported scalar math calls reachable from the interpreter. Floating precision/determinism has the qualifications in the compatibility contract.

## Unity ownership

`NativeScriptProgram` owns read-only native instruction/function/type/default tables. `NativeScriptBatch` owns flattened scratch arrays and one `ExecutionState` per lane. `Execute(index)` derives private pointer ranges `index * capacity .. (index + 1) * capacity` and only writes that lane's state/globals. Parallel-for index restrictions are disabled **only for the sliced scratch containers**, not all safety checks.

Schedulers record handles on both owners. A batch's next schedule depends on its previous use; program reads across different batches do not serialize each other. Managed reads/preparation/mutation complete that batch's prior work. Program disposal completes all recorded readers; batch disposal completes its own recorded jobs. Deferred native deallocation can be chained via `Dispose(JobHandle)`.

These owner classes are main-thread/setup APIs; concurrent calls to them from multiple managed threads are not supported. Host buffers are not owned by them. A user must keep host data alive and correctly dependency-chained until the returned handle completes. `TempJob` retains its four-frame deadline even if a script is suspended; prefer `Persistent` for long-lived instances.

For ECS `IJobEntity` / custom job producers, the public raw VM structs/methods can be embedded in the same native-data partitioning model; the provided adapter is `IJobParallelFor`, not an Entities-specific integration or a GameObject binding system.
