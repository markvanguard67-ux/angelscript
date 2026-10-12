# AngelScript Burst — experimental C# subset

Unofficial, rewritten C# primitive AngelScript compiler and unmanaged bytecode interpreter. **Not a complete AngelScript port**, not the upstream AngelCode library, and not compatible with the C++ bytecode/API. Original C++ sources remain in the repository's `sdk/` directory.

Target: Unity 2022.3+, Burst 1.8.18+, Collections 2.1.4+. Compilation is managed-side; execution runs in Burst-compiled parallel jobs with private native state, fixed memory capacities, resumable instruction budgets, explicit faults, and no managed allocations in the VM.

In the repository checkout, full setup, examples, test commands and audit findings are in `csharp/README.md` and `csharp/AUDIT.md`. When this package is distributed alone, consult [compatibility](Documentation~/compatibility.md), [architecture](Documentation~/architecture.md), and the imported **Parallel simulation** sample.

Implemented: primitive types, numeric functions/casts/operators, overloads, const/scoped variables, constant global initializers, if/loops/switch, calls/recursion, host IDs and optional scalar math. Classes, arrays, strings, object handles/references, GC and the broader AngelScript object model are not implemented. Unsupported syntax produces compiler diagnostics.

Package Editor tests include real-Burst probes that detect managed fallback. **They have not been run in the development sandbox, which has no Unity Editor**; run them and an IL2CPP player build on your target platform before shipping. The standalone core/compiler regression suite passes, but the expanded conformance audit still has 83 mismatches in 8,433 cases (mostly ordinary overload selection; all 7,260 local/global side-effect cases match). This is not a full language-compatibility certificate.

Use `RegisterGenericJobType(typeof(ScriptBatchJob<YourHost>))` at assembly scope for every concrete unmanaged host type. Keep caller-owned native host data alive until the scheduled handle completes. Import the sample for a complete example of buffer ownership and job scheduling.
