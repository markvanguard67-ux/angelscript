using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using AngelScript.Burst;
using AngelScript.Burst.Compilation;

namespace AngelScript.Tests
{
    // Test-only owner. Managed allocations/pinning happen outside the VM, never in Execute.
    internal sealed unsafe class VmFixture : IDisposable
    {
        internal readonly Instruction[] Instructions;
        internal readonly FunctionInfo[] Functions;
        internal readonly ScriptType[] SlotTypes;
        internal readonly Value[] GlobalDefaults;
        internal readonly Value[] Globals;
        internal readonly Value[] Stack;
        internal readonly Value[] Locals;
        internal readonly CallFrame[] Frames;
        internal ProgramView Program;
        internal VmMemory Memory;
        internal ExecutionState State;
        private readonly List<GCHandle> handles = new List<GCHandle>();

        internal VmFixture(CompiledModule module, int stack = 256, int locals = 2048, int frames = 64)
        {
            Instructions = module.CopyInstructions(); Functions = module.CopyFunctionInfo();
            SlotTypes = module.CopySlotTypes(); GlobalDefaults = module.CopyGlobalDefaults();
            Globals = new Value[GlobalDefaults.Length]; Stack = new Value[stack]; Locals = new Value[locals]; Frames = new CallFrame[frames];
            Program = new ProgramView
            {
                Instructions = (Instruction*)Pin(Instructions), InstructionCount = Instructions.Length,
                Functions = (FunctionInfo*)Pin(Functions), FunctionCount = Functions.Length,
                // Older CoreCLR versions cannot GCHandle-pin an enum[]; the ABI is byte-sized.
                SlotTypes = (ScriptType*)PinSlotTypes(SlotTypes), SlotTypeCount = SlotTypes.Length,
                GlobalDefaults = (Value*)Pin(GlobalDefaults), GlobalCount = GlobalDefaults.Length
            };
            Memory = new VmMemory
            {
                Stack = (Value*)Pin(Stack), StackCapacity = stack, Locals = (Value*)Pin(Locals), LocalCapacity = locals,
                Frames = (CallFrame*)Pin(Frames), FrameCapacity = frames, Globals = (Value*)Pin(Globals), GlobalCapacity = Globals.Length
            };
            if (!VirtualMachine.InitializeGlobals(Program, Memory)) throw new InvalidOperationException("Invalid fixture globals.");
        }
        private void* PinSlotTypes(ScriptType[] types)
        {
            var bytes = new byte[types.Length];
            for (int i = 0; i < types.Length; i++) bytes[i] = (byte)types[i];
            return Pin(bytes);
        }
        private void* Pin(Array array)
        {
            GCHandle handle;
            try { handle = GCHandle.Alloc(array, GCHandleType.Pinned); }
            catch (ArgumentException exception) { throw new ArgumentException("Cannot pin test buffer " + array.GetType(), exception); }
            handles.Add(handle);
            return handle.AddrOfPinnedObject().ToPointer();
        }
        internal ExecutionStatus Prepare(int functionIndex, params Value[] arguments)
        {
            fixed (Value* pointer = arguments) return VirtualMachine.Prepare(Program, Memory, ref State, functionIndex, pointer, arguments.Length);
        }
        internal ExecutionStatus Run(int budget = 100000)
        { return VirtualMachine.Execute(Program, Memory, ref State, budget); }
        internal Value* HostArguments() { return VirtualMachine.GetHostArguments(Memory, State); }
        internal ExecutionStatus ReturnHost(Value value, bool success = true)
        { return VirtualMachine.CompleteHostCall(Memory, ref State, value, success); }
        public void Dispose()
        { foreach (GCHandle handle in handles) handle.Free(); handles.Clear(); }
    }
}
