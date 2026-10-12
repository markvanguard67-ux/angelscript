using System.Runtime.InteropServices;
using AngelScript.Burst.Unity;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

// This closed generic is reachable in Editor and IL2CPP player builds, not only test assemblies.
[assembly: RegisterGenericJobType(typeof(ScriptBatchJob<AngelScript.Burst.Validation.ValidationHost>))]

namespace AngelScript.Burst.Validation
{
    public unsafe struct ValidationHost : IScriptHost
    {
        public NativeArray<int> Markers;
        public NativeArray<int> Calls;
        public NativeArray<Value> HostValues;
        public bool Invoke(int id, int lane, Value* args, int count, out Value value)
        {
            bool inBurst = true; MarkManaged(ref inBurst);
            Markers[lane] = inBurst ? 1 : -1;
            Calls[lane] = Calls[lane] + 1;
            value = default(Value);
            switch (id)
            {
                case 0: return count == 0; // Mandatory entry probe, void return.
                case 17:
                    if (count != 2 || args[0].Type != ScriptType.Int32 || args[1].Type != ScriptType.Int32) return false;
                    value = Value.FromInt32(args[0].AsInt32 * 100 + args[1].AsInt32);
                    HostValues[lane] = value; return true;
                case 18:
                    if (count != 1 || args[0].Type != ScriptType.Float) return false;
                    value = Value.FromFloat(args[0].AsFloat * 2.0f); HostValues[lane] = value; return true;
                case 19: value = Value.FromFloat(1); return true; // Intentional return-type mismatch.
                case 20: return false;
                default: return false;
            }
        }
        [BurstDiscard] private static void MarkManaged(ref bool inBurst) { inBurst = false; }
    }

    public static unsafe class ValidationKernels
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void ExecuteKernel(ProgramView* program, VmMemory* memory, ExecutionState* state, int budget, int* marker);
        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
        [AOT.MonoPInvokeCallback(typeof(ExecuteKernel))]
        public static void Execute(ProgramView* program, VmMemory* memory, ExecutionState* state, int budget, int* marker)
        {
            bool inBurst = true; MarkManaged(ref inBurst); *marker = inBurst ? 1 : -1;
            VirtualMachine.Execute(*program, *memory, ref *state, budget);
        }
        [BurstDiscard] private static void MarkManaged(ref bool inBurst) { inBurst = false; }
    }
}
