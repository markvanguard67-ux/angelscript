// Unofficial C# AngelScript subset; not the original AngelCode library.
namespace AngelScript.Burst
{
    /// <summary>
    /// A resumable, allocation-free interpreter. Its complete reachable execution path consists
    /// of unmanaged data and Burst-supported arithmetic. The caller owns and sizes all buffers.
    /// </summary>
    public static unsafe class VirtualMachine
    {
        public static bool InitializeGlobals(ProgramView program, VmMemory memory)
        {
            if (!ValidProgram(program) || !ValidMemory(program, memory)) return false;
            for (int i = 0; i < program.GlobalCount; i++)
            {
                Value value = program.GlobalDefaults[i];
                if (!ScriptTypes.IsValid(value.Type) || value.Type == ScriptType.Void) return false;
                memory.Globals[i] = value;
            }
            return true;
        }

        /// <summary>Start a function. Global memory is preserved; initialize it once with InitializeGlobals.</summary>
        public static ExecutionStatus Prepare(ProgramView program, VmMemory memory, ref ExecutionState state,
            int functionIndex, Value* arguments, int argumentCount)
        {
            state = new ExecutionState { FaultInstruction = -1, HostFunctionId = -1 };
            if (!ValidProgram(program)) return Fail(ref state, VmError.InvalidProgram, -1);
            if (!ValidMemory(program, memory)) return Fail(ref state, VmError.InvalidMemory, -1);
            if ((uint)functionIndex >= (uint)program.FunctionCount) return Fail(ref state, VmError.InvalidFunction, -1);
            FunctionInfo function = program.Functions[functionIndex];
            if (!ValidFunction(program, function)) return Fail(ref state, VmError.InvalidProgram, -1);
            if (argumentCount != function.ParameterCount || (argumentCount > 0 && arguments == null))
                return Fail(ref state, VmError.ArgumentCountMismatch, -1);
            if (memory.FrameCapacity == 0) return Fail(ref state, VmError.CallStackOverflow, -1);
            if (function.LocalCount > memory.LocalCapacity) return Fail(ref state, VmError.LocalStackOverflow, -1);
            for (int i = 0; i < argumentCount; i++)
                if (arguments[i].Type != program.SlotTypes[function.SlotTypeOffset + i])
                    return Fail(ref state, VmError.ArgumentTypeMismatch, -1);
            if (!InitializeLocals(program, memory, function, 0)) return Fail(ref state, VmError.InvalidProgram, -1);
            for (int i = 0; i < argumentCount; i++) memory.Locals[i] = arguments[i];
            memory.Frames[0] = new CallFrame { FunctionIndex = functionIndex, ReturnAddress = -1 };
            state.FrameCount = 1;
            state.LocalCount = function.LocalCount;
            state.InstructionPointer = function.CodeOffset;
            state.Status = ExecutionStatus.Ready;
            return state.Status;
        }

