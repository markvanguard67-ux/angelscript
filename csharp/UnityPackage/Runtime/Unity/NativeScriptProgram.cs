// Unofficial C# AngelScript subset; not the original AngelCode library.
using System;
using AngelScript.Burst.Compilation;
using Unity.Collections;
using Unity.Jobs;

namespace AngelScript.Burst.Unity
{
    /// <summary>Owns immutable native program tables shared read-only by any number of batches.</summary>
    public sealed class NativeScriptProgram : IDisposable
    {
        internal NativeArray<Instruction> Instructions;
        internal NativeArray<FunctionInfo> Functions;
        internal NativeArray<ScriptType> SlotTypes;
        internal NativeArray<Value> GlobalDefaults;
        private JobHandle lastUse;
        private bool disposed;
        public CompiledModule Module { get; private set; }
        public bool IsCreated { get { return !disposed && Instructions.IsCreated; } }

        public NativeScriptProgram(CompiledModule module, Allocator allocator = Allocator.Persistent)
        {
            if (module == null) throw new ArgumentNullException("module");
            ValidateAllocator(allocator);
            Module = module;
            try
            {
                Instructions = new NativeArray<Instruction>(module.CopyInstructions(), allocator);
                Functions = new NativeArray<FunctionInfo>(module.CopyFunctionInfo(), allocator);
                SlotTypes = new NativeArray<ScriptType>(module.CopySlotTypes(), allocator);
                GlobalDefaults = new NativeArray<Value>(module.CopyGlobalDefaults(), allocator);
            }
            catch { DisposeArrays(); disposed = true; throw; }
        }

        internal static void ValidateAllocator(Allocator allocator)
        {
            if (allocator != Allocator.Persistent && allocator != Allocator.TempJob)
                throw new ArgumentException("Use Persistent, or TempJob if every allocation is disposed within four frames.", "allocator");
        }
        internal void EnsureCreated()
        { if (disposed) throw new ObjectDisposedException("NativeScriptProgram"); }
        internal void RegisterUse(JobHandle handle)
        { lastUse = JobHandle.CombineDependencies(lastUse, handle); }
        public void Complete() { lastUse.Complete(); }

        public void Dispose()
        {
            // Also complete a disposal previously scheduled with Dispose(dependency).
            lastUse.Complete();
            if (disposed) return;
            DisposeArrays(); disposed = true;
        }
        /// <summary>Schedule native buffer disposal after all recorded readers and the supplied dependency.</summary>
        public JobHandle Dispose(JobHandle dependency)
        {
            if (disposed) return JobHandle.CombineDependencies(lastUse, dependency);
            JobHandle handle = JobHandle.CombineDependencies(lastUse, dependency);
            if (Instructions.IsCreated) handle = Instructions.Dispose(handle);
            if (Functions.IsCreated) handle = Functions.Dispose(handle);
            if (SlotTypes.IsCreated) handle = SlotTypes.Dispose(handle);
            if (GlobalDefaults.IsCreated) handle = GlobalDefaults.Dispose(handle);
            lastUse = handle; disposed = true;
            return handle;
        }
        private void DisposeArrays()
        {
            if (Instructions.IsCreated) Instructions.Dispose();
            if (Functions.IsCreated) Functions.Dispose();
            if (SlotTypes.IsCreated) SlotTypes.Dispose();
            if (GlobalDefaults.IsCreated) GlobalDefaults.Dispose();
        }
    }
}
