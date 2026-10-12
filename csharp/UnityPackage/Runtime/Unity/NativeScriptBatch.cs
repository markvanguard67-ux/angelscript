// Unofficial C# AngelScript subset; not the original AngelCode library.
using System;
using Unity.Collections;
using Unity.Jobs;

namespace AngelScript.Burst.Unity
{
    /// <summary>
    /// Owns disjoint native VM lanes. Preparation, result access and disposal complete recorded
    /// jobs first. Schedule chains previous jobs on this batch, but different batches can run concurrently.
    /// </summary>
    public sealed class NativeScriptBatch : IDisposable
    {
        private readonly NativeScriptProgram program;
        private ScriptJobData data;
        private JobHandle lastUse;
        private bool disposed;
        public int Count { get; private set; }
        public VmLimits Limits { get { return data.Limits; } }

        public NativeScriptBatch(NativeScriptProgram program, int count, VmLimits? limits = null, Allocator allocator = Allocator.Persistent)
        {
            if (program == null) throw new ArgumentNullException("program");
            program.EnsureCreated(); NativeScriptProgram.ValidateAllocator(allocator);
            if (count < 0) throw new ArgumentOutOfRangeException("count");
            VmLimits capacities = limits ?? VmLimits.Default;
            if (capacities.StackCapacity < 0 || capacities.LocalCapacity < 0 || capacities.CallCapacity < 1)
                throw new ArgumentOutOfRangeException("limits", "Stack/local capacities must be nonnegative and call capacity must be positive.");
            this.program = program; Count = count;
            data.Instructions = program.Instructions; data.Functions = program.Functions;
            data.SlotTypes = program.SlotTypes; data.GlobalDefaults = program.GlobalDefaults; data.Limits = capacities;
            try
            {
                data.States = new NativeArray<ExecutionState>(count, allocator);
                data.Stack = new NativeArray<Value>(checked(count * capacities.StackCapacity), allocator);
                data.Locals = new NativeArray<Value>(checked(count * capacities.LocalCapacity), allocator);
                data.Frames = new NativeArray<CallFrame>(checked(count * capacities.CallCapacity), allocator);
                data.Globals = new NativeArray<Value>(checked(count * program.GlobalDefaults.Length), allocator);
                for (int lane = 0; lane < count; lane++)
                    for (int i = 0; i < program.GlobalDefaults.Length; i++) data.Globals[lane * program.GlobalDefaults.Length + i] = program.GlobalDefaults[i];
            }
            catch { DisposeArrays(); disposed = true; throw; }
        }

        /// <summary>Globals persist across Prepare. Parameters must have exactly the declared types.</summary>
        public unsafe ExecutionStatus Prepare(int laneIndex, int functionIndex, params Value[] arguments)
        {
            EnsureLane(laneIndex); program.EnsureCreated();
            if (arguments == null) throw new ArgumentNullException("arguments");
            lastUse.Complete();
            return PrepareLane(laneIndex, functionIndex, arguments);
        }
        public void PrepareAll(int functionIndex, params Value[] arguments)
        {
            EnsureCreated(); program.EnsureCreated();
            if (arguments == null) throw new ArgumentNullException("arguments");
            lastUse.Complete();
            for (int lane = 0; lane < Count; lane++) PrepareLane(lane, functionIndex, arguments);
        }
        private unsafe ExecutionStatus PrepareLane(int laneIndex, int functionIndex, Value[] arguments)
        {
            ExecutionState state = data.States[laneIndex];
            fixed (Value* pointer = arguments)
                VirtualMachine.Prepare(data.Program(), data.Memory(laneIndex), ref state, functionIndex, pointer, arguments.Length);
            data.States[laneIndex] = state;
            return state.Status;
        }

