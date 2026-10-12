// Unofficial C# AngelScript subset; not the original AngelCode library.
using System.Collections.Generic;

namespace AngelScript.Burst.Compilation
{
    internal sealed class Parser
    {
        private readonly List<Token> tokens;
        private readonly CompilerOptions options;
        private int position;
        private int depth;
        private static readonly HashSet<string> Reserved = new HashSet<string> {
            "void", "bool", "int", "int8", "int16", "int32", "int64", "uint", "uint8", "uint16", "uint32", "uint64", "float", "double",
            "const", "if", "else", "for", "while", "do", "return", "break", "continue", "switch", "case", "default", "true", "false", "and", "or", "not", "xor",
            "class", "interface", "mixin", "enum", "namespace", "using", "typedef", "funcdef", "import", "from", "shared", "external", "abstract", "final", "override",
            "private", "protected", "super", "this", "cast", "null", "is", "in", "out", "inout", "auto", "foreach", "try", "catch", "function", "explicit", "property", "delete"
        };
        private static readonly HashSet<string> Unsupported = new HashSet<string> {
            "class", "interface", "mixin", "enum", "namespace", "using", "typedef", "funcdef", "import", "shared", "external", "abstract", "final", "override",
            "private", "protected", "super", "this", "cast", "null", "is", "in", "out", "inout", "auto", "foreach", "try", "catch", "function", "explicit", "property", "delete",
            "@", "[", "]", "::", "."
        };

        internal Parser(List<Token> tokens, CompilerOptions options) { this.tokens = tokens; this.options = options; }
        private Token Current { get { return tokens[position]; } }
        private Token Next { get { return tokens[position + 1 < tokens.Count ? position + 1 : position]; } }
        private Token Take() { Token token = Current; if (position < tokens.Count - 1) position++; return token; }
        private bool Match(string text) { if (Current.Text != text) return false; Take(); return true; }
        private Token Expect(string text)
        {
            if (Current.Text != text) throw Unexpected("Expected '" + text + "', found '" + Current.Text + "'.");
            return Take();
        }
        private Token Name()
        {
            if (Current.Kind != TokenKind.Identifier || Reserved.Contains(Current.Text)) throw Unexpected("Expected an identifier, found '" + Current.Text + "'.");
            return Take();
        }
        private CompilationFailure Unexpected(string message)
        {
            if (Unsupported.Contains(Current.Text))
                return new CompilationFailure(Current.Location, DiagnosticCode.UnsupportedFeature, "Feature '" + Current.Text + "' is not supported by the Burst primitive subset.");
            return new CompilationFailure(Current.Location, DiagnosticCode.UnexpectedToken, message);
        }
        private void Enter()
        {
            depth++;
            if (depth > options.MaxSyntaxDepth) throw new CompilationFailure(Current.Location, DiagnosticCode.ResourceLimit, "Nesting exceeds MaxSyntaxDepth.");
        }
        private void Exit() { depth--; }
        private bool StartsType()
        {
            ScriptType ignored;
            return Current.Text == "const" || TypeNames.TryParse(Current.Text, out ignored);
        }
        private bool StartsDeclaration()
        {
            // A primitive type followed by '(' is a cast expression, not a variable declaration.
            return StartsType() && Next.Text != "(";
        }
        private TypeSyntax ReadType(bool allowVoid)
        {
            SourceLocation location = Current.Location;
            bool constant = Match("const");
            ScriptType type;
            if (!TypeNames.TryParse(Current.Text, out type))
                throw new CompilationFailure(Current.Location, DiagnosticCode.UnsupportedFeature, "Only primitive types are supported; type or feature '" + Current.Text + "' is unavailable.");
            Take();
            if (!allowVoid && type == ScriptType.Void) throw new CompilationFailure(location, DiagnosticCode.TypeMismatch, "A variable or parameter cannot have type void.");
            if (Current.Text == "&" || Current.Text == "@" || Current.Text == "[")
                throw new CompilationFailure(Current.Location, DiagnosticCode.UnsupportedFeature, "Reference parameters, object handles and arrays are not supported yet.");
            return new TypeSyntax { Type = type, IsConst = constant, Location = location };
        }

