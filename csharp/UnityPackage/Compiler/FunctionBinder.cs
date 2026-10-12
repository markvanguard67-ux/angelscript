// Unofficial C# AngelScript subset; not the original AngelCode library.
using System.Collections.Generic;

namespace AngelScript.Burst.Compilation
{
    internal sealed class FunctionBinder
    {
        private readonly ModuleBinder module;
        private readonly FunctionSymbol function;
        private readonly List<Dictionary<string, VariableSymbol>> scopes = new List<Dictionary<string, VariableSymbol>>();
        private readonly List<ScriptType> slotTypes = new List<ScriptType>();
        private int loopDepth;
        private int breakDepth;
        private int expressionDepth;

        internal FunctionBinder(ModuleBinder module, FunctionSymbol function)
        { this.module = module; this.function = function; PushScope(); }

        internal BoundFunction BindFunction()
        {
            foreach (ParameterSyntax parameter in function.Syntax.Parameters)
                Declare(parameter.Name, parameter.Type.Type, parameter.Type.IsConst, parameter.Location);
            BoundBlock body = BindBlock(function.Syntax.Body, false);
            if (function.ReturnType != ScriptType.Void && (FlowAnalyzer.Analyze(body) & Flow.FallThrough) != 0)
                throw new CompilationFailure(function.Syntax.Location, DiagnosticCode.MissingReturn, "Not every reachable path returns a value from '" + function.Name + "'.");
            return new BoundFunction { Symbol = function, Body = body, SlotTypes = slotTypes };
        }

        private void PushScope() { scopes.Add(new Dictionary<string, VariableSymbol>()); }
        private void PopScope() { scopes.RemoveAt(scopes.Count - 1); }
        private VariableSymbol Declare(string name, ScriptType type, bool isConst, SourceLocation location)
        {
            Dictionary<string, VariableSymbol> scope = scopes[scopes.Count - 1];
            if (scope.ContainsKey(name)) throw ModuleBinder.Duplicate(location, name);
            VariableSymbol symbol = Allocate(name, type, isConst, location);
            scope.Add(name, symbol);
            return symbol;
        }
        private VariableSymbol Allocate(string name, ScriptType type, bool isConst, SourceLocation location)
        {
            if (slotTypes.Count >= module.Options.MaxLocalsPerFunction)
                throw new CompilationFailure(location, DiagnosticCode.ResourceLimit, "Local/temporary count exceeds MaxLocalsPerFunction.");
            var symbol = new VariableSymbol { Name = name, Type = type, IsConst = isConst, Index = slotTypes.Count, Location = location };
            slotTypes.Add(type);
            return symbol;
        }
        private VariableSymbol FindVariable(string name)
        {
            for (int i = scopes.Count - 1; i >= 0; i--)
            {
                VariableSymbol symbol;
                if (scopes[i].TryGetValue(name, out symbol)) return symbol;
            }
            return module.FindGlobal(name);
        }

        private BoundBlock BindBlock(BlockSyntax block, bool newScope)
        {
            if (newScope) PushScope();
            try
            {
                var statements = new List<BoundStatement>();
                foreach (StatementSyntax statement in block.Statements) statements.Add(BindStatement(statement));
                return new BoundBlock(statements, block.Location);
            }
            finally { if (newScope) PopScope(); }
        }
        private BoundStatement BindScoped(StatementSyntax statement)
        {
            if (statement is VariableStatementSyntax)
                throw new CompilationFailure(statement.Location, DiagnosticCode.InvalidOperation, "A controlled variable declaration must be inside a braced block.");
            PushScope();
            try { return BindStatement(statement); }
            finally { PopScope(); }
        }

