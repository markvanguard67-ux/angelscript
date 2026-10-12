// Unofficial C# AngelScript subset; not the original AngelCode library.
using System.Collections.Generic;

namespace AngelScript.Burst.Compilation
{
    internal sealed class BytecodeEmitter
    {
        private readonly BindingResult binding;
        private readonly CompilerOptions options;
        private readonly string sourceName;
        private readonly List<Instruction> code = new List<Instruction>();
        private readonly List<SourceLocation> locations = new List<SourceLocation>();
        private readonly List<ControlTarget> controls = new List<ControlTarget>();
        private sealed class ControlTarget
        {
            internal bool IsLoop;
            internal readonly List<int> Breaks = new List<int>();
            internal readonly List<int> Continues = new List<int>();
        }
        internal BytecodeEmitter(BindingResult binding, CompilerOptions options, string sourceName)
        { this.binding = binding; this.options = options; this.sourceName = sourceName; }

        internal CompiledModule Emit()
        {
            var info = new FunctionInfo[binding.Functions.Count];
            var functions = new FunctionMetadata[info.Length];
            var types = new List<ScriptType>();
            for (int i = 0; i < info.Length; i++)
            {
                BoundFunction function = binding.Functions[i];
                int start = code.Count;
                int typeStart = types.Count;
                types.AddRange(function.SlotTypes);
                EmitStatement(function.Body);
                // Every branch target has an instruction, even at the end of a function.
                // In non-void functions this is unreachable by the checked control-flow analysis.
                Add(OpCode.Return, function.Symbol.Syntax.Location);
                info[i] = new FunctionInfo
                {
                    CodeOffset = start, CodeLength = code.Count - start,
                    ParameterCount = function.Symbol.ParameterTypes.Length, LocalCount = function.SlotTypes.Count,
                    SlotTypeOffset = typeStart, ReturnType = function.Symbol.ReturnType
                };
                functions[i] = new FunctionMetadata(function.Symbol);
            }
            var defaults = new Value[binding.Globals.Count];
            var globals = new GlobalMetadata[defaults.Length];
            for (int i = 0; i < defaults.Length; i++) { defaults[i] = binding.Globals[i].InitialValue; globals[i] = new GlobalMetadata(binding.Globals[i]); }
            return new CompiledModule(sourceName, code.ToArray(), info, types.ToArray(), defaults, locations.ToArray(), functions, globals);
        }

        private int Add(OpCode op, SourceLocation location, int a = 0, int b = 0, ScriptType type = ScriptType.Void)
        { return Add(new Instruction { Code = op, A = a, B = b, Type = type }, location); }
        private int Add(Instruction instruction, SourceLocation location)
        {
            if (code.Count >= options.MaxInstructions) throw new CompilationFailure(location, DiagnosticCode.ResourceLimit, "Bytecode size exceeds MaxInstructions.");
            int index = code.Count;
            code.Add(instruction); locations.Add(location);
            return index;
        }
        private void Push(Value value, SourceLocation location) { Add(new Instruction { Code = OpCode.Push, Constant = value }, location); }
        private void Patch(int index, int target) { Instruction instruction = code[index]; instruction.A = target; code[index] = instruction; }
        private void PatchControl(ControlTarget control, int end, int continueTarget)
        {
            foreach (int index in control.Breaks) Patch(index, end);
            foreach (int index in control.Continues) Patch(index, continueTarget);
            controls.RemoveAt(controls.Count - 1);
        }
        private void Load(VariableSymbol variable, SourceLocation location) { Add(variable.IsGlobal ? OpCode.LoadGlobal : OpCode.LoadLocal, location, variable.Index); }
        private void Store(VariableSymbol variable, SourceLocation location) { Add(variable.IsGlobal ? OpCode.StoreGlobal : OpCode.StoreLocal, location, variable.Index); }
        private void Convert(ScriptType source, ScriptType target, SourceLocation location)
        { if (source != target) Add(OpCode.Convert, location, type: target); }

