// Unofficial C# AngelScript subset; not the original AngelCode library.
using System;
using System.Collections.Generic;

namespace AngelScript.Burst.Compilation
{
    /// <summary>
    /// Managed compilation, outside Burst/jobs. Reusable sequentially with the same host registrations.
    /// Each successful compilation produces an immutable module with no engine/context dependency.
    /// </summary>
    public sealed class ScriptCompiler
    {
        private readonly CompilerOptions options;
        private readonly List<FunctionSymbol> registered = new List<FunctionSymbol>();
        private bool mathRegistered;

        public ScriptCompiler(CompilerOptions options = null)
        { this.options = (options ?? new CompilerOptions()).Snapshot(); }

        /// <summary>Register a primitive signature and an unmanaged dispatch ID, not a managed delegate.</summary>
        public void RegisterHostFunction(string declaration, int functionId)
        {
            if (functionId < 0) throw new ArgumentOutOfRangeException("functionId");
            Register(declaration, FunctionKind.Host, functionId);
        }

        /// <summary>Optional scalar math intrinsics. They are not implicitly part of the original language.</summary>
        public void RegisterMathFunctions()
        {
            if (mathRegistered) return;
            int originalCount = registered.Count;
            try
            {
                string[] realTypes = { "float", "double" };
                string[] names = { "sin", "cos", "tan", "sqrt", "abs", "floor", "ceil", "exp", "log" };
                IntrinsicId[] ids = { IntrinsicId.Sin, IntrinsicId.Cos, IntrinsicId.Tan, IntrinsicId.Sqrt, IntrinsicId.Abs,
                    IntrinsicId.Floor, IntrinsicId.Ceil, IntrinsicId.Exp, IntrinsicId.Log };
                foreach (string type in realTypes)
                {
                    for (int i = 0; i < names.Length; i++) Register(type + " " + names[i] + "(" + type + ")", FunctionKind.Intrinsic, (int)ids[i]);
                    Register(type + " pow(" + type + ", " + type + ")", FunctionKind.Intrinsic, (int)IntrinsicId.Pow);
                }
                foreach (string type in new[] { "int", "uint", "int64", "uint64", "float", "double" })
                {
                    Register(type + " min(" + type + ", " + type + ")", FunctionKind.Intrinsic, (int)IntrinsicId.Min);
                    Register(type + " max(" + type + ", " + type + ")", FunctionKind.Intrinsic, (int)IntrinsicId.Max);
                    Register(type + " clamp(" + type + ", " + type + ", " + type + ")", FunctionKind.Intrinsic, (int)IntrinsicId.Clamp);
                }
                mathRegistered = true;
            }
            catch
            {
                registered.RemoveRange(originalCount, registered.Count - originalCount);
                throw;
            }
        }

        public CompiledModule Compile(string source, string sourceName = "script.as")
        {
            CompilationResult result = TryCompile(source, sourceName);
            if (!result.Success) throw new ScriptCompilationException(result.Diagnostics);
            return result.Module;
        }

        /// <summary>Reports the first error with a source position. It never returns partially compiled bytecode.</summary>
        public CompilationResult TryCompile(string source, string sourceName = "script.as")
        {
            if (source == null) throw new ArgumentNullException("source");
            if (sourceName == null) throw new ArgumentNullException("sourceName");
            try
            {
                var tokens = new Lexer(source, sourceName, options).Lex();
                ModuleSyntax syntax = new Parser(tokens, options).ParseModule();
                BindingResult binding = new ModuleBinder(syntax, registered, options).Bind();
                CompiledModule module = new BytecodeEmitter(binding, options, sourceName).Emit();
                return new CompilationResult(module, new Diagnostic[0]);
            }
            catch (CompilationFailure failure)
            {
                return new CompilationResult(null, new[] { failure.Diagnostic });
            }
        }

        private void Register(string declaration, FunctionKind kind, int id)
        {
            if (declaration == null) throw new ArgumentNullException("declaration");
            try
            {
                FunctionSyntax syntax = new Parser(new Lexer(declaration, "<host declaration>", options).Lex(), options).ParseSignature();
                var symbol = new FunctionSymbol { Syntax = syntax, Kind = kind, Id = id, ParameterTypes = ModuleBinder.ParameterTypes(syntax) };
                foreach (FunctionSymbol previous in registered)
                    if (ModuleBinder.SameSignature(previous, symbol))
                        throw new CompilationFailure(syntax.Location, DiagnosticCode.DuplicateName, "Function signature '" + syntax.Name + "' is already registered.");
                registered.Add(symbol);
            }
            catch (CompilationFailure failure)
            {
                throw new ScriptCompilationException(new CompilationResult(null, new[] { failure.Diagnostic }).Diagnostics);
            }
        }
    }
}