        private BoundStatement BindStatement(StatementSyntax syntax)
        {
            var block = syntax as BlockSyntax;
            if (block != null) return BindBlock(block, true);
            var declaration = syntax as VariableStatementSyntax;
            if (declaration != null)
            {
                var declarations = new List<BoundStatement>();
                foreach (DeclaratorSyntax variable in declaration.Variables)
                {
                    if (declaration.Type.IsConst && variable.Initializer == null)
                        throw new CompilationFailure(variable.Location, DiagnosticCode.InvalidConstant, "A const variable requires an initializer.");
                    VariableSymbol symbol = Declare(variable.Name, declaration.Type.Type, declaration.Type.IsConst, variable.Location);
                    BoundExpression initializer = variable.Initializer == null ? new BoundLiteral(Value.Zero(symbol.Type), variable.Location) :
                        Convert(BindExpression(variable.Initializer), symbol.Type, false, variable.Initializer.Location);
                    Value value;
                    VmError error;
                    if (symbol.IsConst && initializer.IsConstant && ConstantFolder.TryEvaluate(initializer, out value, out error))
                    { symbol.HasConstantValue = true; symbol.ConstantValue = value; }
                    declarations.Add(new BoundDeclaration(symbol, initializer, variable.Location));
                }
                return new BoundBlock(declarations, declaration.Location);
            }
            var expression = syntax as ExpressionStatementSyntax;
            if (expression != null) return new BoundExpressionStatement(expression.Expression == null ? null : BindExpression(expression.Expression), expression.Location);
            var condition = syntax as IfSyntax;
            if (condition != null)
                return new BoundIf(RequireBool(BindExpression(condition.Condition), condition.Condition.Location), BindScoped(condition.Then),
                    condition.Else == null ? null : BindScoped(condition.Else), condition.Location);
            var loop = syntax as WhileSyntax;
            if (loop != null)
            {
                BoundExpression test = RequireBool(BindExpression(loop.Condition), loop.Condition.Location);
                loopDepth++; breakDepth++;
                try { return new BoundWhile(test, BindScoped(loop.Body), loop.IsDoWhile, loop.Location); }
                finally { loopDepth--; breakDepth--; }
            }
            var forLoop = syntax as ForSyntax;
            if (forLoop != null)
            {
                PushScope();
                try
                {
                    BoundStatement initializer = BindStatement(forLoop.Initializer);
                    BoundExpression test = forLoop.Condition == null ? new BoundLiteral(Value.FromBool(true), forLoop.Location) :
                        RequireBool(BindExpression(forLoop.Condition), forLoop.Condition.Location);
                    var iteration = new List<BoundExpression>();
                    foreach (ExpressionSyntax item in forLoop.Iteration) iteration.Add(BindExpression(item));
                    loopDepth++; breakDepth++;
                    try { return new BoundFor(initializer, test, iteration, BindScoped(forLoop.Body), forLoop.Location); }
                    finally { loopDepth--; breakDepth--; }
                }
                finally { PopScope(); }
            }
            var returnStatement = syntax as ReturnSyntax;
            if (returnStatement != null)
            {
                if (returnStatement.Expression == null)
                {
                    if (function.ReturnType != ScriptType.Void) throw new CompilationFailure(returnStatement.Location, DiagnosticCode.TypeMismatch, "A non-void function must return a value.");
                    return new BoundReturn(null, returnStatement.Location);
                }
                if (function.ReturnType == ScriptType.Void)
                    throw new CompilationFailure(returnStatement.Location, DiagnosticCode.TypeMismatch, "A void function cannot return an expression; call it in a separate statement, then return.");
                return new BoundReturn(Convert(BindExpression(returnStatement.Expression), function.ReturnType, false, returnStatement.Location), returnStatement.Location);
            }
            var control = syntax as ControlSyntax;
            if (control != null)
            {
                if (control.IsContinue ? loopDepth == 0 : breakDepth == 0)
                    throw new CompilationFailure(control.Location, DiagnosticCode.InvalidOperation, control.IsContinue ? "continue requires an enclosing loop." : "break requires an enclosing loop or switch.");
                return new BoundControl(control.IsContinue, control.Location);
            }
            var switchStatement = syntax as SwitchSyntax;
            if (switchStatement != null) return BindSwitch(switchStatement);
            throw new CompilationFailure(syntax.Location, DiagnosticCode.UnsupportedFeature, "Unsupported statement.");
        }

