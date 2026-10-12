using System;
using System.Collections.Generic;
using AngelScript.Burst.Compilation;
using AngelScript.Burst.Unity;
using Unity.Burst;
using Unity.Collections;

namespace AngelScript.Burst.Validation
{
    public static partial class ValidationSuite
    {
        private static IEnumerable<Scenario> Scenarios(ValidationOptions options, Action<float[]> visuals)
        {
            yield return new Scenario("integration-environment", "Burst enabled; requested safety instrumentation present", r => {
                Require(BurstCompiler.IsEnabled, "Enable Burst. A managed-only run cannot validate the Burst target.");
#if !ENABLE_UNITY_COLLECTIONS_CHECKS
                Require(!options.RequireSafetyChecks, "Collection safety checks are disabled. Use Editor/development build, or explicitly record a release-mode run.");
#endif
                r.observed = "Environment flags recorded in report (Editor/player/backend/safety/worker count).";
            });
            yield return new Scenario("integration-burst-kernel", "Actual Burst function pointer: VM, truncation, mixed power, math", BurstKernel);
            yield return new Scenario("integration-host-abi", "Generic AOT job: entry marker, last-first arguments, exact host ABI", r => HostAbi(r, options));
            yield return new Scenario("integration-host-faults", "Host false/type-mismatch/no-host faults are explicit", r => HostFaults(r, options));
            yield return new Scenario("integration-budget", "Bounded instruction slices resume without Prepare", r => Budgets(r, options));
            yield return new Scenario("integration-isolation", "Parallel lane faults and native outputs remain isolated", r => Isolation(r, options));
            yield return new Scenario("integration-globals", "Private persistent globals, reset, const/type protection", r => Globals(r, options));
            yield return new Scenario("integration-shared-program", "Concurrent batches share readonly code, not writable state", r => SharedProgram(r, options));
            yield return new Scenario("integration-dependencies", "Same-batch schedules chain their dependencies", r => Dependencies(r, options));
            yield return new Scenario("integration-disposal", "Reader tracking, synchronous/deferred cleanup and disposed-owner rejection", r => Disposal(r, options));
            yield return new Scenario("integration-capacities", "Operand/local/call capacities fault instead of growing", r => Capacities(r));
            yield return new Scenario("integration-invalid-bytecode", "Corrupt opcode/jump/type metadata is rejected safely", InvalidBytecode);
            yield return new Scenario("integration-preparation", "Signature and null/capacity preparation checks", Preparation);
            yield return new Scenario("integration-layout-allocations", "Unmanaged layout and zero hot-path managed allocation", LayoutAllocations);
            yield return new Scenario("integration-diagnostics", "Resource limits, source positions, failed registration rollback", Diagnostics);
            yield return new Scenario("integration-repeatability", "Repeated job runs and lane visualization match deterministic integers", r => Repeatability(r, options, visuals));
        }

        private static unsafe void BurstKernel(ValidationResult result)
        {
            var compiler = new ScriptCompiler(); compiler.RegisterMathFunctions();
            CompiledModule module = compiler.Compile(@"int global=1;double test(){
                if(global+global++!=2)return 0.0;
                double x=9.0, lower=-2147483648.5, base=-1.0;int64 exponent=9007199254740993;float negative=-2.5f;
                if(int(lower)!=-2147483648 || uint8(negative)!=254 || base**exponent!=-1.0)return 0.0;
                return sqrt(x)+sin(0.0)+pow(2.0,3.0);
            }");
            using (var fixture = new ValidationNativeFixture(module))
            {
                fixture.Prepare(module.FindFunction("test"));
                FunctionPointer<ValidationKernels.ExecuteKernel> kernel = BurstCompiler.CompileFunctionPointer<ValidationKernels.ExecuteKernel>(ValidationKernels.Execute);
                ProgramView program = fixture.Program; VmMemory memory = fixture.Memory; ExecutionState state = fixture.State; int marker = 0;
                kernel.Invoke(&program, &memory, &state, 10000, &marker);
                Require(marker == 1, "Function pointer executed as managed fallback, not Burst.");
                Require(state.Status == ExecutionStatus.Finished && state.ReturnValue.Type == ScriptType.Double && state.ReturnValue.AsDouble == 11.0, "Burst kernel failed numeric/VM regressions.");
                result.burstExecuted = true; result.observed = Describe(state) + "; BurstDiscard marker=1";
            }
        }