        internal ModuleSyntax ParseModule()
        {
            var module = new ModuleSyntax();
            while (Current.Kind != TokenKind.End)
            {
                if (Match(";")) continue;
                TypeSyntax type = ReadType(true);
                Token name = Name();
                if (Current.Text == "(")
                {
                    List<ParameterSyntax> parameters = ReadParameters();
                    if (Current.Text == ";") throw new CompilationFailure(Current.Location, DiagnosticCode.UnsupportedFeature, "Function prototypes are not supported; define a body (forward calls do not need prototypes).");
                    var body = ParseStatement() as BlockSyntax;
                    if (body == null) throw new CompilationFailure(name.Location, DiagnosticCode.UnexpectedToken, "A function must have a braced body.");
                    if (module.Functions.Count >= options.MaxFunctions) throw new CompilationFailure(name.Location, DiagnosticCode.ResourceLimit, "Function count exceeds MaxFunctions.");
                    module.Functions.Add(new FunctionSyntax(type, name.Text, parameters, body, name.Location));
                }
                else
                {
                    if (type.Type == ScriptType.Void) throw new CompilationFailure(type.Location, DiagnosticCode.TypeMismatch, "A global variable cannot have type void.");
                    foreach (DeclaratorSyntax variable in ReadDeclarators(name))
                    {
                        if (module.Globals.Count >= options.MaxGlobals) throw new CompilationFailure(variable.Location, DiagnosticCode.ResourceLimit, "Global count exceeds MaxGlobals.");
                        module.Globals.Add(new GlobalSyntax(type, variable));
                    }
                }
            }
            return module;
        }

        internal FunctionSyntax ParseSignature()
        {
            TypeSyntax type = ReadType(true);
            Token name = Name();
            List<ParameterSyntax> parameters = ReadParameters();
            Match(";");
            if (Current.Kind != TokenKind.End) throw Unexpected("Expected the end of the function declaration.");
            return new FunctionSyntax(type, name.Text, parameters, null, name.Location);
        }

        private List<ParameterSyntax> ReadParameters()
        {
            Expect("(");
            var parameters = new List<ParameterSyntax>();
            if (Match(")")) return parameters;
            if (Current.Text == "void" && Next.Text == ")") { Take(); Take(); return parameters; }
            do
            {
                TypeSyntax type = ReadType(false);
                string name = "\0parameter" + parameters.Count;
                SourceLocation location = type.Location;
                if (Current.Kind == TokenKind.Identifier && !Reserved.Contains(Current.Text))
                { Token token = Name(); name = token.Text; location = token.Location; }
                if (Current.Text == "=") throw new CompilationFailure(Current.Location, DiagnosticCode.UnsupportedFeature, "Default arguments are not supported yet.");
                if (parameters.Count >= options.MaxParameters) throw new CompilationFailure(location, DiagnosticCode.ResourceLimit, "Parameter count exceeds MaxParameters.");
                parameters.Add(new ParameterSyntax(type, name, location));
            } while (Match(","));
            Expect(")");
            return parameters;
        }

        private List<DeclaratorSyntax> ReadDeclarators(Token first)
        {
            var variables = new List<DeclaratorSyntax>();
            Token name = first;
            do
            {
                ExpressionSyntax initializer = Match("=") ? ParseExpression() : null;
                variables.Add(new DeclaratorSyntax(name.Text, initializer, name.Location));
                if (!Match(",")) break;
                name = Name();
            } while (true);
            Expect(";");
            return variables;
        }

