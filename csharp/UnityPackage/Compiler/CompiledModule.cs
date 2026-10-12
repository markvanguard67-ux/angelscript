// Unofficial C# AngelScript subset; not the original AngelCode library.
using System;
using System.Collections.ObjectModel;
using System.Text;

namespace AngelScript.Burst.Compilation
{
    public sealed class FunctionMetadata
    {
        public int Index { get; private set; }
        public string Name { get; private set; }
        public ScriptType ReturnType { get; private set; }
        public ReadOnlyCollection<ScriptType> ParameterTypes { get; private set; }
        public string Declaration { get; private set; }
        internal FunctionMetadata(FunctionSymbol symbol)
        {
            Index = symbol.Id; Name = symbol.Name; ReturnType = symbol.ReturnType;
            ParameterTypes = Array.AsReadOnly((ScriptType[])symbol.ParameterTypes.Clone());
            var declaration = new StringBuilder(TypeNames.Get(ReturnType)).Append(' ').Append(Name).Append('(');
            for (int i = 0; i < ParameterTypes.Count; i++)
            { if (i != 0) declaration.Append(", "); declaration.Append(TypeNames.Get(ParameterTypes[i])); }
            Declaration = declaration.Append(')').ToString();
        }
    }

    public sealed class GlobalMetadata
    {
        public int Index { get; private set; }
        public string Name { get; private set; }
        public ScriptType Type { get; private set; }
        public bool IsConst { get; private set; }
        public Value DefaultValue { get; private set; }
        internal GlobalMetadata(VariableSymbol symbol)
        { Index = symbol.Index; Name = symbol.Name; Type = symbol.Type; IsConst = symbol.IsConst; DefaultValue = symbol.InitialValue; }
    }

    /// <summary>Immutable managed output. Copy its unmanaged tables into persistent native buffers before scheduling jobs.</summary>
    public sealed class CompiledModule
    {
        public const int BytecodeVersion = 1;
        private readonly Instruction[] instructions;
        private readonly FunctionInfo[] functionInfo;
        private readonly ScriptType[] slotTypes;
        private readonly Value[] globalDefaults;
        private readonly SourceLocation[] locations;
        public string SourceName { get; private set; }
        public ReadOnlyCollection<FunctionMetadata> Functions { get; private set; }
        public ReadOnlyCollection<GlobalMetadata> Globals { get; private set; }
        public int InstructionCount { get { return instructions.Length; } }

        internal CompiledModule(string sourceName, Instruction[] instructions, FunctionInfo[] functionInfo,
            ScriptType[] slotTypes, Value[] globalDefaults, SourceLocation[] locations,
            FunctionMetadata[] functions, GlobalMetadata[] globals)
        {
            SourceName = sourceName;
            this.instructions = instructions; this.functionInfo = functionInfo; this.slotTypes = slotTypes;
            this.globalDefaults = globalDefaults; this.locations = locations;
            Functions = Array.AsReadOnly(functions); Globals = Array.AsReadOnly(globals);
        }

        /// <summary>Exact signature lookup, including zero arguments when parameterTypes is omitted. Returns -1 if absent.</summary>
        public int FindFunction(string name, params ScriptType[] parameterTypes)
        {
            if (name == null) throw new ArgumentNullException("name");
            if (parameterTypes == null) throw new ArgumentNullException("parameterTypes");
            foreach (FunctionMetadata function in Functions)
            {
                if (function.Name != name || function.ParameterTypes.Count != parameterTypes.Length) continue;
                bool match = true;
                for (int i = 0; i < parameterTypes.Length; i++) if (parameterTypes[i] != function.ParameterTypes[i]) { match = false; break; }
                if (match) return function.Index;
            }
            return -1;
        }
        public int FindGlobal(string name)
        {
            foreach (GlobalMetadata global in Globals) if (global.Name == name) return global.Index;
            return -1;
        }
        public SourceLocation GetInstructionLocation(int instructionIndex)
        {
            if ((uint)instructionIndex >= (uint)locations.Length) throw new ArgumentOutOfRangeException("instructionIndex");
            return locations[instructionIndex];
        }
        public Instruction[] CopyInstructions() { return (Instruction[])instructions.Clone(); }
        public FunctionInfo[] CopyFunctionInfo() { return (FunctionInfo[])functionInfo.Clone(); }
        public ScriptType[] CopySlotTypes() { return (ScriptType[])slotTypes.Clone(); }
        public Value[] CopyGlobalDefaults() { return (Value[])globalDefaults.Clone(); }
    }
}
