// Unofficial C# AngelScript subset; not the original AngelCode library.
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;

namespace AngelScript.Burst.Unity
{
    /// <summary>
    /// Implement with a value-type host containing only job-compatible fields. Burst specializes
    /// the constrained call: there is no delegate, boxing, reflection or managed virtual dispatch.
    /// Arguments are in declaration order. Return default(Value) for a void signature.
    /// </summary>
    public unsafe interface IScriptHost
    {
        bool Invoke(int functionId, int laneIndex, Value* arguments, int argumentCount, out Value result);
    }

    public struct VmLimits
    {
        public int StackCapacity;
        public int LocalCapacity;
        public int CallCapacity;
        public static VmLimits Default
        {
            get { return new VmLimits { StackCapacity = 256, LocalCapacity = 2048, CallCapacity = 64 }; }
        }
    }

    internal struct ScriptJobData
    {
        [ReadOnly] internal NativeArray<Instruction> Instructions;
        [ReadOnly] internal NativeArray<FunctionInfo> Functions;
        [ReadOnly] internal NativeArray<ScriptType> SlotTypes;
        [ReadOnly] internal NativeArray<Value> GlobalDefaults;
        internal NativeArray<ExecutionState> States;
        // Each Execute(index) accesses only index*stride .. (index+1)*stride.
        // These attributes disable Unity's single-element parallel restriction, not allocation safety.
        [NativeDisableParallelForRestriction] internal NativeArray<Value> Stack;
        [NativeDisableParallelForRestriction] internal NativeArray<Value> Locals;
        [NativeDisableParallelForRestriction] internal NativeArray<CallFrame> Frames;
        [NativeDisableParallelForRestriction] internal NativeArray<Value> Globals;
        internal VmLimits Limits;

        internal unsafe ProgramView Program()
        {
            return new ProgramView
            {
                Instructions = (Instruction*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(Instructions), InstructionCount = Instructions.Length,
                Functions = (FunctionInfo*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(Functions), FunctionCount = Functions.Length,
                SlotTypes = (ScriptType*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(SlotTypes), SlotTypeCount = SlotTypes.Length,
                GlobalDefaults = (Value*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(GlobalDefaults), GlobalCount = GlobalDefaults.Length
            };
        }
        internal unsafe VmMemory Memory(int laneIndex)
        {
            int globalsPerLane = GlobalDefaults.Length;
            return new VmMemory
            {
                Stack = (Value*)NativeArrayUnsafeUtility.GetUnsafePtr(Stack) + laneIndex * Limits.StackCapacity, StackCapacity = Limits.StackCapacity,
                Locals = (Value*)NativeArrayUnsafeUtility.GetUnsafePtr(Locals) + laneIndex * Limits.LocalCapacity, LocalCapacity = Limits.LocalCapacity,
                Frames = (CallFrame*)NativeArrayUnsafeUtility.GetUnsafePtr(Frames) + laneIndex * Limits.CallCapacity, FrameCapacity = Limits.CallCapacity,
                Globals = (Value*)NativeArrayUnsafeUtility.GetUnsafePtr(Globals) + laneIndex * globalsPerLane, GlobalCapacity = globalsPerLane
            };
        }
    }

    internal unsafe struct NoHost : IScriptHost
    {
        public bool Invoke(int functionId, int laneIndex, Value* arguments, int argumentCount, out Value result)
        { result = default(Value); return false; }
    }

    internal static class ScriptJobRunner
    {
        internal static unsafe void Run<THost>(ScriptJobData data, int laneIndex, int budget, ref THost host) where THost : unmanaged, IScriptHost
        {
            ProgramView program = data.Program();
            VmMemory memory = data.Memory(laneIndex);
            ExecutionState state = data.States[laneIndex];
            int remaining = budget;
            while (remaining > 0)
            {
                long before = state.InstructionsExecuted;
                VirtualMachine.Execute(program, memory, ref state, remaining);
                remaining -= (int)(state.InstructionsExecuted - before);
                if (state.Status != ExecutionStatus.HostCallPending) break;
                Value result;
                bool success = host.Invoke(state.HostFunctionId, laneIndex, VirtualMachine.GetHostArguments(memory, state), state.HostArgumentCount, out result);
                VirtualMachine.CompleteHostCall(memory, ref state, result, success);
                if (state.Status != ExecutionStatus.Ready) break;
                if (remaining == 0) state.Status = ExecutionStatus.Suspended;
            }
            data.States[laneIndex] = state;
        }
    }

    [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard, CompileSynchronously = true)]
    internal struct ScriptBatchJob : IJobParallelFor
    {
        internal ScriptJobData Data;
        internal int InstructionBudget;
        public void Execute(int index)
        { var host = new NoHost(); ScriptJobRunner.Run(Data, index, InstructionBudget, ref host); }
    }

    /// <summary>Public for RegisterGenericJobType attributes. Schedule via NativeScriptBatch, not by constructing this job directly.</summary>
    [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard, CompileSynchronously = true)]
    public struct ScriptBatchJob<THost> : IJobParallelFor where THost : unmanaged, IScriptHost
    {
        internal ScriptJobData Data;
        internal int InstructionBudget;
        internal THost Host;
        public void Execute(int index)
        {
            // Make host registers lane-local; NativeArray fields still refer to the supplied native data.
            THost host = Host;
            ScriptJobRunner.Run(Data, index, InstructionBudget, ref host);
        }
    }
}
