// Unofficial C# AngelScript subset; not the original AngelCode library.
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace AngelScript.Burst.Compilation
{
    public struct SourceLocation
    {
        public readonly string SourceName;
        public readonly int Line;
        public readonly int Column;
        public SourceLocation(string sourceName, int line, int column)
        { SourceName = sourceName; Line = line; Column = column; }
        public override string ToString() { return (SourceName ?? "script") + "(" + Line + "," + Column + ")"; }
    }

    public enum DiagnosticCode
    {
        UnexpectedToken, UnsupportedFeature, InvalidLiteral, UnknownName, DuplicateName,
        TypeMismatch, InvalidOperation, InvalidConstant, MissingReturn, AmbiguousCall, ResourceLimit
    }

    public sealed class Diagnostic
    {
        public SourceLocation Location { get; private set; }
        public DiagnosticCode Code { get; private set; }
        public string Message { get; private set; }
        internal Diagnostic(SourceLocation location, DiagnosticCode code, string message)
        { Location = location; Code = code; Message = message; }
        public override string ToString() { return Location + ": " + Code + ": " + Message; }
    }

    public sealed class CompilationResult
    {
        public CompiledModule Module { get; private set; }
        public ReadOnlyCollection<Diagnostic> Diagnostics { get; private set; }
        public bool Success { get { return Module != null; } }
        internal CompilationResult(CompiledModule module, IList<Diagnostic> diagnostics)
        { Module = module; Diagnostics = new ReadOnlyCollection<Diagnostic>(diagnostics); }
    }

    public sealed class ScriptCompilationException : Exception
    {
        public ReadOnlyCollection<Diagnostic> Diagnostics { get; private set; }
        internal ScriptCompilationException(ReadOnlyCollection<Diagnostic> diagnostics)
            : base(diagnostics.Count == 0 ? "Script compilation failed." : diagnostics[0].ToString())
        { Diagnostics = diagnostics; }
    }

    internal sealed class CompilationFailure : Exception
    {
        internal readonly Diagnostic Diagnostic;
        internal CompilationFailure(SourceLocation location, DiagnosticCode code, string message)
        { Diagnostic = new Diagnostic(location, code, message); }
    }

    /// <summary>Limits apply to the managed compiler; VM execution has separate per-lane limits.</summary>
    public sealed class CompilerOptions
    {
        public int MaxSourceLength { get; set; } = 1024 * 1024;
        public int MaxTokens { get; set; } = 100000;
        public int MaxSyntaxDepth { get; set; } = 128;
        public int MaxInstructions { get; set; } = 200000;
        public int MaxFunctions { get; set; } = 4096;
        public int MaxGlobals { get; set; } = 4096;
        public int MaxLocalsPerFunction { get; set; } = 4096;
        public int MaxParameters { get; set; } = 128;

        internal CompilerOptions Snapshot()
        {
            if (MaxSourceLength < 1 || MaxTokens < 1 || MaxSyntaxDepth < 1 || MaxSyntaxDepth > 512 ||
                MaxInstructions < 1 || MaxFunctions < 1 || MaxGlobals < 0 || MaxLocalsPerFunction < 1 || MaxParameters < 0)
                throw new ArgumentOutOfRangeException("options", "Compiler limits must be positive (globals/parameters may be zero), and syntax depth cannot exceed 512.");
            return (CompilerOptions)MemberwiseClone();
        }
    }

    internal static class TypeNames
    {
        internal static bool TryParse(string name, out ScriptType type)
        {
            switch (name)
            {
                case "void": type = ScriptType.Void; return true;
                case "bool": type = ScriptType.Bool; return true;
                case "int8": type = ScriptType.Int8; return true;
                case "uint8": type = ScriptType.UInt8; return true;
                case "int16": type = ScriptType.Int16; return true;
                case "uint16": type = ScriptType.UInt16; return true;
                case "int": case "int32": type = ScriptType.Int32; return true;
                case "uint": case "uint32": type = ScriptType.UInt32; return true;
                case "int64": type = ScriptType.Int64; return true;
                case "uint64": type = ScriptType.UInt64; return true;
                case "float": type = ScriptType.Float; return true;
                case "double": type = ScriptType.Double; return true;
                default: type = ScriptType.Void; return false;
            }
        }
        internal static string Get(ScriptType type)
        {
            switch (type)
            {
                case ScriptType.Void: return "void";
                case ScriptType.Bool: return "bool";
                case ScriptType.Int8: return "int8";
                case ScriptType.UInt8: return "uint8";
                case ScriptType.Int16: return "int16";
                case ScriptType.UInt16: return "uint16";
                case ScriptType.Int32: return "int";
                case ScriptType.UInt32: return "uint";
                case ScriptType.Int64: return "int64";
                case ScriptType.UInt64: return "uint64";
                case ScriptType.Float: return "float";
                case ScriptType.Double: return "double";
                default: return "<invalid>";
            }
        }
    }
}