        private void EmitStatement(BoundStatement statement)
        {
            var block = statement as BoundBlock;
            if (block != null) { foreach (BoundStatement item in block.Statements) EmitStatement(item); return; }
            var declaration = statement as BoundDeclaration;
            if (declaration != null) { EmitExpression(declaration.Initializer); Store(declaration.Variable, declaration.Location); return; }
            var expression = statement as BoundExpressionStatement;
            if (expression != null) { if (expression.Expression != null) EmitDiscard(expression.Expression); return; }
            var condition = statement as BoundIf;
            if (condition != null)
            {
                EmitExpression(condition.Condition);
                int otherwise = Add(OpCode.JumpIfFalse, condition.Location);
                EmitStatement(condition.Then);
                if (condition.Else == null) Patch(otherwise, code.Count);
                else
                {
                    int end = Add(OpCode.Jump, condition.Location);
                    Patch(otherwise, code.Count);
                    EmitStatement(condition.Else);
                    Patch(end, code.Count);
                }
                return;
            }
            var loop = statement as BoundWhile;
            if (loop != null) { EmitWhile(loop); return; }
            var forLoop = statement as BoundFor;
            if (forLoop != null) { EmitFor(forLoop); return; }
            var returnStatement = statement as BoundReturn;
            if (returnStatement != null)
            {
                if (returnStatement.Expression != null) EmitExpression(returnStatement.Expression);
                Add(OpCode.Return, returnStatement.Location);
                return;
            }
            var control = statement as BoundControl;
            if (control != null)
            {
                for (int i = controls.Count - 1; i >= 0; i--)
                {
                    if (control.IsContinue && !controls[i].IsLoop) continue;
                    int jump = Add(OpCode.Jump, control.Location);
                    (control.IsContinue ? controls[i].Continues : controls[i].Breaks).Add(jump);
                    return;
                }
                throw new System.InvalidOperationException("No bound control target.");
            }
            var switchStatement = statement as BoundSwitch;
            if (switchStatement != null) { EmitSwitch(switchStatement); return; }
            throw new System.InvalidOperationException("Unknown bound statement.");
        }

        private void EmitWhile(BoundWhile loop)
        {
            var control = new ControlTarget { IsLoop = true };
            controls.Add(control);
            int start = code.Count;
            if (loop.IsDoWhile)
            {
                EmitStatement(loop.Body);
                int condition = code.Count;
                EmitExpression(loop.Condition);
                Add(OpCode.JumpIfTrue, loop.Location, start);
                PatchControl(control, code.Count, condition);
            }
            else
            {
                EmitExpression(loop.Condition);
                int end = Add(OpCode.JumpIfFalse, loop.Location);
                EmitStatement(loop.Body);
                Add(OpCode.Jump, loop.Location, start);
                Patch(end, code.Count);
                PatchControl(control, code.Count, start);
            }
        }
        private void EmitFor(BoundFor loop)
        {
            EmitStatement(loop.Initializer);
            int condition = code.Count;
            EmitExpression(loop.Condition);
            int end = Add(OpCode.JumpIfFalse, loop.Location);
            var control = new ControlTarget { IsLoop = true };
            controls.Add(control);
            EmitStatement(loop.Body);
            int iteration = code.Count;
            foreach (BoundExpression item in loop.Iteration) EmitDiscard(item);
            Add(OpCode.Jump, loop.Location, condition);
            Patch(end, code.Count);
            PatchControl(control, code.Count, iteration);
        }
        private void EmitSwitch(BoundSwitch statement)
        {
            EmitExpression(statement.Expression); Store(statement.Temporary, statement.Location);
            var branches = new List<int>();
            foreach (BoundSwitchCase item in statement.Cases)
            {
                if (item.IsDefault) { branches.Add(-1); continue; }
                Load(statement.Temporary, item.Location); Push(item.Label, item.Location); Add(OpCode.Equal, item.Location);
                branches.Add(Add(OpCode.JumpIfTrue, item.Location));
            }
            int otherwise = Add(OpCode.Jump, statement.Location);
            int defaultTarget = -1;
            var control = new ControlTarget();
            controls.Add(control);
            for (int i = 0; i < statement.Cases.Count; i++)
            {
                if (statement.Cases[i].IsDefault) defaultTarget = code.Count;
                else Patch(branches[i], code.Count);
                EmitStatement(statement.Cases[i].Body);
            }
            Patch(otherwise, defaultTarget < 0 ? code.Count : defaultTarget);
            PatchControl(control, code.Count, -1);
        }

        private void EmitDiscard(BoundExpression expression)
        { EmitExpression(expression); if (expression.Type != ScriptType.Void) Add(OpCode.Pop, expression.Location); }

