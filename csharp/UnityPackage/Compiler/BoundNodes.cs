// Unofficial C# AngelScript subset; not the original AngelCode library.
using System.Collections.Generic;

namespace AngelScript.Burst.Compilation
{
    internal sealed class VariableSymbol
    {
        internal string Name;
        internal ScriptType Type;
        internal bool IsConst;
        internal bool IsGlobal;
        internal int Index;
        internal SourceLocation Location;
        internal bool HasConstantValue;
        internal Value ConstantValue;
        internal Value InitialValue;
        internal GlobalSyntax GlobalSyntax;
        internal int InitializationState;
    }
    internal enum FunctionKind { Script, Host, Intrinsic }
    internal sealed class FunctionSymbol
    {
        internal FunctionSyntax Syntax;
        internal FunctionKind Kind;
        internal int Id;
        internal ScriptType[] ParameterTypes;
        internal ScriptType ReturnType { get { return Syntax.ReturnType.Type; } }
        internal string Name { get { return Syntax.Name; } }
    }
    internal sealed class BoundFunction
    {
        internal FunctionSymbol Symbol;
        internal BoundBlock Body;
        internal List<ScriptType> SlotTypes;
    }
    internal abstract class BoundNode
    {
        internal readonly SourceLocation Location;
        protected BoundNode(SourceLocation location) { Location = location; }
    }
    internal abstract class BoundStatement : BoundNode { protected BoundStatement(SourceLocation location) : base(location) { } }
    internal sealed class BoundBlock : BoundStatement
    {
        internal readonly List<BoundStatement> Statements;
        internal BoundBlock(List<BoundStatement> statements, SourceLocation location) : base(location) { Statements = statements; }
    }
    internal sealed class BoundDeclaration : BoundStatement
    {
        internal readonly VariableSymbol Variable;
        internal readonly BoundExpression Initializer;
        internal BoundDeclaration(VariableSymbol variable, BoundExpression initializer, SourceLocation location) : base(location) { Variable = variable; Initializer = initializer; }
    }
    internal sealed class BoundExpressionStatement : BoundStatement
    {
        internal readonly BoundExpression Expression;
        internal BoundExpressionStatement(BoundExpression expression, SourceLocation location) : base(location) { Expression = expression; }
    }
    internal sealed class BoundIf : BoundStatement
    {
        internal readonly BoundExpression Condition;
        internal readonly BoundStatement Then, Else;
        internal BoundIf(BoundExpression condition, BoundStatement then, BoundStatement otherwise, SourceLocation location) : base(location)
        { Condition = condition; Then = then; Else = otherwise; }
    }
    internal sealed class BoundWhile : BoundStatement
    {
        internal readonly BoundExpression Condition;
        internal readonly BoundStatement Body;
        internal readonly bool IsDoWhile;
        internal BoundWhile(BoundExpression condition, BoundStatement body, bool isDoWhile, SourceLocation location) : base(location)
        { Condition = condition; Body = body; IsDoWhile = isDoWhile; }
    }
    internal sealed class BoundFor : BoundStatement
    {
        internal readonly BoundStatement Initializer;
        internal readonly BoundExpression Condition;
        internal readonly List<BoundExpression> Iteration;
        internal readonly BoundStatement Body;
        internal BoundFor(BoundStatement initializer, BoundExpression condition, List<BoundExpression> iteration, BoundStatement body, SourceLocation location) : base(location)
        { Initializer = initializer; Condition = condition; Iteration = iteration; Body = body; }
    }
    internal sealed class BoundReturn : BoundStatement
    {
        internal readonly BoundExpression Expression;
        internal BoundReturn(BoundExpression expression, SourceLocation location) : base(location) { Expression = expression; }
    }
    internal sealed class BoundControl : BoundStatement
    {
        internal readonly bool IsContinue;
        internal BoundControl(bool isContinue, SourceLocation location) : base(location) { IsContinue = isContinue; }
    }
    internal sealed class BoundSwitch : BoundStatement
    {
        internal readonly BoundExpression Expression;
        internal readonly VariableSymbol Temporary;
        internal readonly List<BoundSwitchCase> Cases;
        internal BoundSwitch(BoundExpression expression, VariableSymbol temporary, List<BoundSwitchCase> cases, SourceLocation location) : base(location)
        { Expression = expression; Temporary = temporary; Cases = cases; }
    }
    internal sealed class BoundSwitchCase
    {
        internal bool IsDefault;
        internal Value Label;
        internal BoundBlock Body;
        internal SourceLocation Location;
    }
    internal abstract class BoundExpression : BoundNode
    {
        internal readonly ScriptType Type;
        internal readonly bool IsConstant;
        protected BoundExpression(ScriptType type, bool constant, SourceLocation location) : base(location) { Type = type; IsConstant = constant; }
    }
    internal sealed class BoundLiteral : BoundExpression
    {
        internal readonly Value Value;
        internal BoundLiteral(Value value, SourceLocation location) : base(value.Type, true, location) { Value = value; }
    }
    internal sealed class BoundVariable : BoundExpression
    {
        internal readonly VariableSymbol Variable;
        internal BoundVariable(VariableSymbol variable, SourceLocation location) : base(variable.Type, false, location) { Variable = variable; }
    }
    internal sealed class BoundConversion : BoundExpression
    {
        internal readonly BoundExpression Expression;
        internal BoundConversion(ScriptType type, BoundExpression expression, SourceLocation location) : base(type, expression.IsConstant, location) { Expression = expression; }
    }
    internal sealed class BoundCopy : BoundExpression
    {
        internal readonly BoundExpression Expression;
        internal BoundCopy(BoundExpression expression, SourceLocation location) : base(expression.Type, expression.IsConstant, location) { Expression = expression; }
    }
    internal sealed class BoundUnary : BoundExpression
    {
        internal readonly OpCode Code;
        internal readonly BoundExpression Operand;
        internal BoundUnary(OpCode code, BoundExpression operand, SourceLocation location) : base(operand.Type, operand.IsConstant, location) { Code = code; Operand = operand; }
    }
    internal enum ShortCircuit { None, And, Or }
    internal sealed class BoundBinary : BoundExpression
    {
        internal readonly OpCode Code;
        internal readonly ShortCircuit ShortCircuit;
        internal readonly BoundExpression Left, Right;
        internal BoundBinary(OpCode code, ShortCircuit shortCircuit, BoundExpression left, BoundExpression right, ScriptType type, SourceLocation location) : base(type, left.IsConstant && right.IsConstant, location)
        { Code = code; ShortCircuit = shortCircuit; Left = left; Right = right; }
    }
    internal sealed class BoundAssignment : BoundExpression
    {
        internal readonly VariableSymbol Variable;
        internal readonly BoundExpression Expression;
        internal BoundAssignment(VariableSymbol variable, BoundExpression expression, SourceLocation location) : base(variable.Type, false, location) { Variable = variable; Expression = expression; }
    }
    internal sealed class BoundCompoundAssignment : BoundExpression
    {
        internal readonly VariableSymbol Variable;
        internal readonly BoundExpression Right;
        internal readonly ScriptType OperationType;
        internal readonly ScriptType ResultType;
        internal readonly OpCode Code;
        internal BoundCompoundAssignment(VariableSymbol variable, BoundBinary operation, SourceLocation location) : base(variable.Type, false, location)
        { Variable = variable; Right = operation.Right; OperationType = operation.Left.Type; ResultType = operation.Type; Code = operation.Code; }
    }
    internal sealed class BoundIncrement : BoundExpression
    {
        internal readonly VariableSymbol Variable;
        internal readonly bool Postfix;
        internal readonly bool Decrement;
        internal BoundIncrement(VariableSymbol variable, bool postfix, bool decrement, SourceLocation location) : base(variable.Type, false, location)
        { Variable = variable; Postfix = postfix; Decrement = decrement; }
    }
    internal sealed class BoundConditional : BoundExpression
    {
        internal readonly BoundExpression Condition, Then, Else;
        internal BoundConditional(BoundExpression condition, BoundExpression then, BoundExpression otherwise, ScriptType type, SourceLocation location) : base(type, condition.IsConstant && then.IsConstant && otherwise.IsConstant, location)
        { Condition = condition; Then = then; Else = otherwise; }
    }
    internal sealed class BoundCall : BoundExpression
    {
        internal readonly FunctionSymbol Function;
        internal readonly List<BoundExpression> Arguments;
        internal BoundCall(FunctionSymbol function, List<BoundExpression> arguments, SourceLocation location) : base(function.ReturnType, false, location)
        { Function = function; Arguments = arguments; }
    }
}