        /// <summary>Execute/resume each prepared lane for a bounded number of VM instructions.</summary>
        public JobHandle Schedule(int instructionBudget = 10000, int innerLoopBatchCount = 32, JobHandle dependency = default(JobHandle))
        {
            ValidateSchedule(instructionBudget, innerLoopBatchCount);
            var job = new ScriptBatchJob { Data = data, InstructionBudget = instructionBudget };
            JobHandle handle = job.Schedule(Count, innerLoopBatchCount, JobHandle.CombineDependencies(lastUse, dependency));
            Record(handle);
            return handle;
        }
        /// <summary>The caller owns native host buffers and must keep them alive until the returned handle completes.</summary>
        public JobHandle Schedule<THost>(THost host, int instructionBudget = 10000, int innerLoopBatchCount = 32, JobHandle dependency = default(JobHandle))
            where THost : unmanaged, IScriptHost
        {
            ValidateSchedule(instructionBudget, innerLoopBatchCount);
            var job = new ScriptBatchJob<THost> { Data = data, InstructionBudget = instructionBudget, Host = host };
            JobHandle handle = job.Schedule(Count, innerLoopBatchCount, JobHandle.CombineDependencies(lastUse, dependency));
            Record(handle);
            return handle;
        }
        private void ValidateSchedule(int budget, int batchCount)
        {
            EnsureCreated(); program.EnsureCreated();
            if (budget < 1) throw new ArgumentOutOfRangeException("instructionBudget");
            if (batchCount < 1) throw new ArgumentOutOfRangeException("innerLoopBatchCount");
        }
        private void Record(JobHandle handle) { lastUse = handle; program.RegisterUse(handle); }
        /// <summary>Wait for recorded work, including previously scheduled deferred disposal.</summary>
        public void Complete() { lastUse.Complete(); }

        public ExecutionState GetState(int laneIndex)
        { EnsureLane(laneIndex); lastUse.Complete(); return data.States[laneIndex]; }
        public Value GetGlobal(int laneIndex, int globalIndex)
        {
            EnsureGlobal(laneIndex, globalIndex); lastUse.Complete();
            return data.Globals[laneIndex * data.GlobalDefaults.Length + globalIndex];
        }
        public void SetGlobal(int laneIndex, int globalIndex, Value value)
        {
            EnsureGlobal(laneIndex, globalIndex);
            var global = program.Module.Globals[globalIndex];
            if (global.IsConst) throw new InvalidOperationException("Cannot change a const global (its uses may be inlined).");
            if (value.Type != global.Type) throw new ArgumentException("Global value type must exactly match its declaration.", "value");
            lastUse.Complete(); data.Globals[laneIndex * data.GlobalDefaults.Length + globalIndex] = value;
        }
        public unsafe void ResetGlobals(int laneIndex)
        {
            EnsureLane(laneIndex); program.EnsureCreated(); lastUse.Complete();
            VirtualMachine.InitializeGlobals(data.Program(), data.Memory(laneIndex));
        }
        private void EnsureCreated() { if (disposed) throw new ObjectDisposedException("NativeScriptBatch"); }
        private void EnsureLane(int laneIndex)
        { EnsureCreated(); if ((uint)laneIndex >= (uint)Count) throw new ArgumentOutOfRangeException("laneIndex"); }
        private void EnsureGlobal(int laneIndex, int globalIndex)
        {
            EnsureLane(laneIndex);
            if ((uint)globalIndex >= (uint)program.Module.Globals.Count) throw new ArgumentOutOfRangeException("globalIndex");
        }

        public void Dispose()
        {
            // A previous Dispose(dependency) may still have running jobs/deallocations.
            // Synchronous disposal must wait for them even though no buffers can be reused.
            lastUse.Complete();
            if (disposed) return;
            DisposeArrays(); disposed = true;
        }
        public JobHandle Dispose(JobHandle dependency)
        {
            if (disposed) return JobHandle.CombineDependencies(lastUse, dependency);
            JobHandle handle = JobHandle.CombineDependencies(lastUse, dependency);
            if (data.States.IsCreated) handle = data.States.Dispose(handle);
            if (data.Stack.IsCreated) handle = data.Stack.Dispose(handle);
            if (data.Locals.IsCreated) handle = data.Locals.Dispose(handle);
            if (data.Frames.IsCreated) handle = data.Frames.Dispose(handle);
            if (data.Globals.IsCreated) handle = data.Globals.Dispose(handle);
            lastUse = handle; disposed = true;
            return handle;
        }
        private void DisposeArrays()
        {
            if (data.States.IsCreated) data.States.Dispose();
            if (data.Stack.IsCreated) data.Stack.Dispose();
            if (data.Locals.IsCreated) data.Locals.Dispose();
            if (data.Frames.IsCreated) data.Frames.Dispose();
            if (data.Globals.IsCreated) data.Globals.Dispose();
        }
    }
}
