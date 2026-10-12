# Parallel simulation sample

Import this sample from Package Manager, add `ParallelSimulation` to a GameObject, and enter Play mode with Burst enabled.

The managed `Start` method compiles one script and allocates persistent native program/context buffers. Each frame, one `IJobParallelFor` lane runs a private VM and private `distance` global. The script's `readVelocity()` host call is dispatched to an unmanaged struct backed by a read-only `NativeArray<float>`.

The assembly-level `RegisterGenericJobType` attribute ensures that Unity sees the concrete host job for job reflection and AOT/Burst compilation. Add an equivalent attribute for each host implementation in your own project.

`PrepareAll` preserves globals but starts a new invocation. To resume a `Suspended` invocation instead, call `Schedule` again **without** preparing it. Host/native data must remain alive until the job handle completes. No UnityEngine API is called inside the VM or the host.

The sample uses small explicit VM capacities rather than the larger general-purpose defaults. Size these for your functions and recursion depth. It completes work for readback in `LateUpdate`; a production system can chain dependencies and defer readback instead.