        private static ScriptCompiler ProbeCompiler()
        {
            var compiler = new ScriptCompiler(); compiler.RegisterHostFunction("void __as_validation_probe()", 0); return compiler;
        }
        private static void AssertMarkers(NativeArray<int> markers)
        {
            for (int lane = 0; lane < markers.Length; lane++) Require(markers[lane] == 1, "Missing real-Burst host marker on lane " + lane);
        }
        private static void HostAbi(ValidationResult result, ValidationOptions options)
        {
            var compiler = ProbeCompiler(); compiler.RegisterHostFunction("int combine(int first,int second)", 17);
            CompiledModule module = compiler.Compile("int sequence=0;int test(){__as_validation_probe();return combine(sequence++,sequence++);}");
            using (var markers = new NativeArray<int>(options.Lanes, Allocator.Persistent))
            using (var calls = new NativeArray<int>(options.Lanes, Allocator.Persistent))
            using (var values = new NativeArray<Value>(options.Lanes, Allocator.Persistent))
            using (var program = new NativeScriptProgram(module))
            using (var batch = new NativeScriptBatch(program, options.Lanes))
            {
                batch.PrepareAll(module.FindFunction("test"));
                batch.Schedule(new ValidationHost { Markers = markers, Calls = calls, HostValues = values }, 10000, 1).Complete();
                AssertMarkers(markers);
                for (int lane = 0; lane < options.Lanes; lane++) Require(batch.GetState(lane).Status == ExecutionStatus.Finished &&
                    batch.GetState(lane).ReturnValue.AsInt32 == 100 && values[lane].AsInt32 == 100 && calls[lane] == 2, "Host declaration order/void/probe mismatch.");
            }
            result.burstExecuted = true; result.lanes = options.Lanes; result.observed = "Each lane returned 100; two native host calls; marker=1.";
        }
        private static void HostFaults(ValidationResult result, ValidationOptions options)
        {
            using (var markers = new NativeArray<int>(options.Lanes, Allocator.Persistent))
            using (var calls = new NativeArray<int>(options.Lanes, Allocator.Persistent))
            using (var values = new NativeArray<Value>(options.Lanes, Allocator.Persistent))
            {
                foreach (int id in new[] { 19, 20 })
                {
                    var compiler = ProbeCompiler(); compiler.RegisterHostFunction("int bad()", id);
                    CompiledModule module = compiler.Compile("int test(){__as_validation_probe();return bad();}");
                    using (var program = new NativeScriptProgram(module)) using (var batch = new NativeScriptBatch(program, options.Lanes))
                    {
                        batch.PrepareAll(0); batch.Schedule(new ValidationHost { Markers = markers, Calls = calls, HostValues = values }, 10000, 1).Complete(); AssertMarkers(markers);
                        VmError expected = id == 19 ? VmError.HostReturnTypeMismatch : VmError.HostFunctionFailed;
                        for (int lane = 0; lane < options.Lanes; lane++) Require(batch.GetState(lane).Status == ExecutionStatus.Faulted && batch.GetState(lane).Error == expected, "Host failure must not escape or continue.");
                    }
                }
            }
            var missing = new ScriptCompiler(); missing.RegisterHostFunction("int missing()", 23);
            using (var program = new NativeScriptProgram(missing.Compile("int test(){return missing();}"))) using (var batch = new NativeScriptBatch(program, 2))
            { batch.PrepareAll(0); batch.Schedule().Complete(); Require(batch.GetState(0).Error == VmError.HostFunctionFailed, "Missing-host execution must fault."); }
            result.burstExecuted = true; result.observed = "false, wrong return type and missing host all produced expected VmError.";
        }
        private static void Budgets(ValidationResult result, ValidationOptions options)
        {
            CompiledModule module = new ScriptCompiler().Compile("int test(){int sum=0;for(int i=0;i<50;i++)sum+=i;return sum;}");
            using (var program = new NativeScriptProgram(module)) using (var batch = new NativeScriptBatch(program, options.Lanes))
            {
                batch.PrepareAll(0); long previous = 0; int slices = 0;
                do
                {
                    batch.Schedule(7, 1).Complete(); ExecutionState state = batch.GetState(0);
                    Require(state.InstructionsExecuted - previous <= 7 && state.InstructionsExecuted > previous, "Instruction slice not honored.");
                    previous = state.InstructionsExecuted; slices++;
                    if (state.Status == ExecutionStatus.Finished) break;
                    Require(state.Status == ExecutionStatus.Suspended && slices < 2000, "Script failed to resume/terminate.");
                } while (true);
                Require(slices > 1 && batch.GetState(0).ReturnValue.AsInt32 == 1225, "Resume discarded state or skipped loop iterations.");
                result.observed = slices + " slices, final result 1225.";
            }
            using (var fixture = new ValidationNativeFixture(new ScriptCompiler().Compile("void test(){for(;;){}}")))
            { fixture.Prepare(0); Require(fixture.Execute(31) == ExecutionStatus.Suspended && fixture.State.InstructionsExecuted == 31, "Infinite invocation exceeded its budget."); }
        }
        private static void Isolation(ValidationResult result, ValidationOptions options)
        {
            CompiledModule module = new ScriptCompiler().Compile("int test(int lane){return 120/lane;}");
            using (var program = new NativeScriptProgram(module)) using (var batch = new NativeScriptBatch(program, options.Lanes))
            {
                for (int lane = 0; lane < options.Lanes; lane++) batch.Prepare(lane, 0, Value.FromInt32(lane));
                batch.Schedule(1000, 1).Complete();
                Require(batch.GetState(0).Error == VmError.DivisionByZero, "Lane zero should fault.");
                for (int lane = 1; lane < options.Lanes; lane++) Require(batch.GetState(lane).Status == ExecutionStatus.Finished && batch.GetState(lane).ReturnValue.AsInt32 == 120 / lane, "Fault escaped its lane.");
            }
            result.observed = "Only lane 0 faulted; other private stacks returned exact integer quotients.";
        }
        private static void Globals(ValidationResult result, ValidationOptions options)
        {
            CompiledModule module = new ScriptCompiler().Compile("const int fixed=2;int total=0;int test(int add){total+=add;return total+fixed;}");
            using (var program = new NativeScriptProgram(module)) using (var batch = new NativeScriptBatch(program, options.Lanes))
            {
                for (int lane = 0; lane < options.Lanes; lane++) batch.Prepare(lane, 0, Value.FromInt32(lane));
                batch.Schedule().Complete(); batch.PrepareAll(0, Value.FromInt32(1)); batch.Schedule().Complete();
                for (int lane = 0; lane < options.Lanes; lane++) Require(batch.GetState(lane).ReturnValue.AsInt32 == lane + 3, "Globals not private/persistent.");
                Expect<InvalidOperationException>(() => batch.SetGlobal(0, module.FindGlobal("fixed"), Value.FromInt32(5)));
                Expect<ArgumentException>(() => batch.SetGlobal(0, module.FindGlobal("total"), Value.FromFloat(5)));
                batch.ResetGlobals(0); Require(batch.GetGlobal(0, module.FindGlobal("total")).AsInt32 == 0, "Global reset failed.");
                Require(batch.GetGlobal(1, module.FindGlobal("total")).AsInt32 == 2, "Reset affected another lane.");
            }
            result.observed = "Per-lane persistence/reset and const/type mutation guards passed.";
        }
        private static void SharedProgram(ValidationResult result, ValidationOptions options)
        {
            CompiledModule module = new ScriptCompiler().Compile("int total=0;int test(int add){total+=add;return total;}");
            using (var program = new NativeScriptProgram(module))
            using (var first = new NativeScriptBatch(program, options.Lanes)) using (var second = new NativeScriptBatch(program, options.Lanes))
            {
                first.PrepareAll(0, Value.FromInt32(3)); second.PrepareAll(0, Value.FromInt32(7));
                var a = first.Schedule(1000, 1); var b = second.Schedule(1000, 1);
                global::Unity.Jobs.JobHandle.CombineDependencies(a, b).Complete();
                for (int lane = 0; lane < options.Lanes; lane++) Require(first.GetState(lane).ReturnValue.AsInt32 == 3 && second.GetState(lane).ReturnValue.AsInt32 == 7, "Readonly sharing leaked mutable state.");
            }
            result.observed = "Concurrent readers and disjoint globals passed.";
        }
        private static void Dependencies(ValidationResult result, ValidationOptions options)
        {
            CompiledModule module = new ScriptCompiler().Compile("int test(){int sum=0;for(int i=0;i<20;i++)sum+=i;return sum;}");
            using (var program = new NativeScriptProgram(module)) using (var batch = new NativeScriptBatch(program, options.Lanes))
            {
                batch.PrepareAll(0);
                for (int slice = 0; slice < 100; slice++) batch.Schedule(10, 1); // No caller-provided chain; the owner must do it.
                batch.Complete();
                for (int lane = 0; lane < options.Lanes; lane++) Require(batch.GetState(lane).Status == ExecutionStatus.Finished && batch.GetState(lane).ReturnValue.AsInt32 == 190, "Schedules raced or resumed invalid state.");
            }
            result.observed = "100 queued schedules on the same batch completed in order.";
        }
        private static void Disposal(ValidationResult result, ValidationOptions options)
        {
            var compiler = ProbeCompiler(); compiler.RegisterHostFunction("float sample(float value)", 18);
            CompiledModule module = compiler.Compile("float test(){__as_validation_probe();return sample(2.0f);}");
            using (var markers = new NativeArray<int>(options.Lanes, Allocator.Persistent))
            using (var calls = new NativeArray<int>(options.Lanes, Allocator.Persistent))
            using (var values = new NativeArray<Value>(options.Lanes, Allocator.Persistent))
            {
                var program = new NativeScriptProgram(module); var batch = new NativeScriptBatch(program, options.Lanes);
                try
                {
                    batch.PrepareAll(0); var execution = batch.Schedule(new ValidationHost { Markers = markers, Calls = calls, HostValues = values }, 1000, 1);
                    var buffers = batch.Dispose(execution); batch.Complete(); batch.Dispose();
                    Require(buffers.IsCompleted, "Deferred batch cleanup did not complete.");
                    AssertMarkers(markers); Require(values[0].AsFloat == 4.0f, "Caller-owned host buffer lost its completed data.");
                    var code = program.Dispose(execution); program.Dispose(); program.Complete(); Require(code.IsCompleted, "Deferred program cleanup did not complete.");
                    Expect<ObjectDisposedException>(() => batch.PrepareAll(0)); Expect<ObjectDisposedException>(() => batch.GetState(0));
                }
                finally { batch.Dispose(); program.Dispose(); }
            }
            var shared = new NativeScriptProgram(new ScriptCompiler().Compile("int test(){return 7;}"));
            var reader = new NativeScriptBatch(shared, options.Lanes);
            try
            {
                reader.PrepareAll(0); reader.Schedule(); shared.Dispose();
                Require(reader.GetState(0).Status == ExecutionStatus.Finished, "Program disposal failed to wait for readers.");
                Expect<ObjectDisposedException>(() => reader.Schedule());
            }
            finally { reader.Dispose(); shared.Dispose(); }
            result.burstExecuted = true; result.observed = "Tracked readers, deferred cleanup, host buffer safety, idempotence and use-after-dispose guards passed.";
        }
        private static void Capacities(ValidationResult result)
        {
            CompiledModule recursive = new ScriptCompiler().Compile("int test(){return test();}");
            using (var fixture = new ValidationNativeFixture(recursive, new VmLimits { StackCapacity = 16, LocalCapacity = 16, CallCapacity = 3 }))
            { fixture.Prepare(0); fixture.Execute(1000); Require(fixture.State.Error == VmError.CallStackOverflow, "Call capacity not bounded."); }
            CompiledModule local = new ScriptCompiler().Compile("int test(){int n=1;return test()+n;}");
            using (var fixture = new ValidationNativeFixture(local, new VmLimits { StackCapacity = 16, LocalCapacity = 2, CallCapacity = 32 }))
            { fixture.Prepare(0); fixture.Execute(1000); Require(fixture.State.Error == VmError.LocalStackOverflow, "Local capacity not bounded."); }
            CompiledModule operands = new ScriptCompiler().Compile("int id(int x){return x;}int test(){return id(1)+id(2);}");
            using (var fixture = new ValidationNativeFixture(operands, new VmLimits { StackCapacity = 1, LocalCapacity = 16, CallCapacity = 8 }))
            { fixture.Prepare(operands.FindFunction("test")); fixture.Execute(1000); Require(fixture.State.Error == VmError.StackOverflow, "Operand capacity not bounded."); }
            result.observed = "Call/local/operand exhaustion produced explicit VmError.";
        }
        private static unsafe void InvalidBytecode(ValidationResult result)
        {
            CompiledModule module = new ScriptCompiler().Compile("int test(){return 1;}");
            using (var fixture = new ValidationNativeFixture(module))
            {
                ProgramView program = fixture.Program; VmMemory memory = fixture.Memory;
                Instruction saved = program.Instructions[0];
                program.Instructions[0] = new Instruction { Code = (OpCode)255 };
                fixture.Prepare(0); fixture.Execute(100); Require(fixture.State.Error == VmError.InvalidInstruction, "Unknown opcode accepted.");
                program.Instructions[0] = new Instruction { Code = OpCode.Jump, A = int.MaxValue };
                fixture.Prepare(0); fixture.Execute(100); Require(fixture.State.Error == VmError.InvalidInstruction, "Invalid target accepted.");
                program.Instructions[0] = saved;
                memory.Stack = null; ExecutionState state = default(ExecutionState);
                Require(VirtualMachine.Prepare(program, memory, ref state, 0, null, 0) == ExecutionStatus.Faulted && state.Error == VmError.InvalidMemory, "Null scratch pointer not rejected.");
            }
            result.observed = "Forged opcode/target/null-buffer descriptors faulted. Not a forged-pointer sandbox guarantee.";
        }
        private static void Preparation(ValidationResult result)
        {
            CompiledModule module = new ScriptCompiler().Compile("int test(float x){return int(x);}");
            using (var fixture = new ValidationNativeFixture(module))
            {
                fixture.Prepare(-1); Require(fixture.State.Error == VmError.InvalidFunction, "Invalid entry accepted.");
                fixture.Prepare(0); Require(fixture.State.Error == VmError.ArgumentCountMismatch, "Missing argument accepted.");
                fixture.Prepare(0, Value.FromInt32(1)); Require(fixture.State.Error == VmError.ArgumentTypeMismatch, "Wrong argument type accepted.");
                fixture.Prepare(0, Value.FromFloat(2)); fixture.Execute(100); Require(fixture.State.Status == ExecutionStatus.Finished && fixture.State.ReturnValue.AsInt32 == 2, "Recovery after failed preparation failed.");
            }
            result.observed = "Entry/count/type checks and clean re-Prepare passed.";
        }
        private static unsafe void LayoutAllocations(ValidationResult result)
        {
            Require(sizeof(Value) == 16 && sizeof(ScriptType) == 1, "Primitive ABI changed.");
            CompiledModule module = new ScriptCompiler().Compile("int test(){int n=0;for(int i=0;i<8;i++)n+=i;return n;}");
            using (var fixture = new ValidationNativeFixture(module))
            {
                ProgramView program = fixture.Program; VmMemory memory = fixture.Memory; ExecutionState state = default(ExecutionState);
                for (int i = 0; i < 64; i++) { VirtualMachine.Prepare(program, memory, ref state, 0, null, 0); VirtualMachine.Execute(program, memory, ref state, 1000); }
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 1000; i++) { VirtualMachine.Prepare(program, memory, ref state, 0, null, 0); VirtualMachine.Execute(program, memory, ref state, 1000); }
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                Require(allocated == 0, "Warmed Prepare/Execute allocated " + allocated + " managed bytes.");
                Require(state.Status == ExecutionStatus.Finished && state.ReturnValue.AsInt32 == 28, "Allocation workload did not execute correctly.");
                result.observed = "Value=16 bytes; ScriptType=1 byte; 1000 warmed invocations allocated 0 managed bytes.";
            }
        }
        private static void Diagnostics(ValidationResult result)
        {
            CompilationResult bad = new ScriptCompiler().TryCompile("int test(){\nreturn missing;\n}", "location.as");
            Require(!bad.Success && bad.Diagnostics.Count == 1 && bad.Diagnostics[0].Location.Line == 2 && bad.Diagnostics[0].Location.SourceName == "location.as", "Source diagnostics lost position.");
            var limited = new ScriptCompiler(new CompilerOptions { MaxInstructions = 1 });
            Require(limited.TryCompile("int test(){return 1;}").Diagnostics[0].Code == DiagnosticCode.ResourceLimit, "Compiler limit not applied.");
            var compiler = new ScriptCompiler(); compiler.RegisterHostFunction("float sqrt(float)", 9);
            Expect<ScriptCompilationException>(() => compiler.RegisterMathFunctions()); compiler.RegisterHostFunction("float sin(float)", 10);
            result.observed = "Positioned diagnostics, resource limit and transactional registration passed.";
        }
        private static void Repeatability(ValidationResult result, ValidationOptions options, Action<float[]> visuals)
        {
            CompiledModule module = new ScriptCompiler().Compile("int test(int seed){int n=seed;for(int i=0;i<10;i++)n=n*3+1;return n;}");
            var expected = new int[options.Lanes]; var display = new float[options.Lanes];
            for (int lane = 0; lane < options.Lanes; lane++) { int n = lane; for (int i = 0; i < 10; i++) n = unchecked(n * 3 + 1); expected[lane] = n; }
            using (var program = new NativeScriptProgram(module)) using (var batch = new NativeScriptBatch(program, options.Lanes))
            {
                for (int run = 0; run < 3; run++)
                {
                    for (int lane = 0; lane < options.Lanes; lane++) batch.Prepare(lane, 0, Value.FromInt32(lane));
                    batch.Schedule(10000, 1).Complete();
                    for (int lane = 0; lane < options.Lanes; lane++) Require(batch.GetState(lane).Status == ExecutionStatus.Finished && batch.GetState(lane).ReturnValue.AsInt32 == expected[lane], "Repeated lane output drifted.");
                }
                for (int lane = 0; lane < options.Lanes; lane++) display[lane] = 0.25f + (float)(Math.Abs(expected[lane]) % 100) / 40;
            }
            if (visuals != null) visuals(display);
            result.lanes = options.Lanes; result.observed = "Three repeated integer runs matched on all lanes; heights exported to scene visualization.";
        }
        private static void Expect<T>(Action action) where T : Exception
        {
            try { action(); } catch (T) { return; }
            throw new InvalidOperationException("Expected " + typeof(T).Name + ".");
        }
    }
}