        private StatementSyntax ParseStatement()
        {
            Enter();
            try
            {
                SourceLocation location = Current.Location;
                if (Match("{"))
                {
                    var statements = new List<StatementSyntax>();
                    while (Current.Text != "}" && Current.Kind != TokenKind.End) statements.Add(ParseStatement());
                    Expect("}");
                    return new BlockSyntax(statements, location);
                }
                if (Match("if"))
                {
                    Expect("("); ExpressionSyntax condition = ParseExpression(); Expect(")");
                    StatementSyntax then = ParseStatement();
                    StatementSyntax otherwise = Match("else") ? ParseStatement() : null;
                    return new IfSyntax(condition, then, otherwise, location);
                }
                if (Match("while"))
                {
                    Expect("("); ExpressionSyntax condition = ParseExpression(); Expect(")");
                    return new WhileSyntax(condition, ParseStatement(), false, location);
                }
                if (Match("do"))
                {
                    StatementSyntax body = ParseStatement();
                    Expect("while"); Expect("("); ExpressionSyntax condition = ParseExpression(); Expect(")"); Expect(";");
                    return new WhileSyntax(condition, body, true, location);
                }
                if (Match("for")) return ReadFor(location);
                if (Match("switch")) return ReadSwitch(location);
                if (Match("return"))
                {
                    ExpressionSyntax expression = Current.Text == ";" ? null : ParseExpression();
                    Expect(";"); return new ReturnSyntax(expression, location);
                }
                if (Current.Text == "break" || Current.Text == "continue")
                {
                    bool isContinue = Take().Text == "continue";
                    Expect(";"); return new ControlSyntax(isContinue, location);
                }
                if (StartsDeclaration() || (Current.Kind == TokenKind.Identifier && Next.Kind == TokenKind.Identifier && !Reserved.Contains(Next.Text)))
                {
                    TypeSyntax type = ReadType(false);
                    return new VariableStatementSyntax(type, ReadDeclarators(Name()));
                }
                if (Match(";")) return new ExpressionStatementSyntax(null, location);
                ExpressionSyntax value = ParseExpression();
                Expect(";");
                return new ExpressionStatementSyntax(value, location);
            }
            finally { Exit(); }
        }

        private ForSyntax ReadFor(SourceLocation location)
        {
            Expect("(");
            StatementSyntax initializer;
            if (StartsDeclaration())
            {
                TypeSyntax type = ReadType(false);
                initializer = new VariableStatementSyntax(type, ReadDeclarators(Name()));
            }
            else
            {
                var statements = new List<StatementSyntax>();
                if (Current.Text != ";")
                {
                    SourceLocation initializerLocation = Current.Location;
                    statements.Add(new ExpressionStatementSyntax(ParseExpression(), initializerLocation));
                }
                Expect(";");
                initializer = new BlockSyntax(statements, location);
            }
            ExpressionSyntax condition = Current.Text == ";" ? null : ParseExpression();
            Expect(";");
            var iteration = new List<ExpressionSyntax>();
            if (Current.Text != ")") do { iteration.Add(ParseExpression()); } while (Match(","));
            Expect(")");
            return new ForSyntax(initializer, condition, iteration, ParseStatement(), location);
        }

        private SwitchSyntax ReadSwitch(SourceLocation location)
        {
            Expect("("); ExpressionSyntax expression = ParseExpression(); Expect(")"); Expect("{");
            var cases = new List<SwitchCaseSyntax>();
            while (Current.Text != "}" && Current.Kind != TokenKind.End)
            {
                SourceLocation labelLocation = Current.Location;
                ExpressionSyntax label;
                if (Match("case")) label = ParseExpression();
                else if (Match("default")) label = null;
                else throw Unexpected("Expected case or default.");
                Expect(":");
                var statements = new List<StatementSyntax>();
                while (Current.Text != "case" && Current.Text != "default" && Current.Text != "}" && Current.Kind != TokenKind.End)
                    statements.Add(ParseStatement());
                cases.Add(new SwitchCaseSyntax(label, statements, labelLocation));
            }
            Expect("}");
            if (cases.Count == 0) throw new CompilationFailure(location, DiagnosticCode.InvalidOperation, "An AngelScript switch must contain at least one case or default label.");
            return new SwitchSyntax(expression, cases, location);
        }

