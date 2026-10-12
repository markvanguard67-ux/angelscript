// Unofficial C# AngelScript subset; not the original AngelCode library.
using System;
using System.Collections.Generic;

namespace AngelScript.Burst.Compilation
{
    internal enum TokenKind { End, Identifier, Number, Symbol }
    internal struct Token
    {
        internal TokenKind Kind;
        internal string Text;
        internal SourceLocation Location;
    }

    internal sealed class Lexer
    {
        private readonly string source;
        private readonly string sourceName;
        private readonly CompilerOptions options;
        private int position;
        private int line = 1;
        private int column = 1;
        // Longest match first, notably AngelScript's arithmetic shift >>>.
        private static readonly string[] Operators = {
            ">>>=", "**=", "<<=", ">>=", ">>>", "++", "--", "+=", "-=", "*=", "/=", "%=",
            "&=", "|=", "^=", "==", "!=", "<=", ">=", "&&", "||", "^^", "<<", ">>", "**", "::"
        };

        internal Lexer(string source, string sourceName, CompilerOptions options)
        { this.source = source; this.sourceName = sourceName; this.options = options; }

        internal List<Token> Lex()
        {
            if (source.Length > options.MaxSourceLength)
                throw Failure(DiagnosticCode.ResourceLimit, "Source length exceeds MaxSourceLength.");
            var tokens = new List<Token>();
            while (position < source.Length)
            {
                char c = Current;
                if (char.IsWhiteSpace(c) || c == '\ufeff') { Advance(); continue; }
                if (c == '/' && Peek(1) == '/')
                {
                    while (position < source.Length && Current != '\n' && Current != '\r') Advance();
                    continue;
                }
                if (c == '/' && Peek(1) == '*')
                {
                    SourceLocation start = Location;
                    Advance(); Advance();
                    while (position < source.Length && !(Current == '*' && Peek(1) == '/')) Advance();
                    if (position == source.Length) throw new CompilationFailure(start, DiagnosticCode.UnexpectedToken, "Unterminated block comment.");
                    Advance(); Advance();
                    continue;
                }
                SourceLocation location = Location;
                int begin = position;
                TokenKind kind;
                if (IsIdentifierStart(c))
                {
                    Advance();
                    while (IsIdentifierStart(Current) || IsDigit(Current)) Advance();
                    kind = TokenKind.Identifier;
                }
                else if (IsDigit(c) || (c == '.' && IsDigit(Peek(1))))
                {
                    ScanNumber();
                    kind = TokenKind.Number;
                }
                else if (c == '"' || c == '\'')
                    throw Failure(DiagnosticCode.UnsupportedFeature, "Strings and character literals are not supported by the Burst primitive subset.");
                else
                {
                    kind = TokenKind.Symbol;
                    bool found = false;
                    foreach (string op in Operators)
                    {
                        if (position + op.Length <= source.Length && string.CompareOrdinal(source, position, op, 0, op.Length) == 0)
                        {
                            for (int i = 0; i < op.Length; i++) Advance();
                            found = true;
                            break;
                        }
                    }
                    if (!found)
                    {
                        if ("+-*/%&|^~!<>=?:;,.(){}[]@".IndexOf(c) < 0)
                            throw Failure(DiagnosticCode.UnexpectedToken, "Unrecognized character '" + c + "'.");
                        Advance();
                    }
                }
                if (tokens.Count >= options.MaxTokens) throw new CompilationFailure(location, DiagnosticCode.ResourceLimit, "Token count exceeds MaxTokens.");
                tokens.Add(new Token { Kind = kind, Text = source.Substring(begin, position - begin), Location = location });
            }
            tokens.Add(new Token { Kind = TokenKind.End, Text = "<end>", Location = Location });
            return tokens;
        }

        private void ScanNumber()
        {
            if (Current == '0' && "xXbBoOdD".IndexOf(Peek(1)) >= 0 && Peek(1) != '\0')
            {
                Advance(); Advance();
                while (IsDigit(Current) || (Current >= 'a' && Current <= 'f') || (Current >= 'A' && Current <= 'F') || Current == '\'') Advance();
                return;
            }
            while (IsDigit(Current) || Current == '\'') Advance();
            if (Current == '.')
            {
                Advance();
                while (IsDigit(Current) || Current == '\'') Advance();
            }
            if (Current == 'e' || Current == 'E')
            {
                Advance();
                if (Current == '+' || Current == '-') Advance();
                while (IsDigit(Current) || Current == '\'') Advance();
            }
            if (Current == 'f' || Current == 'F') Advance();
        }

        private static bool IsIdentifierStart(char c)
        { return c == '_' || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'); }
        private static bool IsDigit(char c) { return c >= '0' && c <= '9'; }
        private char Current { get { return Peek(0); } }
        private char Peek(int offset) { return position + offset < source.Length ? source[position + offset] : '\0'; }
        private SourceLocation Location { get { return new SourceLocation(sourceName, line, column); } }
        private CompilationFailure Failure(DiagnosticCode code, string message) { return new CompilationFailure(Location, code, message); }
        private void Advance()
        {
            if (position >= source.Length) return;
            char c = source[position++];
            if (c == '\r')
            {
                if (position < source.Length && source[position] == '\n') position++;
                line++; column = 1;
            }
            else if (c == '\n') { line++; column = 1; }
            else column++;
        }
    }
}
