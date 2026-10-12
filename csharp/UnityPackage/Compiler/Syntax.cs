// Unofficial C# AngelScript subset; not the original AngelCode library.
using System.Collections.Generic;

namespace AngelScript.Burst.Compilation
{
    internal struct TypeSyntax
    {
        internal ScriptType Type;
        internal bool IsConst;
        internal SourceLocation Location;
    }
    internal abstract class SyntaxNode
    {
        internal readonly SourceLocation Location;
        protected SyntaxNode(SourceLocation location) { Location = location; }
    }
    internal sealed class ModuleSyntax
    {
        internal readonly List<FunctionSyntax> Functions = new List<FunctionSyntax>();
        internal readonly List<GlobalSyntax> Globals = new List<GlobalSyntax>();
    }
    internal sealed class ParameterSyntax : SyntaxNode
    {
        internal readonly TypeSyntax Type;
        internal readonly string Name;
        internal ParameterSyntax(TypeSyntax type, string name, SourceLocation location) : base(location) { Type = type; Name = name; }
    }
    internal sealed class FunctionSyntax : SyntaxNode
    {
        internal readonly TypeSyntax ReturnType;
        internal readonly string Name;
        internal readonly List<ParameterSyntax> Parameters;
        internal readonly BlockSyntax Body;
        internal FunctionSyntax(TypeSyntax returnType, string name, List<ParameterSyntax> parameters, BlockSyntax body, SourceLocation location) : base(location)
        { ReturnType = returnType; Name = name; Parameters = parameters; Body = body; }
    }
    internal sealed class DeclaratorSyntax : SyntaxNode
    {
        internal readonly string Name;
        internal readonly ExpressionSyntax Initializer;
        internal DeclaratorSyntax(string name, ExpressionSyntax initializer, SourceLocation location) : base(location) { Name = name; Initializer = initializer; }
    }
    internal sealed class GlobalSyntax : SyntaxNode
    {
        internal readonly TypeSyntax Type;
        internal readonly DeclaratorSyntax Variable;
        internal GlobalSyntax(TypeSyntax type, DeclaratorSyntax variable) : base(variable.Location) { Type = type; Variable = variable; }
    }
    internal abstract class StatementSyntax : SyntaxNode { protected StatementSyntax(SourceLocation location) : base(location) { } }
    internal sealed class BlockSyntax : StatementSyntax
    {
        internal readonly List<StatementSyntax> Statements;
        internal BlockSyntax(List<StatementSyntax> statements, SourceLocation location) : base(location) { Statements = statements; }
    }
    internal sealed class VariableStatementSyntax : StatementSyntax
    {
        internal readonly TypeSyntax Type;
        internal readonly List<DeclaratorSyntax> Variables;
        internal VariableStatementSyntax(TypeSyntax type, List<DeclaratorSyntax> variables) : base(type.Location) { Type = type; Variables = variables; }
    }
    internal sealed class ExpressionStatementSyntax : StatementSyntax
    {
        internal readonly ExpressionSyntax Expression;
        internal ExpressionStatementSyntax(ExpressionSyntax expression, SourceLocation location) : base(location) { Expression = expression; }
    }
    internal sealed class IfSyntax : StatementSyntax
    {
        internal readonly ExpressionSyntax Condition;
        internal readonly StatementSyntax Then, Else;
        internal IfSyntax(ExpressionSyntax condition, StatementSyntax then, StatementSyntax otherwise, SourceLocation location) : base(location)
        { Condition = condition; Then = then; Else = otherwise; }
    }
    internal sealed class WhileSyntax : StatementSyntax
    {
        internal readonly ExpressionSyntax Condition;
        internal readonly StatementSyntax Body;
        internal readonly bool IsDoWhile;
        internal WhileSyntax(ExpressionSyntax condition, StatementSyntax body, bool isDoWhile, SourceLocation location) : base(location)
        { Condition = condition; Body = body; IsDoWhile = isDoWhile; }
    }
    internal sealed class ForSyntax : StatementSyntax
    {
        internal readonly StatementSyntax Initializer;
        internal readonly ExpressionSyntax Condition;
        internal readonly List<ExpressionSyntax> Iteration;
        internal readonly StatementSyntax Body;
        internal ForSyntax(StatementSyntax initializer, ExpressionSyntax condition, List<ExpressionSyntax> iteration, StatementSyntax body, SourceLocation location) : base(location)
        { Initializer = initializer; Condition = condition; Iteration = iteration; Body = body; }
    }
    internal sealed class ReturnSyntax : StatementSyntax
    {
        internal readonly ExpressionSyntax Expression;
        internal ReturnSyntax(ExpressionSyntax expression, SourceLocation location) : base(location) { Expression = expression; }
    }
    internal sealed class ControlSyntax : StatementSyntax
    {
        internal readonly bool IsContinue;
        internal ControlSyntax(bool isContinue, SourceLocation location) : base(location) { IsContinue = isContinue; }
    }
    internal sealed class SwitchSyntax : StatementSyntax
    {
        internal readonly ExpressionSyntax Expression;
        internal readonly List<SwitchCaseSyntax> Cases;
        internal SwitchSyntax(ExpressionSyntax expression, List<SwitchCaseSyntax> cases, SourceLocation location) : base(location) { Expression = expression; Cases = cases; }
    }
    internal sealed class SwitchCaseSyntax : SyntaxNode
    {
        internal readonly ExpressionSyntax Label; // null means default
        internal readonly List<StatementSyntax> Statements;
        internal SwitchCaseSyntax(ExpressionSyntax label, List<StatementSyntax> statements, SourceLocation location) : base(location) { Label = label; Statements = statements; }
    }
    internal abstract class ExpressionSyntax : SyntaxNode { protected ExpressionSyntax(SourceLocation location) : base(location) { } }
    internal sealed class LiteralSyntax : ExpressionSyntax
    {
        internal readonly string Text;
        internal LiteralSyntax(string text, SourceLocation location) : base(location) { Text = text; }
    }
    internal sealed class NameSyntax : ExpressionSyntax
    {
        internal readonly string Name;
        internal NameSyntax(string name, SourceLocation location) : base(location) { Name = name; }
    }
    internal sealed class UnarySyntax : ExpressionSyntax
    {
        internal readonly string Operator;
        internal readonly ExpressionSyntax Operand;
        internal readonly bool Postfix;
        internal UnarySyntax(string op, ExpressionSyntax operand, bool postfix, SourceLocation location) : base(location) { Operator = op; Operand = operand; Postfix = postfix; }
    }
    internal sealed class BinarySyntax : ExpressionSyntax
    {
        internal readonly string Operator;
        internal readonly ExpressionSyntax Left, Right;
        internal BinarySyntax(string op, ExpressionSyntax left, ExpressionSyntax right, SourceLocation location) : base(location) { Operator = op; Left = left; Right = right; }
    }
    internal sealed class ConditionalSyntax : ExpressionSyntax
    {
        internal readonly ExpressionSyntax Condition, Then, Else;
        internal ConditionalSyntax(ExpressionSyntax condition, ExpressionSyntax then, ExpressionSyntax otherwise, SourceLocation location) : base(location)
        { Condition = condition; Then = then; Else = otherwise; }
    }
    internal sealed class CallSyntax : ExpressionSyntax
    {
        internal readonly string Name;
        internal readonly List<ExpressionSyntax> Arguments;
        internal CallSyntax(string name, List<ExpressionSyntax> arguments, SourceLocation location) : base(location) { Name = name; Arguments = arguments; }
    }
    internal sealed class CastSyntax : ExpressionSyntax
    {
        internal readonly ScriptType Type;
        internal readonly ExpressionSyntax Expression;
        internal CastSyntax(ScriptType type, ExpressionSyntax expression, SourceLocation location) : base(location) { Type = type; Expression = expression; }
    }
}
