// Unofficial C# AngelScript subset; not the original AngelCode library.
using System.Collections.Generic;

namespace AngelScript.Burst.Compilation
{
    internal sealed class BindingResult
    {
        internal readonly List<BoundFunction> Functions = new List<BoundFunction>();
        internal readonly List<VariableSymbol> Globals = new List<VariableSymbol>();
    }

    internal sealed class ModuleBinder
    {
        internal readonly CompilerOptions Options;
        private readonly Dictionary<string, VariableSymbol> globals = new Dictionary<string, VariableSymbol>();
        private readonly Dictionary<string, List<FunctionSymbol>> functions = new Dictionary<string, List<FunctionSymbol>>();
        private readonly List<FunctionSymbol> scriptFunctions = new List<FunctionSymbol>();
        private readonly BindingResult result = new BindingResult();
        private int constantDepth;

        internal ModuleBinder(ModuleSyntax syntax, List<FunctionSymbol> registered, CompilerOptions options)
        {
            Options = options;
            foreach (GlobalSyntax declaration in syntax.Globals)
            {
                if (globals.ContainsKey(declaration.Variable.Name)) throw Duplicate(declaration.Location, declaration.Variable.Name);
                var symbol = new VariableSymbol
                {
                    Name = declaration.Variable.Name, Type = declaration.Type.Type, IsConst = declaration.Type.IsConst,
                    IsGlobal = true, Index = result.Globals.Count, GlobalSyntax = declaration, Location = declaration.Location
                };
                globals.Add(symbol.Name, symbol);
                result.Globals.Add(symbol);
            }
            foreach (FunctionSymbol function in registered) AddFunction(function);
            foreach (FunctionSyntax declaration in syntax.Functions)
            {
                var symbol = new FunctionSymbol { Syntax = declaration, Kind = FunctionKind.Script, Id = scriptFunctions.Count, ParameterTypes = ParameterTypes(declaration) };
                AddFunction(symbol); scriptFunctions.Add(symbol);
            }
        }

        internal BindingResult Bind()
        {
            foreach (VariableSymbol global in result.Globals) InitializeGlobal(global);
            foreach (FunctionSymbol function in scriptFunctions)
                result.Functions.Add(new FunctionBinder(this, function).BindFunction());
            return result;
        }

        internal VariableSymbol FindGlobal(string name)
        {
            VariableSymbol symbol;
            return globals.TryGetValue(name, out symbol) ? symbol : null;
        }
        internal List<FunctionSymbol> FindFunctions(string name)
        {
            List<FunctionSymbol> list;
            return functions.TryGetValue(name, out list) ? list : null;
        }
        internal Value ResolveConstant(VariableSymbol symbol) { InitializeGlobal(symbol); return symbol.ConstantValue; }

        private void InitializeGlobal(VariableSymbol symbol)
        {
            if (symbol.InitializationState == 2) return;
            if (symbol.InitializationState == 1) throw new CompilationFailure(symbol.Location, DiagnosticCode.InvalidConstant, "Cyclic global constant initializer involving '" + symbol.Name + "'.");
            if (++constantDepth > Options.MaxSyntaxDepth) throw new CompilationFailure(symbol.Location, DiagnosticCode.ResourceLimit, "Global initializer dependency depth exceeds MaxSyntaxDepth.");
            try
            {
                symbol.InitializationState = 1;
                ExpressionSyntax initializer = symbol.GlobalSyntax.Variable.Initializer;
                Value value = Value.Zero(symbol.Type);
                if (initializer != null)
                {
                    var binder = new FunctionBinder(this, null);
                    BoundExpression expression = binder.Convert(binder.BindExpression(initializer), symbol.Type, false, initializer.Location);
                    VmError error;
                    if (!ConstantFolder.TryEvaluate(expression, out value, out error))
                        throw new CompilationFailure(initializer.Location, DiagnosticCode.InvalidConstant,
                            error == VmError.None ? "Global initializers must be constant primitive expressions; mutable globals and function calls are not allowed." : "Invalid global initializer: " + error + ".");
                }
                else if (symbol.IsConst) throw new CompilationFailure(symbol.Location, DiagnosticCode.InvalidConstant, "A const global requires an initializer.");
                symbol.InitialValue = value;
                symbol.HasConstantValue = symbol.IsConst;
                symbol.ConstantValue = value;
                symbol.InitializationState = 2;
            }
            finally { constantDepth--; }
        }

        private void AddFunction(FunctionSymbol symbol)
        {
            if (globals.ContainsKey(symbol.Name)) throw Duplicate(symbol.Syntax.Location, symbol.Name);
            List<FunctionSymbol> overloads;
            if (!functions.TryGetValue(symbol.Name, out overloads))
            { overloads = new List<FunctionSymbol>(); functions.Add(symbol.Name, overloads); }
            foreach (FunctionSymbol previous in overloads)
                if (SameSignature(previous, symbol)) throw Duplicate(symbol.Syntax.Location, symbol.Name);
            overloads.Add(symbol);
        }

        internal static ScriptType[] ParameterTypes(FunctionSyntax syntax)
        {
            var types = new ScriptType[syntax.Parameters.Count];
            for (int i = 0; i < types.Length; i++) types[i] = syntax.Parameters[i].Type.Type;
            return types;
        }
        internal static bool SameSignature(FunctionSymbol a, FunctionSymbol b)
        {
            if (a.Name != b.Name || a.ParameterTypes.Length != b.ParameterTypes.Length) return false;
            for (int i = 0; i < a.ParameterTypes.Length; i++) if (a.ParameterTypes[i] != b.ParameterTypes[i]) return false;
            return true;
        }
        internal static CompilationFailure Duplicate(SourceLocation location, string name)
        { return new CompilationFailure(location, DiagnosticCode.DuplicateName, "Duplicate declaration or conflicting name '" + name + "'."); }
    }
}