        /// <summary>Execute at most instructionBudget instructions; suspension is resumable without allocations.</summary>
        public static ExecutionStatus Execute(ProgramView program, VmMemory memory, ref ExecutionState state, int instructionBudget)
        {
            if (state.Status == ExecutionStatus.Finished || state.Status == ExecutionStatus.Faulted) return state.Status;
            if (!ValidProgram(program)) return Fail(ref state, VmError.InvalidProgram, state.InstructionPointer);
            if (!ValidMemory(program, memory)) return Fail(ref state, VmError.InvalidMemory, state.InstructionPointer);
            if (!ValidState(memory, state) || instructionBudget < 0)
                return Fail(ref state, VmError.InvalidState, state.InstructionPointer);
            if (state.Status == ExecutionStatus.HostCallPending) return state.Status;
            if (state.Status != ExecutionStatus.Ready && state.Status != ExecutionStatus.Suspended)
                return Fail(ref state, VmError.InvalidState, state.InstructionPointer);

            for (int executed = 0; executed < instructionBudget; executed++)
            {
                CallFrame frame = memory.Frames[state.FrameCount - 1];
                if ((uint)frame.FunctionIndex >= (uint)program.FunctionCount)
                    return Fail(ref state, VmError.InvalidState, state.InstructionPointer);
                FunctionInfo function = program.Functions[frame.FunctionIndex];
                if (!ValidFunction(program, function)) return Fail(ref state, VmError.InvalidProgram, state.InstructionPointer);
                if (frame.LocalBase < 0 || frame.LocalBase > state.LocalCount ||
                    function.LocalCount > state.LocalCount - frame.LocalBase ||
                    frame.StackBase < 0 || frame.StackBase > state.StackCount)
                    return Fail(ref state, VmError.InvalidState, state.InstructionPointer);
                int ip = state.InstructionPointer;
                if (!ValidTarget(function, ip)) return Fail(ref state, VmError.InvalidInstruction, ip);
                Instruction instruction = program.Instructions[ip];
                state.InstructionPointer = ip + 1;
                state.InstructionsExecuted++;
                int available = state.StackCount - frame.StackBase;
                Value result;
                VmError error;

                switch (instruction.Code)
                {
                    case OpCode.Nop: break;
                    case OpCode.Push:
                        if (!ScriptTypes.IsValid(instruction.Constant.Type) || instruction.Constant.Type == ScriptType.Void)
                            return Fail(ref state, VmError.InvalidInstruction, ip);
                        if (!Push(memory, ref state, instruction.Constant)) return Fail(ref state, VmError.StackOverflow, ip);
                        break;
                    case OpCode.Pop:
                        if (available < 1) return Fail(ref state, VmError.StackUnderflow, ip);
                        state.StackCount--;
                        break;
                    case OpCode.Duplicate:
                        if (available < 1) return Fail(ref state, VmError.StackUnderflow, ip);
                        if (!Push(memory, ref state, memory.Stack[state.StackCount - 1])) return Fail(ref state, VmError.StackOverflow, ip);
                        break;
                    case OpCode.Swap:
                        if (available < 2) return Fail(ref state, VmError.StackUnderflow, ip);
                        result = memory.Stack[state.StackCount - 1];
                        memory.Stack[state.StackCount - 1] = memory.Stack[state.StackCount - 2];
                        memory.Stack[state.StackCount - 2] = result;
                        break;
                    case OpCode.LoadLocal:
                        if ((uint)instruction.A >= (uint)function.LocalCount) return Fail(ref state, VmError.InvalidInstruction, ip);
                        result = memory.Locals[frame.LocalBase + instruction.A];
                        if (result.Type != program.SlotTypes[function.SlotTypeOffset + instruction.A]) return Fail(ref state, VmError.TypeMismatch, ip);
                        if (!Push(memory, ref state, result)) return Fail(ref state, VmError.StackOverflow, ip);
                        break;
                    case OpCode.StoreLocal:
                        if ((uint)instruction.A >= (uint)function.LocalCount) return Fail(ref state, VmError.InvalidInstruction, ip);
                        if (available < 1) return Fail(ref state, VmError.StackUnderflow, ip);
                        result = memory.Stack[state.StackCount - 1];
                        if (result.Type != program.SlotTypes[function.SlotTypeOffset + instruction.A]) return Fail(ref state, VmError.TypeMismatch, ip);
                        memory.Locals[frame.LocalBase + instruction.A] = result;
                        state.StackCount--;
                        break;
                    case OpCode.LoadGlobal:
                        if ((uint)instruction.A >= (uint)program.GlobalCount) return Fail(ref state, VmError.InvalidInstruction, ip);
                        result = memory.Globals[instruction.A];
                        if (result.Type != program.GlobalDefaults[instruction.A].Type) return Fail(ref state, VmError.TypeMismatch, ip);
                        if (!Push(memory, ref state, result)) return Fail(ref state, VmError.StackOverflow, ip);
                        break;
                    case OpCode.StoreGlobal:
                        if ((uint)instruction.A >= (uint)program.GlobalCount) return Fail(ref state, VmError.InvalidInstruction, ip);
                        if (available < 1) return Fail(ref state, VmError.StackUnderflow, ip);
                        result = memory.Stack[state.StackCount - 1];
                        if (result.Type != program.GlobalDefaults[instruction.A].Type) return Fail(ref state, VmError.TypeMismatch, ip);
                        memory.Globals[instruction.A] = result;
                        state.StackCount--;
                        break;
                    case OpCode.Convert:
                        if (available < 1) return Fail(ref state, VmError.StackUnderflow, ip);
                        if (instruction.Type == ScriptType.Void || !NumericOperations.TryConvert(memory.Stack[state.StackCount - 1], instruction.Type, out result))
                            return Fail(ref state, VmError.InvalidConversion, ip);
                        memory.Stack[state.StackCount - 1] = result;
                        break;
                    case OpCode.Negate:
                    case OpCode.BitNot:
                    case OpCode.LogicalNot:
                        if (available < 1) return Fail(ref state, VmError.StackUnderflow, ip);
                        if (!NumericOperations.TryUnary(instruction.Code, memory.Stack[state.StackCount - 1], out result, out error))
                            return Fail(ref state, error, ip);
                        memory.Stack[state.StackCount - 1] = result;
                        break;
                    case OpCode.Jump:
                        if (!ValidTarget(function, instruction.A)) return Fail(ref state, VmError.InvalidInstruction, ip);
                        state.InstructionPointer = instruction.A;
                        break;
                    case OpCode.JumpIfFalse:
                    case OpCode.JumpIfTrue:
                        if (available < 1) return Fail(ref state, VmError.StackUnderflow, ip);
                        result = memory.Stack[state.StackCount - 1];
                        if (result.Type != ScriptType.Bool) return Fail(ref state, VmError.TypeMismatch, ip);
                        if (!ValidTarget(function, instruction.A)) return Fail(ref state, VmError.InvalidInstruction, ip);
                        state.StackCount--;
                        if (result.AsBool == (instruction.Code == OpCode.JumpIfTrue)) state.InstructionPointer = instruction.A;
                        break;
                    case OpCode.Call:
                        if ((uint)instruction.A >= (uint)program.FunctionCount) return Fail(ref state, VmError.InvalidFunction, ip);
                        FunctionInfo callee = program.Functions[instruction.A];
                        if (!ValidFunction(program, callee)) return Fail(ref state, VmError.InvalidProgram, ip);
                        if (available < callee.ParameterCount) return Fail(ref state, VmError.StackUnderflow, ip);
                        if (state.FrameCount >= memory.FrameCapacity) return Fail(ref state, VmError.CallStackOverflow, ip);
                        if (callee.LocalCount > memory.LocalCapacity - state.LocalCount) return Fail(ref state, VmError.LocalStackOverflow, ip);
                        for (int i = 0; i < callee.ParameterCount; i++)
                            if (memory.Stack[state.StackCount - 1 - i].Type != program.SlotTypes[callee.SlotTypeOffset + i])
                                return Fail(ref state, VmError.ArgumentTypeMismatch, ip);
                        if (!InitializeLocals(program, memory, callee, state.LocalCount)) return Fail(ref state, VmError.InvalidProgram, ip);
                        for (int i = 0; i < callee.ParameterCount; i++)
                            memory.Locals[state.LocalCount + i] = memory.Stack[state.StackCount - 1 - i];
                        state.StackCount -= callee.ParameterCount;
                        memory.Frames[state.FrameCount] = new CallFrame
                        {
                            FunctionIndex = instruction.A,
                            ReturnAddress = state.InstructionPointer,
                            LocalBase = state.LocalCount,
                            StackBase = state.StackCount
                        };
                        state.FrameCount++;
                        state.LocalCount += callee.LocalCount;
                        state.InstructionPointer = callee.CodeOffset;
                        break;
                    case OpCode.CallHost:
                        if (instruction.A < 0 || instruction.B < 0 || !ScriptTypes.IsValid(instruction.Type))
                            return Fail(ref state, VmError.InvalidInstruction, ip);
                        if (available < instruction.B) return Fail(ref state, VmError.StackUnderflow, ip);
                        ReverseArguments(memory, state.StackCount, instruction.B);
                        state.HostFunctionId = instruction.A;
                        state.HostArgumentCount = instruction.B;
                        state.HostReturnType = instruction.Type;
                        state.Status = ExecutionStatus.HostCallPending;
                        return state.Status;
                    case OpCode.Intrinsic:
                        if (instruction.A < 0 || instruction.A > (int)IntrinsicId.Clamp || instruction.B < 0)
                            return Fail(ref state, VmError.InvalidInstruction, ip);
                        if (available < instruction.B) return Fail(ref state, VmError.StackUnderflow, ip);
                        ReverseArguments(memory, state.StackCount, instruction.B);
                        if (!NumericOperations.TryIntrinsic((IntrinsicId)instruction.A, memory.Stack + state.StackCount - instruction.B,
                            instruction.B, instruction.Type, out result, out error)) return Fail(ref state, error, ip);
                        state.StackCount -= instruction.B;
                        if (!Push(memory, ref state, result)) return Fail(ref state, VmError.StackOverflow, ip);
                        break;
                    case OpCode.Return:
                        bool hasReturn = function.ReturnType != ScriptType.Void;
                        if (available != (hasReturn ? 1 : 0))
                            return Fail(ref state, available < (hasReturn ? 1 : 0) ? VmError.StackUnderflow : VmError.InvalidState, ip);
                        result = hasReturn ? memory.Stack[state.StackCount - 1] : default(Value);
                        if (result.Type != function.ReturnType) return Fail(ref state, VmError.TypeMismatch, ip);
                        state.StackCount = frame.StackBase;
                        state.LocalCount = frame.LocalBase;
                        state.FrameCount--;
                        if (state.FrameCount == 0)
                        {
                            state.ReturnValue = result;
                            state.Status = ExecutionStatus.Finished;
                            return state.Status;
                        }
                        state.InstructionPointer = frame.ReturnAddress;
                        if (hasReturn && !Push(memory, ref state, result)) return Fail(ref state, VmError.StackOverflow, ip);
                        break;
                    default:
                        if (instruction.Code < OpCode.Add || instruction.Code > OpCode.LogicalXor)
                            return Fail(ref state, VmError.InvalidInstruction, ip);
                        if (available < 2) return Fail(ref state, VmError.StackUnderflow, ip);
                        if (!NumericOperations.TryBinary(instruction.Code, memory.Stack[state.StackCount - 2], memory.Stack[state.StackCount - 1], out result, out error))
                            return Fail(ref state, error, ip);
                        memory.Stack[state.StackCount - 2] = result;
                        state.StackCount--;
                        break;
                }
            }
            state.Status = ExecutionStatus.Suspended;
            return state.Status;
        }