        private void EmitExpression(BoundExpression expression)
        {
            var literal = expression as BoundLiteral;
            if (literal != null) { Push(literal.Value, literal.Location); return; }
            var variable = expression as BoundVariable;
            if (variable != null) { Load(variable.Variable, variable.Location); return; }
            var copy = expression as BoundCopy;
            if (copy != null) { EmitExpression(copy.Expression); return; }
            var conversion = expression as BoundConversion;
            if (conversion != null) { EmitExpression(conversion.Expression); Convert(conversion.Expression.Type, conversion.Type, conversion.Location); return; }
            var unary = expression as BoundUnary;
            if (unary != null) { EmitExpression(unary.Operand); Add(unary.Code, unary.Location); return; }
            var binary = expression as BoundBinary;
            if (binary != null) { EmitBinary(binary); return; }
            var assignment = expression as BoundAssignment;
            if (assignment != null)
            {
                EmitExpression(assignment.Expression); Add(OpCode.Duplicate, assignment.Location); Store(assignment.Variable, assignment.Location); return;
            }
            var compound = expression as BoundCompoundAssignment;
            if (compound != null)
            {
                // AngelScript evaluates the RHS before reading the assignment target.
                EmitExpression(compound.Right); Load(compound.Variable, compound.Location);
                Convert(compound.Variable.Type, compound.OperationType, compound.Location);
                Add(OpCode.Swap, compound.Location); Add(compound.Code, compound.Location);
                Convert(compound.ResultType, compound.Variable.Type, compound.Location);
                Add(OpCode.Duplicate, compound.Location); Store(compound.Variable, compound.Location); return;
            }
            var increment = expression as BoundIncrement;
            if (increment != null) { EmitIncrement(increment); return; }
            var conditional = expression as BoundConditional;
            if (conditional != null)
            {
                EmitExpression(conditional.Condition);
                int otherwise = Add(OpCode.JumpIfFalse, conditional.Location);
                EmitExpression(conditional.Then);
                int end = Add(OpCode.Jump, conditional.Location);
                Patch(otherwise, code.Count); EmitExpression(conditional.Else); Patch(end, code.Count); return;
            }
            var call = expression as BoundCall;
            if (call != null)
            {
                // The original engine evaluates and captures arguments from last to first.
                for (int i = call.Arguments.Count - 1; i >= 0; i--) EmitExpression(call.Arguments[i]);
                OpCode op = call.Function.Kind == FunctionKind.Script ? OpCode.Call : call.Function.Kind == FunctionKind.Host ? OpCode.CallHost : OpCode.Intrinsic;
                Add(op, call.Location, call.Function.Id, call.Arguments.Count, call.Type); return;
            }
            throw new System.InvalidOperationException("Unknown bound expression.");
        }

        private void EmitBinary(BoundBinary binary)
        {
            if (binary.ShortCircuit != ShortCircuit.None)
            {
                EmitExpression(binary.Left); Add(OpCode.Duplicate, binary.Location);
                int end = Add(binary.ShortCircuit == ShortCircuit.And ? OpCode.JumpIfFalse : OpCode.JumpIfTrue, binary.Location);
                Add(OpCode.Pop, binary.Location); EmitExpression(binary.Right); Patch(end, code.Count); return;
            }
            VariableSymbol leftAlias, rightAlias;
            // Numeric LOCAL variable/assignment operands retain references until consumed;
            // globals are captured values. e.g. local i+i++ reads i AFTER the increment,
            // but global g+g++ captures g BEFORE it. Unary + always forces a copy.
            if (TryNumericAlias(binary.Left, out leftAlias))
            {
                EmitAliasEffects(binary.Left);
                if (TryNumericAlias(binary.Right, out rightAlias))
                {
                    EmitAliasEffects(binary.Right); Load(leftAlias, binary.Left.Location); Load(rightAlias, binary.Right.Location);
                }
                else
                {
                    EmitExpression(binary.Right); Load(leftAlias, binary.Left.Location); Add(OpCode.Swap, binary.Location);
                }
            }
            else { EmitExpression(binary.Left); EmitExpression(binary.Right); }
            Add(binary.Code, binary.Location);
        }
        private void EmitAliasEffects(BoundExpression expression)
        { if (!(expression is BoundVariable)) EmitDiscard(expression); }
        private static bool TryNumericAlias(BoundExpression expression, out VariableSymbol variable)
        {
            variable = null;
            if (!ScriptTypes.IsNumeric(expression.Type)) return false;
            var reference = expression as BoundVariable;
            if (reference != null) variable = reference.Variable;
            var assignment = expression as BoundAssignment;
            if (assignment != null) variable = assignment.Variable;
            var compound = expression as BoundCompoundAssignment;
            if (compound != null) variable = compound.Variable;
            return variable != null && !variable.IsGlobal;
        }
        private void EmitIncrement(BoundIncrement increment)
        {
            ScriptType type = ScriptTypes.IsInteger(increment.Type) ? ScriptTypes.PromoteInteger(increment.Type) : increment.Type;
            Load(increment.Variable, increment.Location);
            if (increment.Postfix) Add(OpCode.Duplicate, increment.Location);
            Convert(increment.Type, type, increment.Location);
            Value one;
            NumericOperations.TryConvert(Value.FromInt32(1), type, out one);
            Push(one, increment.Location); Add(increment.Decrement ? OpCode.Subtract : OpCode.Add, increment.Location);
            Convert(type, increment.Type, increment.Location);
            if (!increment.Postfix) Add(OpCode.Duplicate, increment.Location);
            Store(increment.Variable, increment.Location);
        }
    }
}