        private BoundSwitch BindSwitch(SwitchSyntax syntax)
        {
            BoundExpression expression = BindExpression(syntax.Expression);
            if (!ScriptTypes.IsInteger(expression.Type)) throw new CompilationFailure(syntax.Expression.Location, DiagnosticCode.TypeMismatch, "switch requires an integer expression.");
            // Native AngelScript converts every switch expression/case label to int32.
            // In particular, uint64(4294967296) must match case 0, not a 64-bit key.
            expression = Convert(expression, ScriptType.Int32, false, expression.Location);
            VariableSymbol temporary = Allocate("\0switch" + slotTypes.Count, expression.Type, false, syntax.Location);
            var cases = new List<BoundSwitchCase>();
            var labels = new HashSet<ulong>();
            bool hasDefault = false;
            PushScope(); breakDepth++;
            try
            {
                foreach (SwitchCaseSyntax item in syntax.Cases)
                {
                    if (hasDefault && item.Label != null)
                        throw new CompilationFailure(item.Location, DiagnosticCode.InvalidOperation, "The default case must be last, as in AngelScript.");
                    Value label = default(Value);
                    if (item.Label == null)
                    {
                        if (hasDefault) throw new CompilationFailure(item.Location, DiagnosticCode.DuplicateName, "Duplicate default label.");
                        hasDefault = true;
                    }
                    else
                    {
                        BoundExpression boundLabel = BindExpression(item.Label);
                        if (!ScriptTypes.IsInteger(boundLabel.Type)) throw new CompilationFailure(item.Location, DiagnosticCode.TypeMismatch, "case requires a constant integer.");
                        boundLabel = Convert(boundLabel, expression.Type, false, item.Location);
                        VmError error;
                        if (!ConstantFolder.TryEvaluate(boundLabel, out label, out error)) throw new CompilationFailure(item.Location, DiagnosticCode.InvalidConstant, "case requires a valid constant integer.");
                        if (!labels.Add(label.Bits)) throw new CompilationFailure(item.Location, DiagnosticCode.DuplicateName, "Duplicate case value after conversion.");
                    }
                    foreach (StatementSyntax statement in item.Statements)
                        if (statement is VariableStatementSyntax)
                            throw new CompilationFailure(statement.Location, DiagnosticCode.InvalidOperation, "Variables in switch cases must be declared inside a braced statement block.");
                    cases.Add(new BoundSwitchCase
                    {
                        IsDefault = item.Label == null, Label = label,
                        Body = BindBlock(new BlockSyntax(item.Statements, item.Location), false), Location = item.Location
                    });
                }
            }
            finally { breakDepth--; PopScope(); }
            return new BoundSwitch(expression, temporary, cases, syntax.Location);
        }

