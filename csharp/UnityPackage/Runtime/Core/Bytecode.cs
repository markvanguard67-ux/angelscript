// Unofficial C# AngelScript subset; not the original AngelCode library.
using System.Runtime.InteropServices;

namespace AngelScript.Burst
{
    public enum OpCode : byte
    {
        Nop, Push, Pop, Duplicate, Swap, LoadLocal, StoreLocal, LoadGlobal, StoreGlobal,
        Convert, Negate, BitNot, LogicalNot,
        Add, Subtract, Multiply, Divide, Modulo, Power,
        BitAnd, BitOr, BitXor, ShiftLeft, ShiftRightLogical, ShiftRightArithmetic,
        Equal, NotEqual, Less, LessEqual, Greater, GreaterEqual, LogicalXor,
        Jump, JumpIfFalse, JumpIfTrue, Call, CallHost, Intrinsic, Return
    }

    public enum IntrinsicId : byte
    {
        Sin, Cos, Tan, Sqrt, Abs, Floor, Ceil, Exp, Log, Pow, Min, Max, Clamp
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Instruction
    {
        public OpCode Code;
        public ScriptType Type;
        public int A;
        public int B;
        public Value Constant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FunctionInfo
    {
        public int CodeOffset;
        public int CodeLength;
        public int ParameterCount;
        public int LocalCount;
        // Includes parameters first, followed by local variables and compiler temporaries.
        public int SlotTypeOffset;
        public ScriptType ReturnType;
    }

    /// <summary>Read-only program memory. The owner must keep every buffer alive while it is in use.</summary>
    public unsafe struct ProgramView
    {
        public Instruction* Instructions;
        public int InstructionCount;
        public FunctionInfo* Functions;
        public int FunctionCount;
        public ScriptType* SlotTypes;
        public int SlotTypeCount;
        public Value* GlobalDefaults;
        public int GlobalCount;
    }

    /// <summary>Private, preallocated buffers for exactly one VM instance. Never share writable buffers between lanes.</summary>
    public unsafe struct VmMemory
    {
        public Value* Stack;
        public int StackCapacity;
        public Value* Locals;
        public int LocalCapacity;
        public CallFrame* Frames;
        public int FrameCapacity;
        public Value* Globals;
        public int GlobalCapacity;
    }

    public struct CallFrame
    {
        public int FunctionIndex;
        public int LocalBase;
        public int StackBase;
        public int ReturnAddress;
    }

    public enum ExecutionStatus : byte
    {
        Unprepared, Ready, Suspended, HostCallPending, Finished, Faulted
    }

    public enum VmError : byte
    {
        None, InvalidProgram, InvalidMemory, InvalidState, InvalidFunction,
        ArgumentCountMismatch, ArgumentTypeMismatch, InvalidInstruction,
        StackOverflow, StackUnderflow, LocalStackOverflow, CallStackOverflow,
        TypeMismatch, InvalidConversion, DivisionByZero, IntegerOverflow,
        HostFunctionFailed, HostReturnTypeMismatch
    }

    /// <summary>Unmanaged execution registers, including a resumable host-call boundary.</summary>
    public struct ExecutionState
    {
        public ExecutionStatus Status;
        public VmError Error;
        public int InstructionPointer;
        public int FaultInstruction;
        public int FrameCount;
        public int StackCount;
        public int LocalCount;
        public long InstructionsExecuted;
        public Value ReturnValue;
        public int HostFunctionId;
        public int HostArgumentCount;
        public ScriptType HostReturnType;
    }
}