        /// <summary>Arguments are in declaration order and valid only until CompleteHostCall / Prepare.</summary>
        public static Value* GetHostArguments(VmMemory memory, ExecutionState state)
        {
            if (state.Status != ExecutionStatus.HostCallPending || state.HostArgumentCount < 0 ||
                state.StackCount < state.HostArgumentCount || state.StackCount > memory.StackCapacity || memory.Stack == null) return null;
            return memory.Stack + state.StackCount - state.HostArgumentCount;
        }

        /// <summary>Resume a pending host call. Return an exactly typed value, or default(Value) for void.</summary>
        public static ExecutionStatus CompleteHostCall(VmMemory memory, ref ExecutionState state, Value result, bool succeeded = true)
        {
            if (state.Status != ExecutionStatus.HostCallPending || !ValidMemory(default(ProgramView), memory) || !ValidState(memory, state) ||
                !ScriptTypes.IsValid(state.HostReturnType) || state.HostArgumentCount < 0 || state.HostArgumentCount > state.StackCount)
                return Fail(ref state, VmError.InvalidState, state.InstructionPointer - 1);
            CallFrame frame = memory.Frames[state.FrameCount - 1];
            if (frame.StackBase < 0 || frame.StackBase > state.StackCount || state.HostArgumentCount > state.StackCount - frame.StackBase)
                return Fail(ref state, VmError.InvalidState, state.InstructionPointer - 1);
            if (!succeeded) return Fail(ref state, VmError.HostFunctionFailed, state.InstructionPointer - 1);
            if (result.Type != state.HostReturnType) return Fail(ref state, VmError.HostReturnTypeMismatch, state.InstructionPointer - 1);
            state.StackCount -= state.HostArgumentCount;
            if (state.HostReturnType != ScriptType.Void && !Push(memory, ref state, result))
                return Fail(ref state, VmError.StackOverflow, state.InstructionPointer - 1);
            state.HostFunctionId = -1;
            state.HostArgumentCount = 0;
            state.Status = ExecutionStatus.Ready;
            return state.Status;
        }