        internal BoundExpression BindExpression(ExpressionSyntax syntax)
        {
            if (++expressionDepth > module.Options.MaxSyntaxDepth)
                throw new CompilationFailure(syntax.Location, DiagnosticCode.ResourceLimit, "Expression tree exceeds MaxSyntaxDepth.");
            try
            {
                var literal = syntax as LiteralSyntax;
                if (literal != null) return new BoundLiteral(LiteralParser.Parse(literal), literal.Location);
                var name = syntax as NameSyntax;
                if (name != null)
                {
                    VariableSymbol symbol = FindVariable(name.Name);
                    if (symbol == null) throw new CompilationFailure(name.Location, DiagnosticCode.UnknownName, "Unknown variable '" + name.Name + "'.");
                    if (symbol.IsGlobal && symbol.IsConst) return new BoundLiteral(module.ResolveConstant(symbol), name.Location);
                    if (symbol.HasConstantValue) return new BoundLiteral(symbol.ConstantValue, name.Location);
                    return new BoundVariable(symbol, name.Location);
                }
                var unary = syntax as UnarySyntax;
                if (unary != null) return BindUnary(unary);
                var binary = syntax as BinarySyntax;
                if (binary != null) return BindBinary(binary);
                var cast = syntax as CastSyntax;
                if (cast != null)
                {
                    if (cast.Type == ScriptType.Void) throw new CompilationFailure(cast.Location, DiagnosticCode.TypeMismatch, "void cannot be the target of a cast expression.");
                    return Convert(BindExpression(cast.Expression), cast.Type, true, cast.Location);
                }
                var conditional = syntax as ConditionalSyntax;
                if (conditional != null)
                {
                    BoundExpression test = RequireBool(BindExpression(conditional.Condition), conditional.Condition.Location);
                    BoundExpression then = BindExpression(conditional.Then), otherwise = BindExpression(conditional.Else);
                    ScriptType type;
                    if (then.Type == otherwise.Type) type = then.Type;
                    else if (ScriptTypes.IsNumeric(then.Type) && ScriptTypes.IsNumeric(otherwise.Type)) type = CommonNumericType(then, otherwise);
                    else throw new CompilationFailure(conditional.Location, DiagnosticCode.TypeMismatch, "Both conditional branches must have compatible types.");
                    return ConstantFolder.Fold(new BoundConditional(test, Convert(then, type, false, then.Location), Convert(otherwise, type, false, otherwise.Location), type, conditional.Location));
                }
                var call = syntax as CallSyntax;
                if (call != null) return BindCall(call);
                throw new CompilationFailure(syntax.Location, DiagnosticCode.UnsupportedFeature, "Unsupported expression.");
            }
            finally { expressionDepth--; }
        }

        private VariableSymbol WritableVariable(ExpressionSyntax syntax)
        {
            var name = syntax as NameSyntax;
            if (name == null) throw new CompilationFailure(syntax.Location, DiagnosticCode.InvalidOperation, "Assignment/increment requires a variable lvalue.");
            VariableSymbol symbol = FindVariable(name.Name);
            if (symbol == null) throw new CompilationFailure(name.Location, DiagnosticCode.UnknownName, "Unknown variable '" + name.Name + "'.");
            if (symbol.IsConst) throw new CompilationFailure(name.Location, DiagnosticCode.InvalidOperation, "Cannot modify const variable '" + name.Name + "'.");
            return symbol;
        }

        private BoundExpression BindUnary(UnarySyntax syntax)
        {
            if (syntax.Operator == "++" || syntax.Operator == "--")
            {
                VariableSymbol symbol = WritableVariable(syntax.Operand);
                if (!ScriptTypes.IsNumeric(symbol.Type)) throw new CompilationFailure(syntax.Location, DiagnosticCode.InvalidOperation, "Increment/decrement requires a numeric variable.");
                return new BoundIncrement(symbol, syntax.Postfix, syntax.Operator == "--", syntax.Location);
            }
            BoundExpression operand = BindExpression(syntax.Operand);
            if (syntax.Operator == "!" || syntax.Operator == "not")
                return ConstantFolder.Fold(new BoundUnary(OpCode.LogicalNot, RequireBool(operand, syntax.Location), syntax.Location));
            if (syntax.Operator == "~")
            {
                if (!ScriptTypes.IsInteger(operand.Type)) throw new CompilationFailure(syntax.Location, DiagnosticCode.InvalidOperation, "Bitwise complement requires an integer.");
                ScriptType target = UnsignedType(operand.Type);
                ScriptType operationType = ScriptTypes.BitWidth(target) == 64 ? ScriptType.UInt64 : ScriptType.UInt32;
                BoundExpression operation = new BoundUnary(OpCode.BitNot, Convert(operand, operationType, true, operand.Location), syntax.Location);
                return Convert(ConstantFolder.Fold(operation), target, true, syntax.Location);
            }
            if (!ScriptTypes.IsNumeric(operand.Type)) throw new CompilationFailure(syntax.Location, DiagnosticCode.InvalidOperation, "Unary +/- requires a numeric operand.");
            // Like AngelScript, unary +/- converts unsigned operands to their same-width signed type.
            ScriptType signed = ScriptTypes.IsInteger(operand.Type) ? SignedType(operand.Type) : operand.Type;
            if (syntax.Operator == "+") return ConstantFolder.Fold(new BoundCopy(Convert(operand, signed, true, syntax.Location), syntax.Location));
            ScriptType promoted = ScriptTypes.IsInteger(signed) ? ScriptTypes.PromoteInteger(signed) : signed;
            BoundExpression negation = new BoundUnary(OpCode.Negate, Convert(operand, promoted, true, operand.Location), syntax.Location);
            return Convert(ConstantFolder.Fold(negation), signed, true, syntax.Location);
        }

