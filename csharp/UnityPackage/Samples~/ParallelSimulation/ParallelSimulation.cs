using AngelScript.Burst;
using AngelScript.Burst.Compilation;
using AngelScript.Burst.Unity;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

[assembly: RegisterGenericJobType(typeof(ScriptBatchJob<AngelScript.Samples.SimulationHost>))]

namespace AngelScript.Samples
{
    public unsafe struct SimulationHost : IScriptHost
    {
        [ReadOnly] public NativeArray<float> Velocities;
        public bool Invoke(int functionId, int laneIndex, Value* arguments, int argumentCount, out Value result)
        {
            result = default(Value);
            if (functionId != 0 || argumentCount != 0) return false;
            result = Value.FromFloat(Velocities[laneIndex]);
            return true;
        }
    }

    /// <summary>Add to a GameObject. Only setup/readback touches managed code; script execution runs in jobs.</summary>
    public sealed class ParallelSimulation : MonoBehaviour
    {
        [Min(1)] public int EntityCount = 1024;
        private NativeScriptProgram program;
        private NativeScriptBatch batch;
        private NativeArray<float> velocities;
        private readonly Value[] arguments = new Value[1];
        private int tick;
        private float firstEntityDistance;

        private void Start()
        {
            var compiler = new ScriptCompiler();
            compiler.RegisterHostFunction("float readVelocity()", 0);
            CompiledModule module = compiler.Compile(@"
                float distance = 0;
                float tick(float deltaTime)
                {
                    distance += readVelocity() * deltaTime;
                    return distance;
                }", "movement.as");
            tick = module.FindFunction("tick", ScriptType.Float);
            program = new NativeScriptProgram(module);
            batch = new NativeScriptBatch(program, EntityCount,
                new VmLimits { StackCapacity = 32, LocalCapacity = 32, CallCapacity = 8 });
            velocities = new NativeArray<float>(EntityCount, Allocator.Persistent);
            for (int i = 0; i < EntityCount; i++) velocities[i] = 1.0f + i * 0.01f;
        }

        private void Update()
        {
            if (batch == null) return;
            arguments[0] = Value.FromFloat(Time.deltaTime);
            batch.PrepareAll(tick, arguments); // Completes the previous batch before touching its state.
            batch.Schedule(new SimulationHost { Velocities = velocities }, 1000, 32);
        }
        private void LateUpdate()
        {
            if (batch == null) return;
            ExecutionState result = batch.GetState(0); // Completes the handle before readback.
            if (result.Status == ExecutionStatus.Finished) firstEntityDistance = result.ReturnValue.AsFloat;
            else if (result.Status == ExecutionStatus.Faulted) Debug.LogError("Script fault: " + result.Error);
        }
        private void OnGUI()
        {
            GUILayout.Label("AngelScript C# jobs: " + EntityCount + " independent lanes");
            GUILayout.Label("Entity 0 distance: " + firstEntityDistance.ToString("F3"));
        }
        private void OnDestroy()
        {
            if (batch != null) batch.Dispose();
            if (program != null) program.Dispose();
            if (velocities.IsCreated) velocities.Dispose(); // Host data must outlive every job that reads it.
        }
    }
}
