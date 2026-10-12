# Roadmap to a broader C# port

The current package is a tested primitive compiler/VM foundation, not a finished engine conversion. Full AngelScript parity requires additional implementation and systematic conformance work.

1. **Verify native integration:** run the actual Burst probes/job tests in supported Editor versions; add IL2CPP/AOT player builds on target platforms; test safety checks and native host registration; benchmark interpreter throughput and per-lane memory.
2. **Extend primitive conformance:** migrate more native regression scripts, numeric overload selection/costs, constant diagnostics, initialization rules, lvalue side effects, numeric edge cases and source/debug metadata. Specify which engine options are supported rather than assuming all C++ settings.
3. **Build-time assets:** editor import/baking, versioned validated bytecode serialization and IL2CPP builds that omit managed source compilation if desired. Current modules are in-memory only.
4. **Native arrays/value types:** type/layout registration with explicit sizes/alignment, bounded native buffer handles, bounds/ownership rules, structs/vector types and by-reference parameters. None should be backed by CLR objects in the execution path.
5. **Object model:** script classes, interfaces, methods, inheritance and dispatch on type IDs/arena offsets; native object tables, handles/generations, constructors/destructors and lifetime rules.
6. **Memory management:** define native heap/arena allocation limits and deterministic ownership/refcount/GC behavior compatible with jobs. A direct translation of C++ pointer/GC behavior or replacement by managed objects is not Burst-compatible. Clarify cross-lane sharing before allowing it.
7. **Remaining language/API:** strings, enums, namespaces, templates, function handles, modules/imports, original API compatibility where meaningful, and add-ons translated against the native runtime model.
8. **Production acceptance:** full chosen-language conformance matrix, security/unsafe-memory audit, API stability, real target-platform Burst tests and performance evidence.

Do not label the primitive subset as a full AngelScript port or use the standalone tests as proof of Unity/Burst/IL2CPP execution. Keep the original native library independent, retain its attribution/license, and respect the upstream project's contribution policy.