        private static void ReverseArguments(VmMemory memory, int top, int count)
        {
            for (int i = 0; i < count / 2; i++)
            {
                Value temporary = memory.Stack[top - count + i];
                memory.Stack[top - count + i] = memory.Stack[top - 1 - i];
                memory.Stack[top - 1 - i] = temporary;
            }
        }

        private static bool Push(VmMemory memory, ref ExecutionState state, Value value)
        {
            if (state.StackCount >= memory.StackCapacity) return false;
            memory.Stack[state.StackCount++] = value;
            return true;
        }

        private static bool InitializeLocals(ProgramView program, VmMemory memory, FunctionInfo function, int start)
        {
            for (int i = 0; i < function.LocalCount; i++)
            {
                ScriptType type = program.SlotTypes[function.SlotTypeOffset + i];
                if (!ScriptTypes.IsValid(type) || type == ScriptType.Void) return false;
                memory.Locals[start + i] = Value.Zero(type);
            }
            return true;
        }

        private static bool ValidProgram(ProgramView program)
        {
            return program.InstructionCount >= 0 && (program.InstructionCount == 0 || program.Instructions != null) &&
                program.FunctionCount >= 0 && (program.FunctionCount == 0 || program.Functions != null) &&
                program.SlotTypeCount >= 0 && (program.SlotTypeCount == 0 || program.SlotTypes != null) &&
                program.GlobalCount >= 0 && (program.GlobalCount == 0 || program.GlobalDefaults != null);
        }