        private BoundExpression BindBinary(BinarySyntax syntax)
        {
            if (IsAssignment(syntax.Operator))
            {
                VariableSymbol variable = WritableVariable(syntax.Left);
                BoundExpression right = BindExpression(syntax.Right);
                if (syntax.Operator == "=") return new BoundAssignment(variable, Convert(right, variable.Type, false, syntax.Location), syntax.Location);
                string op = syntax.Operator.Substring(0, syntax.Operator.Length - 1);
                BoundBinary operation = BindOperation(op, new BoundVariable(variable, syntax.Left.Location), right, syntax.Location);
                return new BoundCompoundAssignment(variable, operation, syntax.Location);
            }
            return ConstantFolder.Fold(BindOperation(syntax.Operator, BindExpression(syntax.Left), BindExpression(syntax.Right), syntax.Location));
        }

        private BoundBinary BindOperation(string op, BoundExpression left, BoundExpression right, SourceLocation location)
        {
            if (op == "and" || op == "&&" || op == "or" || op == "||")
            {
                bool and = op == "and" || op == "&&";
                return new BoundBinary(OpCode.Nop, and ? ShortCircuit.And : ShortCircuit.Or,
                    RequireBool(left, location), RequireBool(right, location), ScriptType.Bool, location);
            }
            if (op == "xor" || op == "^^")
                return new BoundBinary(OpCode.LogicalXor, ShortCircuit.None, RequireBool(left, location), RequireBool(right, location), ScriptType.Bool, location);
            bool compare = op == "==" || op == "!=" || op == "<" || op == "<=" || op == ">" || op == ">=";
            if (compare && left.Type == ScriptType.Bool && right.Type == ScriptType.Bool)
            {
                if (op != "==" && op != "!=") throw new CompilationFailure(location, DiagnosticCode.InvalidOperation, "bool only supports equality comparisons.");
                return new BoundBinary(BinaryCode(op), ShortCircuit.None, left, right, ScriptType.Bool, location);
            }
            bool bits = op == "&" || op == "|" || op == "^";
            bool shift = op == "<<" || op == ">>" || op == ">>>";
            if (bits || shift)
            {
                if (!ScriptTypes.IsInteger(left.Type) || (shift ? !ScriptTypes.IsNumeric(right.Type) : !ScriptTypes.IsInteger(right.Type)))
                    throw new CompilationFailure(location, DiagnosticCode.InvalidOperation, shift ? "A shift requires an integer left operand and a numeric count." : "Bitwise operators require integer operands.");
                ScriptType type;
                if (shift) type = ScriptTypes.PromoteInteger(left.Type);
                else
                {
                    bool wide = ScriptTypes.BitWidth(left.Type) == 64 || ScriptTypes.BitWidth(right.Type) == 64;
                    type = ScriptTypes.IsSigned(left.Type) ? (wide ? ScriptType.Int64 : ScriptType.Int32) : (wide ? ScriptType.UInt64 : ScriptType.UInt32);
                }
                return new BoundBinary(BinaryCode(op), ShortCircuit.None, Convert(left, type, false, location),
                    Convert(right, shift ? ScriptType.UInt32 : type, false, location), type, location);
            }
            if (!ScriptTypes.IsNumeric(left.Type) || !ScriptTypes.IsNumeric(right.Type))
                throw new CompilationFailure(location, DiagnosticCode.InvalidOperation, "Operator '" + op + "' requires numeric operands.");
            // The original compiler has a distinct double**integer path: the exponent
            // is converted to int32, not to double. Preserve narrowing and odd/even parity.
            if (op == "**" && left.Type == ScriptType.Double && ScriptTypes.IsInteger(right.Type))
                return new BoundBinary(OpCode.Power, ShortCircuit.None, left,
                    Convert(right, ScriptType.Int32, false, location), ScriptType.Double, location);
            ScriptType common = CommonNumericType(left, right);
            left = Convert(left, common, false, location); right = Convert(right, common, false, location);
            if (op == "/" || op == "%")
            {
                Value constant;
                VmError error;
                if (right.IsConstant && ConstantFolder.TryEvaluate(right, out constant, out error) && NumericOperations.ReadReal(constant) == 0.0)
                    throw new CompilationFailure(location, DiagnosticCode.InvalidConstant, "Division or modulo by a constant zero.");
            }
            return new BoundBinary(BinaryCode(op), ShortCircuit.None, left, right, compare ? ScriptType.Bool : common, location);
        }