        private ExpressionSyntax ParseExpression(int minimumPrecedence = 1)
        {
            Enter();
            try
            {
                ExpressionSyntax expression = ParseUnary();
                while (true)
                {
                    string op = Current.Text;
                    int precedence = Precedence(op);
                    if (precedence < minimumPrecedence) break;
                    SourceLocation location = Take().Location;
                    if (op == "?")
                    {
                        ExpressionSyntax then = ParseExpression(); Expect(":"); ExpressionSyntax otherwise = ParseExpression();
                        expression = new ConditionalSyntax(expression, then, otherwise, location);
                    }
                    else
                    {
                        // Assignments are right associative; other binary operators, including **, are left associative in AngelScript.
                        ExpressionSyntax right = ParseExpression(precedence == 1 ? precedence : precedence + 1);
                        expression = new BinarySyntax(op, expression, right, location);
                    }
                }
                return expression;
            }
            finally { Exit(); }
        }

        private ExpressionSyntax ParseUnary()
        {
            Enter();
            try
            {
                if (Current.Text == "+" || Current.Text == "-" || Current.Text == "!" || Current.Text == "not" ||
                    Current.Text == "~" || Current.Text == "++" || Current.Text == "--")
                {
                    Token token = Take();
                    return new UnarySyntax(token.Text, ParseUnary(), false, token.Location);
                }
                ExpressionSyntax expression = ParsePrimary();
                while (Current.Text == "++" || Current.Text == "--")
                { Token token = Take(); expression = new UnarySyntax(token.Text, expression, true, token.Location); }
                if (Current.Text == "[" || Current.Text == "." || Current.Text == "::" || Current.Text == "@") throw Unexpected("Unsupported expression.");
                return expression;
            }
            finally { Exit(); }
        }

        private ExpressionSyntax ParsePrimary()
        {
            Token token = Current;
            if (Match("(")) { ExpressionSyntax expression = ParseExpression(); Expect(")"); return expression; }
            if (token.Kind == TokenKind.Number || token.Text == "true" || token.Text == "false")
            { Take(); return new LiteralSyntax(token.Text, token.Location); }
            ScriptType type;
            if (TypeNames.TryParse(token.Text, out type) && Next.Text == "(")
            {
                Take(); Expect("("); ExpressionSyntax expression = ParseExpression(); Expect(")");
                return new CastSyntax(type, expression, token.Location);
            }
            Token name = Name();
            if (!Match("(")) return new NameSyntax(name.Text, name.Location);
            var arguments = new List<ExpressionSyntax>();
            if (Current.Text != ")")
                do
                {
                    if (Current.Kind == TokenKind.Identifier && Next.Text == ":")
                        throw new CompilationFailure(Current.Location, DiagnosticCode.UnsupportedFeature, "Named arguments are not supported yet.");
                    if (arguments.Count >= options.MaxParameters) throw new CompilationFailure(Current.Location, DiagnosticCode.ResourceLimit, "Argument count exceeds MaxParameters.");
                    arguments.Add(ParseExpression());
                } while (Match(","));
            Expect(")");
            return new CallSyntax(name.Text, arguments, name.Location);
        }

        private static int Precedence(string op)
        {
            switch (op)
            {
                case "=": case "+=": case "-=": case "*=": case "/=": case "%=": case "**=": case "&=": case "|=": case "^=": case "<<=": case ">>=": case ">>>=": return 1;
                case "?": return 2;
                case "or": case "||": return 3;
                case "and": case "&&": return 4;
                case "==": case "!=": case "xor": case "^^": return 5;
                case "<": case "<=": case ">": case ">=": return 6;
                case "|": return 7;
                case "^": return 8;
                case "&": return 9;
                case "<<": case ">>": case ">>>": return 10;
                case "+": case "-": return 11;
                case "*": case "/": case "%": return 12;
                case "**": return 13;
                default: return 0;
            }
        }
    }
}