        private static bool ValidMemory(ProgramView program, VmMemory memory)
        {
            return memory.StackCapacity >= 0 && (memory.StackCapacity == 0 || memory.Stack != null) &&
                memory.LocalCapacity >= 0 && (memory.LocalCapacity == 0 || memory.Locals != null) &&
                memory.FrameCapacity >= 0 && (memory.FrameCapacity == 0 || memory.Frames != null) &&
                memory.GlobalCapacity >= program.GlobalCount && (memory.GlobalCapacity == 0 || memory.Globals != null);
        }

        private static bool ValidState(VmMemory memory, ExecutionState state)
        {
            return state.FrameCount > 0 && state.FrameCount <= memory.FrameCapacity &&
                state.StackCount >= 0 && state.StackCount <= memory.StackCapacity &&
                state.LocalCount >= 0 && state.LocalCount <= memory.LocalCapacity && state.InstructionsExecuted >= 0;
        }

        private static bool ValidFunction(ProgramView program, FunctionInfo function)
        {
            return function.CodeOffset >= 0 && function.CodeOffset < program.InstructionCount &&
                function.CodeLength > 0 && function.CodeLength <= program.InstructionCount - function.CodeOffset &&
                function.ParameterCount >= 0 && function.LocalCount >= function.ParameterCount &&
                function.SlotTypeOffset >= 0 && function.SlotTypeOffset <= program.SlotTypeCount &&
                function.LocalCount <= program.SlotTypeCount - function.SlotTypeOffset && ScriptTypes.IsValid(function.ReturnType);
        }

        private static bool ValidTarget(FunctionInfo function, int target)
        {
            return target >= function.CodeOffset && target - function.CodeOffset < function.CodeLength;
        }

        private static ExecutionStatus Fail(ref ExecutionState state, VmError error, int instruction)
        {
            state.Status = ExecutionStatus.Faulted;
            state.Error = error;
            state.FaultInstruction = instruction;
            return state.Status;
        }
    }
}