        internal BoundExpression Convert(BoundExpression expression, ScriptType target, bool explicitCast, SourceLocation location)
        {
            if (expression.Type == target) return expression;
            bool numeric = ScriptTypes.IsNumeric(expression.Type) && ScriptTypes.IsNumeric(target);
            if (!numeric)
                throw new CompilationFailure(location, DiagnosticCode.TypeMismatch, "Cannot " + (explicitCast ? "explicitly" : "implicitly") + " convert " + TypeNames.Get(expression.Type) + " to " + TypeNames.Get(target) + ".");
            return ConstantFolder.Fold(new BoundConversion(target, expression, location));
        }
        private static BoundExpression RequireBool(BoundExpression expression, SourceLocation location)
        {
            if (expression.Type != ScriptType.Bool) throw new CompilationFailure(location, DiagnosticCode.TypeMismatch, "A condition/logical operand must have type bool; use an explicit comparison.");
            return expression;
        }

        private BoundExpression BindCall(CallSyntax syntax)
        {
            if (FindVariable(syntax.Name) != null) throw new CompilationFailure(syntax.Location, DiagnosticCode.InvalidOperation, "Variable '" + syntax.Name + "' is not callable.");
            List<FunctionSymbol> candidates = module.FindFunctions(syntax.Name);
            if (candidates == null) throw new CompilationFailure(syntax.Location, DiagnosticCode.UnknownName, "Unknown function '" + syntax.Name + "'.");
            var arguments = new List<BoundExpression>();
            foreach (ExpressionSyntax argument in syntax.Arguments) arguments.Add(BindExpression(argument));
            int bestCost = int.MaxValue;
            FunctionSymbol best = null;
            bool ambiguous = false;
            foreach (FunctionSymbol candidate in candidates)
            {
                if (candidate.ParameterTypes.Length != arguments.Count) continue;
                int total = 0;
                for (int i = 0; i < arguments.Count; i++)
                {
                    int cost = ConversionCost(arguments[i], candidate.ParameterTypes[i]);
                    if (cost < 0) { total = int.MaxValue; break; }
                    total += cost;
                }
                if (total < bestCost) { best = candidate; bestCost = total; ambiguous = false; }
                else if (total == bestCost && total != int.MaxValue) ambiguous = true;
            }
            if (best == null) throw new CompilationFailure(syntax.Location, DiagnosticCode.TypeMismatch, "No overload of '" + syntax.Name + "' accepts these arguments.");
            if (ambiguous) throw new CompilationFailure(syntax.Location, DiagnosticCode.AmbiguousCall, "Ambiguous numeric overload of '" + syntax.Name + "'; use explicit casts.");
            for (int i = 0; i < arguments.Count; i++) arguments[i] = Convert(arguments[i], best.ParameterTypes[i], false, arguments[i].Location);
            return new BoundCall(best, arguments, syntax.Location);
        }

