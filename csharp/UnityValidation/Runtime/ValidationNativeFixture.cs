using System;
using AngelScript.Burst.Compilation;
using AngelScript.Burst.Unity;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace AngelScript.Burst.Validation
{
    /// <summary>Real native buffers for direct managed-core/function-pointer comparison. No Unity emulation.</summary>
    public sealed unsafe class ValidationNativeFixture : IDisposable
    {
        private NativeArray<Instruction> code;
        private NativeArray<FunctionInfo> functions;
        private NativeArray<ScriptType> types;
        private NativeArray<Value> defaults, globals, stack, locals;
        private NativeArray<CallFrame> frames;
        public ExecutionState State;
        public readonly CompiledModule Module;
        public ValidationNativeFixture(CompiledModule module, VmLimits? limits = null)
        {
            Module = module;
            VmLimits capacity = limits ?? new VmLimits { StackCapacity = 256, LocalCapacity = 2048, CallCapacity = 64 };
            try
            {
                code = new NativeArray<Instruction>(module.CopyInstructions(), Allocator.Persistent);
                functions = new NativeArray<FunctionInfo>(module.CopyFunctionInfo(), Allocator.Persistent);
                types = new NativeArray<ScriptType>(module.CopySlotTypes(), Allocator.Persistent);
                defaults = new NativeArray<Value>(module.CopyGlobalDefaults(), Allocator.Persistent);
                globals = new NativeArray<Value>(module.Globals.Count, Allocator.Persistent);
                stack = new NativeArray<Value>(capacity.StackCapacity, Allocator.Persistent);
                locals = new NativeArray<Value>(capacity.LocalCapacity, Allocator.Persistent);
                frames = new NativeArray<CallFrame>(capacity.CallCapacity, Allocator.Persistent);
                if (!VirtualMachine.InitializeGlobals(Program, Memory)) throw new InvalidOperationException("Invalid fixture buffers/defaults.");
            }
            catch { Dispose(); throw; }
        }
        public ProgramView Program
        {
            get
            {
                return new ProgramView {
                    Instructions = (Instruction*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(code), InstructionCount = code.Length,
                    Functions = (FunctionInfo*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(functions), FunctionCount = functions.Length,
                    SlotTypes = (ScriptType*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(types), SlotTypeCount = types.Length,
                    GlobalDefaults = (Value*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(defaults), GlobalCount = defaults.Length
                };
            }
        }
        public VmMemory Memory
        {
            get
            {
                return new VmMemory {
                    Stack = (Value*)NativeArrayUnsafeUtility.GetUnsafePtr(stack), StackCapacity = stack.Length,
                    Locals = (Value*)NativeArrayUnsafeUtility.GetUnsafePtr(locals), LocalCapacity = locals.Length,
                    Frames = (CallFrame*)NativeArrayUnsafeUtility.GetUnsafePtr(frames), FrameCapacity = frames.Length,
                    Globals = (Value*)NativeArrayUnsafeUtility.GetUnsafePtr(globals), GlobalCapacity = globals.Length
                };
            }
        }
        public ExecutionStatus Prepare(int index, params Value[] arguments)
        {
            fixed (Value* args = arguments) return VirtualMachine.Prepare(Program, Memory, ref State, index, args, arguments.Length);
        }
        public ExecutionStatus Execute(int budget) { return VirtualMachine.Execute(Program, Memory, ref State, budget); }
        public void Dispose()
        {
            if (frames.IsCreated) frames.Dispose();
            if (locals.IsCreated) locals.Dispose();
            if (stack.IsCreated) stack.Dispose();
            if (globals.IsCreated) globals.Dispose();
            if (defaults.IsCreated) defaults.Dispose();
            if (types.IsCreated) types.Dispose();
            if (functions.IsCreated) functions.Dispose();
            if (code.IsCreated) code.Dispose();
        }
    }
}
