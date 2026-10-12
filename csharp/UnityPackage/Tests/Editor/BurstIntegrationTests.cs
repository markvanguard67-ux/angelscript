using System;
using System.Runtime.InteropServices;
using AngelScript.Burst.Compilation;
using AngelScript.Burst.Unity;
using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;

// Closed generic jobs must be visible to Unity's reflection/AOT job registration pass.
[assembly: RegisterGenericJobType(typeof(ScriptBatchJob<AngelScript.Burst.Tests.ProbeHost>))]

namespace AngelScript.Burst.Tests
{
    public unsafe struct ProbeHost : IScriptHost
    {
        public NativeArray<int> BurstMarkers;
        public bool Invoke(int functionId, int laneIndex, Value* arguments, int argumentCount, out Value result)
        {
            bool inBurst = true;
            MarkManaged(ref inBurst);
            BurstMarkers[laneIndex] = inBurst ? 1 : 0;
            result = default(Value);
            if (functionId != 12 || argumentCount != 1 || arguments[0].Type != ScriptType.Float) return false;
            result = Value.FromFloat(arguments[0].AsFloat * 2.0f);
            return true;
        }
        [BurstDiscard] private static void MarkManaged(ref bool inBurst) { inBurst = false; }
    }

    public unsafe class BurstIntegrationTests
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void ExecuteKernel(ProgramView* program, VmMemory* memory, ExecutionState* state, int budget, int* burstMarker);

        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict)]
        [AOT.MonoPInvokeCallback(typeof(ExecuteKernel))]
        private static void ExecuteInBurst(ProgramView* program, VmMemory* memory, ExecutionState* state, int budget, int* burstMarker)
        {
            bool inBurst = true;
            MarkManaged(ref inBurst);
            *burstMarker = inBurst ? 1 : 0;
            VirtualMachine.Execute(*program, *memory, ref *state, budget);
        }
        [BurstDiscard] private static void MarkManaged(ref bool inBurst) { inBurst = false; }

        [Test]
        public void ActualBurstFunctionPointerExecutesTheCoreAndMath()
        {
            RequireBurst();
            var compiler = new ScriptCompiler(); compiler.RegisterMathFunctions();
            // Exercise the audit fixes through the actual Burst kernel, including Math.Truncate
            // and the mixed double/int32 power instruction. These checks must not be constant-folded.
            CompiledModule module = compiler.Compile(@"int global=1;double test(){
                if(global+global++!=2)return 0.0;
                double x=9.0, base=-1.0, lower=-2147483648.5;
                int64 exponent=9007199254740993;float negative=-2.5f;
                if(base**exponent!=-1.0 || int(lower)!=-2147483648 || uint8(negative)!=254)return 0.0;
                return sqrt(x)+sin(0.0)+pow(2.0,3.0);
            }");
            using (var code = new NativeArray<Instruction>(module.CopyInstructions(), Allocator.TempJob))
            using (var functions = new NativeArray<FunctionInfo>(module.CopyFunctionInfo(), Allocator.TempJob))
            using (var types = new NativeArray<ScriptType>(module.CopySlotTypes(), Allocator.TempJob))
            using (var defaults = new NativeArray<Value>(module.CopyGlobalDefaults(), Allocator.TempJob))
            using (var globals = new NativeArray<Value>(module.Globals.Count, Allocator.TempJob))
            using (var stack = new NativeArray<Value>(64, Allocator.TempJob))
            using (var locals = new NativeArray<Value>(64, Allocator.TempJob))
            using (var frames = new NativeArray<CallFrame>(16, Allocator.TempJob))
            {
                var program = new ProgramView
                {
                    Instructions = (Instruction*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(code), InstructionCount = code.Length,
                    Functions = (FunctionInfo*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(functions), FunctionCount = functions.Length,
                    SlotTypes = (ScriptType*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(types), SlotTypeCount = types.Length,
                    GlobalDefaults = (Value*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(defaults), GlobalCount = defaults.Length
                };
                var memory = new VmMemory
                {
                    Stack = (Value*)NativeArrayUnsafeUtility.GetUnsafePtr(stack), StackCapacity = stack.Length,
                    Locals = (Value*)NativeArrayUnsafeUtility.GetUnsafePtr(locals), LocalCapacity = locals.Length,
                    Frames = (CallFrame*)NativeArrayUnsafeUtility.GetUnsafePtr(frames), FrameCapacity = frames.Length,
                    Globals = (Value*)NativeArrayUnsafeUtility.GetUnsafePtr(globals), GlobalCapacity = globals.Length
                };
                ExecutionState state = default(ExecutionState);
                Assert.IsTrue(VirtualMachine.InitializeGlobals(program, memory));
                Assert.AreEqual(ExecutionStatus.Ready, VirtualMachine.Prepare(program, memory, ref state, 0, null, 0));
                FunctionPointer<ExecuteKernel> kernel = BurstCompiler.CompileFunctionPointer<ExecuteKernel>(ExecuteInBurst);
                int marker = 0;
                kernel.Invoke(&program, &memory, &state, 1000, &marker);
                Assert.AreEqual(1, marker, "Kernel ran as managed C#, not Burst. Check Burst compilation errors/settings.");
                Assert.AreEqual(ExecutionStatus.Finished, state.Status);
                Assert.AreEqual(11.0, state.ReturnValue.AsDouble, 1e-12);
            }
        }

        [Test]
        public void ParallelHostJobsActuallyRunInBurstAndKeepGlobalsPrivate()
        {
            RequireBurst();
            const int count = 64;
            var compiler = new ScriptCompiler(); compiler.RegisterHostFunction("float sample(float value)", 12);
            CompiledModule module = compiler.Compile("float total=0;float tick(float x){total+=sample(x);return total;}");
            using (var markers = new NativeArray<int>(count, Allocator.TempJob))
            using (var program = new NativeScriptProgram(module))
            using (var batch = new NativeScriptBatch(program, count, SmallLimits()))
            {
                int tick = module.FindFunction("tick", ScriptType.Float);
                for (int i = 0; i < count; i++) Assert.AreEqual(ExecutionStatus.Ready, batch.Prepare(i, tick, Value.FromFloat(i)));
                batch.Schedule(new ProbeHost { BurstMarkers = markers }, 1000, 1).Complete();
                for (int i = 0; i < count; i++)
                {
                    Assert.AreEqual(1, markers[i], "Generic host job fell back to managed execution.");
                    Assert.AreEqual(ExecutionStatus.Finished, batch.GetState(i).Status);
                    Assert.AreEqual(i * 2.0f, batch.GetState(i).ReturnValue.AsFloat);
                }
                batch.PrepareAll(tick, Value.FromFloat(1));
                batch.Schedule(new ProbeHost { BurstMarkers = markers }, 1000, 1).Complete();
                for (int i = 0; i < count; i++) Assert.AreEqual(i * 2.0f + 2.0f, batch.GetState(i).ReturnValue.AsFloat);
            }
        }

        [Test]
        public void JobsResumeAtInstructionBudgets()
        {
            CompiledModule module = new ScriptCompiler().Compile("int tick(){int n=0;for(int i=0;i<30;i++)n+=i;return n;}");
            using (var program = new NativeScriptProgram(module))
            using (var batch = new NativeScriptBatch(program, 16, SmallLimits()))
            {
                batch.PrepareAll(module.FindFunction("tick"));
                int slices = 0;
                do
                {
                    long before = batch.GetState(0).InstructionsExecuted;
                    batch.Schedule(5, 1).Complete();
                    Assert.LessOrEqual(batch.GetState(0).InstructionsExecuted - before, 5);
                    Assert.Less(++slices, 1000);
                } while (batch.GetState(0).Status == ExecutionStatus.Suspended);
                Assert.Greater(slices, 1);
                for (int i = 0; i < batch.Count; i++)
                {
                    Assert.AreEqual(ExecutionStatus.Finished, batch.GetState(i).Status);
                    Assert.AreEqual(435, batch.GetState(i).ReturnValue.AsInt32);
                }
            }
        }

        [Test]
        public void OneLaneFaultDoesNotAffectOtherLanes()
        {
            CompiledModule module = new ScriptCompiler().Compile("int tick(int x){return 12/x;}");
            using (var program = new NativeScriptProgram(module))
            using (var batch = new NativeScriptBatch(program, 4, SmallLimits()))
            {
                for (int i = 0; i < batch.Count; i++) batch.Prepare(i, 0, Value.FromInt32(i));
                batch.Schedule(100, 1).Complete();
                Assert.AreEqual(ExecutionStatus.Faulted, batch.GetState(0).Status);
                Assert.AreEqual(VmError.DivisionByZero, batch.GetState(0).Error);
                for (int i = 1; i < batch.Count; i++)
                { Assert.AreEqual(ExecutionStatus.Finished, batch.GetState(i).Status); Assert.AreEqual(12 / i, batch.GetState(i).ReturnValue.AsInt32); }
            }
        }

        [Test]
        public void SharedProgramCanServeConcurrentBatches()
        {
            CompiledModule module = new ScriptCompiler().Compile("int tick(int x){return x*3;}");
            using (var program = new NativeScriptProgram(module))
            using (var a = new NativeScriptBatch(program, 64, SmallLimits()))
            using (var b = new NativeScriptBatch(program, 64, SmallLimits()))
            {
                a.PrepareAll(0, Value.FromInt32(2)); b.PrepareAll(0, Value.FromInt32(7));
                JobHandle aHandle = a.Schedule(), bHandle = b.Schedule();
                JobHandle.CombineDependencies(aHandle, bHandle).Complete();
                for (int i = 0; i < a.Count; i++) { Assert.AreEqual(6, a.GetState(i).ReturnValue.AsInt32); Assert.AreEqual(21, b.GetState(i).ReturnValue.AsInt32); }
            }
        }

        [Test]
        public void HostCallsWithoutAHostFaultExplicitly()
        {
            var compiler = new ScriptCompiler(); compiler.RegisterHostFunction("int missing()", 1);
            CompiledModule module = compiler.Compile("int tick(){return missing();}");
            using (var program = new NativeScriptProgram(module))
            using (var batch = new NativeScriptBatch(program, 1, SmallLimits()))
            {
                batch.PrepareAll(0); batch.Schedule().Complete();
                Assert.AreEqual(ExecutionStatus.Faulted, batch.GetState(0).Status);
                Assert.AreEqual(VmError.HostFunctionFailed, batch.GetState(0).Error);
            }
        }

        [Test]
        public void DisposeWaitsForRecordedJobsAndPreventsReuse()
        {
            CompiledModule module = new ScriptCompiler().Compile("int tick(){int n=0;for(int i=0;i<100;i++)n++;return n;}");
            var program = new NativeScriptProgram(module);
            var batch = new NativeScriptBatch(program, 32, SmallLimits());
            try
            {
                batch.PrepareAll(0); batch.Schedule(10000);
                program.Dispose(); // Must complete all readers before freeing their code.
                Assert.AreEqual(ExecutionStatus.Finished, batch.GetState(0).Status);
                Assert.AreEqual(100, batch.GetState(0).ReturnValue.AsInt32);
                Assert.Throws<ObjectDisposedException>(() => batch.Schedule());
                batch.Dispose(); batch.Dispose(); program.Dispose();
                Assert.Throws<ObjectDisposedException>(() => batch.GetState(0));
            }
            finally { batch.Dispose(); program.Dispose(); }
        }

        [Test]
        public void DeferredDisposalAndConstGlobalProtection()
        {
            CompiledModule module = new ScriptCompiler().Compile("const int n=2;int value=3;int tick(){return n+value;}");
            var program = new NativeScriptProgram(module);
            var batch = new NativeScriptBatch(program, 4, SmallLimits());
            try
            {
                Assert.Throws<InvalidOperationException>(() => batch.SetGlobal(0, module.FindGlobal("n"), Value.FromInt32(9)));
                batch.SetGlobal(0, module.FindGlobal("value"), Value.FromInt32(7));
                batch.PrepareAll(0); JobHandle execution = batch.Schedule(); execution.Complete();
                Assert.AreEqual(9, batch.GetState(0).ReturnValue.AsInt32);
                Assert.AreEqual(5, batch.GetState(1).ReturnValue.AsInt32);
                JobHandle buffers = batch.Dispose(execution), code = program.Dispose(execution);
                JobHandle.CombineDependencies(buffers, code).Complete();
            }
            finally { batch.Dispose(); program.Dispose(); }
        }

        [Test]
        public void OwnersCanCompletePreviouslyDeferredDisposal()
        {
            CompiledModule module = new ScriptCompiler().Compile("int tick(){return 7;}");
            var program = new NativeScriptProgram(module);
            var batch = new NativeScriptBatch(program, 16, SmallLimits());
            try
            {
                batch.PrepareAll(0);
                JobHandle execution = batch.Schedule();
                JobHandle buffers = batch.Dispose(execution), code = program.Dispose(execution);
                batch.Complete(); program.Complete();
                Assert.IsTrue(buffers.IsCompleted);
                Assert.IsTrue(code.IsCompleted);
                Assert.Throws<ObjectDisposedException>(() => batch.GetState(0));
            }
            finally { batch.Dispose(); program.Dispose(); }
        }

        [Test]
        public void SynchronousDisposalWaitsAfterDeferredDisposal()
        {
            var compiler = new ScriptCompiler(); compiler.RegisterHostFunction("float sample(float value)", 12);
            CompiledModule module = compiler.Compile("float tick(){return sample(2.0f);}");
            using (var markers = new NativeArray<int>(16, Allocator.TempJob))
            {
                var program = new NativeScriptProgram(module);
                var batch = new NativeScriptBatch(program, 16, SmallLimits());
                try
                {
                    batch.PrepareAll(0);
                    JobHandle execution = batch.Schedule(new ProbeHost { BurstMarkers = markers }, 1000, 1);
                    JobHandle buffers = batch.Dispose(execution);
                    batch.Dispose(); // Must complete the queued work, not return early as already disposed.
                    Assert.IsTrue(buffers.IsCompleted);
                    // Complete() must release job safety ownership of caller-owned host buffers too.
                    int marker = markers[0];
                    Assert.IsTrue(marker == 0 || marker == 1);
                    JobHandle code = program.Dispose(execution);
                    program.Dispose();
                    Assert.IsTrue(code.IsCompleted);
                }
                finally { batch.Dispose(); program.Dispose(); }
            }
        }

        private static VmLimits SmallLimits() { return new VmLimits { StackCapacity = 64, LocalCapacity = 64, CallCapacity = 16 }; }
        private static void RequireBurst()
        {
            if (!BurstCompiler.IsEnabled) Assert.Ignore("Enable Burst compilation to run the real-Burst probes. A managed fallback is not treated as a successful Burst test.");
        }
    }
}