        private static int ConversionCost(BoundExpression expression, ScriptType target)
        {
            ScriptType source = expression.Type;
            if (source == target) return 0;
            if (!ScriptTypes.IsNumeric(source) || !ScriptTypes.IsNumeric(target)) return -1;
            bool si = ScriptTypes.IsInteger(source), ti = ScriptTypes.IsInteger(target);
            if (si && ti)
            {
                int sizeCost = ScriptTypes.BitWidth(target) > ScriptTypes.BitWidth(source) ? 1 : ScriptTypes.BitWidth(target) < ScriptTypes.BitWidth(source) ? 4 : 0;
                return sizeCost + (ScriptTypes.IsSigned(source) != ScriptTypes.IsSigned(target) ? 2 : 0);
            }
            if (!si && !ti) return target == ScriptType.Double ? 1 : 4;
            if (si) return target == ScriptType.Double ? 3 : 4;
            return 6;
        }

        private static ScriptType CommonNumericType(BoundExpression left, BoundExpression right)
        {
            if (left.Type == ScriptType.Double || right.Type == ScriptType.Double)
            {
                // AngelScript narrows a double literal when paired with a non-constant float expression.
                if ((left.Type == ScriptType.Double && left.IsConstant && right.Type == ScriptType.Float && !right.IsConstant) ||
                    (right.Type == ScriptType.Double && right.IsConstant && left.Type == ScriptType.Float && !left.IsConstant)) return ScriptType.Float;
                return ScriptType.Double;
            }
            if (left.Type == ScriptType.Float || right.Type == ScriptType.Float) return ScriptType.Float;
            bool wide = ScriptTypes.BitWidth(left.Type) == 64 || ScriptTypes.BitWidth(right.Type) == 64;
            // AngelScript gives a signed non-constant operand priority, unlike C#'s usual promotions.
            bool signedVariable = (ScriptTypes.IsSigned(left.Type) && !left.IsConstant) || (ScriptTypes.IsSigned(right.Type) && !right.IsConstant);
            bool unsigned = !ScriptTypes.IsSigned(left.Type) || !ScriptTypes.IsSigned(right.Type);
            return signedVariable || !unsigned ? (wide ? ScriptType.Int64 : ScriptType.Int32) : (wide ? ScriptType.UInt64 : ScriptType.UInt32);
        }
        private static ScriptType SignedType(ScriptType type)
        {
            switch (type)
            {
                case ScriptType.UInt8: return ScriptType.Int8;
                case ScriptType.UInt16: return ScriptType.Int16;
                case ScriptType.UInt32: return ScriptType.Int32;
                case ScriptType.UInt64: return ScriptType.Int64;
                default: return type;
            }
        }
        private static ScriptType UnsignedType(ScriptType type)
        {
            switch (type)
            {
                case ScriptType.Int8: return ScriptType.UInt8;
                case ScriptType.Int16: return ScriptType.UInt16;
                case ScriptType.Int32: return ScriptType.UInt32;
                case ScriptType.Int64: return ScriptType.UInt64;
                default: return type;
            }
        }
        private static bool IsAssignment(string op)
        { return op == "=" || op == "+=" || op == "-=" || op == "*=" || op == "/=" || op == "%=" || op == "**=" || op == "&=" || op == "|=" || op == "^=" || op == "<<=" || op == ">>=" || op == ">>>="; }
        private static OpCode BinaryCode(string op)
        {
            switch (op)
            {
                case "+": return OpCode.Add;
                case "-": return OpCode.Subtract;
                case "*": return OpCode.Multiply;
                case "/": return OpCode.Divide;
                case "%": return OpCode.Modulo;
                case "**": return OpCode.Power;
                case "&": return OpCode.BitAnd;
                case "|": return OpCode.BitOr;
                case "^": return OpCode.BitXor;
                case "<<": return OpCode.ShiftLeft;
                case ">>": return OpCode.ShiftRightLogical;
                case ">>>": return OpCode.ShiftRightArithmetic;
                case "==": return OpCode.Equal;
                case "!=": return OpCode.NotEqual;
                case "<": return OpCode.Less;
                case "<=": return OpCode.LessEqual;
                case ">": return OpCode.Greater;
                case ">=": return OpCode.GreaterEqual;
                default: throw new System.InvalidOperationException("Unknown bound operator: " + op);
            }
        }
    }
}
