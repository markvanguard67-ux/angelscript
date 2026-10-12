# Coverage ledger and full-conversion workflow

The laboratory separates **executed behavior**, **unimplemented language features**, and **missing host/API instrumentation**. A capability checkbox without assertions is not a test.

| Work area | Existing evidence/driver | Additional acceptance work |
| --- | --- | --- |
| Primitive language | Original-engine fixtures; all numeric types; globals vs locals; operators/control/calls/recursion/diagnostics | Migrate full upstream primitive regressions, all overload rules, engine options, diagnostic/warning parity and platform numerical edges |
| Burst execution | Function pointer + per-lane `BurstDiscard` host marker; managed/native/Burst comparison | Execute actual supported Editors/platforms, release float configurations and target CPUs |
| Jobs/native ownership | Concurrent batches, private scratch/globals, dependencies, host buffers, budgets, failures, deferred/synchronous cleanup | Stress many frames, cancellation/teardown under domain reload, ECS/custom job producers, host dependencies and safety-disabled builds |
| Fixed VM capacities | Stack/local/call exhaustion, explicit preparation errors, corrupt opcode/target descriptors | Fuzz validated serialization; genuine unsafe-pointer/security audit; high instruction counter limits |
| Objects/interfaces/inheritance | Native-verified constructor/field/method/destructor/interface/base-dispatch probes; currently blocked in C# | Arena layout/alignment, object tables, virtual dispatch, construction failure, all lifetime paths and registered host objects |
| Handles/references | Native identity/null, cyclic graph and in/out/inout probes; currently blocked | Generation/stale-handle safety, alias behavior, readonly/ref return rules, cross-lane ownership and host marshalling |
| Arrays/strings/templates/dictionary | Native add-on probes, growth/index/bounds, UTF-8 string behavior; currently blocked | Bounded native allocators, string representation/encoding, template registration, resize aliases, destruction and add-on API parity |
| Enums/typedefs/namespaces/auto | Native positive fixtures; currently blocked | Full lookup/conversion/scoping/diagnostics and reflected API metadata |
| Function handles/arguments | Native funcdef, default/named argument probes; currently blocked | Delegates/lambdas/captures, default evaluation order, AOT target dispatch and callback ownership |
| GC/refcounting/native heap | Graph behavior probe; **GC accounting driver is missing** | Add native heap counters/snapshots, forced collection, cycle detection/reclamation, leak/stale-access tests and native C++ observer expectations |
| Modules/imports/shared state | **Driver missing** | Multi-module recipe/driver, import rebinding, unload while jobs run, shared declarations/type identity and original configuration API |
| Bytecode persistence | **Driver missing** | Versioned validated serialization, original Save/Load contract, malformed assets, endianness/ABI, reload after domain restart |
| Registered value/object types | **Driver missing** | Sizes/alignment, field offsets, by-value/by-reference ABI, native owners/refcounts, constructors/methods/property bindings and concrete AOT registrations |
| Preprocessing/debug API | **Drivers missing** | Include graph, source maps, callbacks/breakpoints, stack traces/local inspection, context suspend/abort, recursive host reentry rules |
| Remaining SDK add-ons | **Driver missing** | Adapt chosen add-ons to native ownership, declare intentional exclusions, compare registration and behavioral matrices |
| Production performance/security | Timings and warmed managed-allocation check for one VM workload | Native heap allocations, memory scales, throughput under realistic scripts/jobs, fuzzing/sandbox boundaries, stability and supported platform CI |

## Extend a language feature

1. Add a native-valid recipe in `Tools~/generate_corpus.py`, initially `phase='Future'`. Keep the entry result primitive so the existing oracle can record exact bits. Select the native engine/add-on profile explicitly.
2. Regenerate the corpus with the original engine. Invalid native syntax/tooling failures must be fixed, not frozen as expected feature rejection.
3. Implement the C# feature and its unmanaged representation outside the validation package. A compiler rejection remains **BLOCKED** until the implementation is real.
4. The same source then automatically reaches managed VM and every Burst job lane. Require original outcome/type/bits plus actual Burst markers; investigate any mismatch.
5. Add job/lifetime/capacity/fault-path scenarios for new resources. Passing an object-field read does not prove destruction/GC/alias safety.
6. Move a probe to `Current` only when it is an accepted supported contract with all mandatory tests; keep full native conformance probes in the ledger.

## Extend an engine/API capability

`scenario='Capability'` is deliberately blocked by `ValidationSuite.ExecuteOracleCase`. Add an explicit driver dispatch and its required native observers before changing it to a pass:

- imports need multiple module source sections and bind/unload operations;
- serialization needs real save/reload/validation operations, not a source recompilation;
- registered values/objects need layout and host-owner probes;
- GC needs actual current/live/destroyed allocation and cycle counters before/after collection;
- debugger/add-on/configuration probes need original API-level expectations.

Prefer separate scenario methods/classes over adding pretend-success callbacks. Include required driver IDs in the report plan and result-gate tests. Record a **FAIL** when an executed behavior differs; use **BLOCKED** only when it cannot yet be executed because a feature/driver is absent. Never turn gaps into successful negative tests.

## Inspect an assumption

Every result includes ID, source, native expectation, observed path and diagnostic detail. Find its recipe/scenario, add the smallest edge-case reproducer, regenerate C++ expectations, then run the scene in both Editor and IL2CPP. Preserve the report's corpus/environment hashes with the issue. Do not compare reports from different source/configuration fingerprints as if they were the same test.

A full-port green ledger means only that its **selected declared gates** passed. It is not a proof that every upstream script, engine API, memory path, platform, or security property is implemented. Expand coverage alongside implementation rather than using a finite checklist to claim exhaustive correctness.
