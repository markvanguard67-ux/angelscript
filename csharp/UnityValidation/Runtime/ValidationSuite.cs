using System;
using System.Collections;
using System.Diagnostics;
using System.Collections.Generic;
using AngelScript.Burst.Compilation;
using AngelScript.Burst.Unity;
using Unity.Burst;
using Unity.Collections;

namespace AngelScript.Burst.Validation
{
    /// <summary>Same suite used by the visible scene, PlayMode tests and standalone validation players.</summary>
    public static partial class ValidationSuite
    {
        public static IEnumerator Run(ValidationOptions options, ValidationReport report, Action<ValidationResult> onResult = null,
            Func<bool> cancellation = null, Action<float[]> onLaneValues = null)
        {
            options.Validate();
            ValidationCorpus corpus = ValidationCorpus.Load();
            report.environment = ValidationEnvironment.Capture(corpus);
            var scenarios = new List<Scenario>(Scenarios(options, onLaneValues));
            var plan = new List<string>();
            foreach (Scenario scenario in scenarios) plan.Add(scenario.Id);
            foreach (ValidationCase item in corpus.cases) plan.Add(item.id);
            report.plannedCaseIds = plan.ToArray();
            foreach (Scenario scenario in scenarios)
            {
                if (cancellation != null && cancellation()) { report.Finish(true); yield break; }
                var result = new ValidationResult { id = scenario.Id, title = scenario.Title, category = scenario.Category,
                    phase = "Current", requiredForSubset = true, status = "NotRun", expected = "All assertions; real Burst where applicable" };
                var watch = Stopwatch.StartNew();
                try { scenario.Execute(result); result.status = "Passed"; }
                catch (Exception error) { result.status = "Failed"; result.detail = error.ToString(); }
                watch.Stop(); result.milliseconds = watch.Elapsed.TotalMilliseconds;
                report.results.Add(result); if (onResult != null) onResult(result);
                // No native allocations/job handles live across this yield. Cancellation/destruction cannot strand them.
                yield return null;
            }
            foreach (ValidationCase item in corpus.cases)
            {
                if (cancellation != null && cancellation()) { report.Finish(true); yield break; }
                ValidationResult result = ValidationResult.For(item);
                var watch = Stopwatch.StartNew();
                try { ExecuteOracleCase(item, options, result); }
                catch (Exception error) { result.status = "Failed"; result.detail = error.ToString(); }
                watch.Stop(); result.milliseconds = watch.Elapsed.TotalMilliseconds;
                report.results.Add(result); if (onResult != null) onResult(result);
                yield return null;
            }
            report.Finish(false);
        }

        public static void ExecuteOracleCase(ValidationCase item, ValidationOptions options, ValidationResult result)
        {
            if (item.scenario == "Capability")
            {
                result.status = "Blocked";
                result.detail = "No C# validation driver for this engine/API capability yet. Implement instrumentation before claiming coverage: " + item.feature;
                return;
            }
            var compiler = Compiler(item.profile);
            CompilationResult compiled = compiler.TryCompile(item.source, item.id + ".as");
            if (!compiled.Success)
            {
                result.observed = "CompileRejected: " + compiled.Diagnostics[0];
                if (item.expectedOutcome == "CompileRejected") result.status = "Passed";
                else
                {
                    result.status = item.phase == "Future" ? "Blocked" : "Failed";
                    result.detail = "Native engine accepts this fixture. C# compiler rejected it; a feature gap is not a passing negative test.";
                }
                return;
            }
            if (item.expectedOutcome == "CompileRejected")
            {
                result.status = "Failed"; result.observed = "C# accepted source rejected by C++"; return;
            }
            int entry = compiled.Module.FindFunction("test");
            Require(entry >= 0, "Missing zero-argument test entry.");
            ExecutionState managed;
            using (var fixture = new ValidationNativeFixture(compiled.Module))
            {
                Require(fixture.Prepare(entry) == ExecutionStatus.Ready, "Managed preparation failed.");
                for (int slice = 0; slice < options.MaximumSlices; slice++)
                    if (fixture.Execute(options.InstructionBudget) != ExecutionStatus.Suspended) break;
                managed = fixture.State;
            }
            result.observed = "managed=" + Describe(managed);
            CheckNative(item, managed, "managed core");

            // The host marker is invoked at the wrapper entry on EVERY lane; absence/managed fallback fails.
            var jobCompiler = Compiler(item.profile); jobCompiler.RegisterHostFunction("void __as_validation_probe()", 0);
            string type = item.declaration.Split(' ')[0];
            string body = item.ReturnType == ScriptType.Void ? "test();return;" : "return test();";
            CompiledModule jobModule = jobCompiler.Compile(item.source + "\n" + type + " __as_validation_entry(){__as_validation_probe();" + body + "}");
            using (var markers = new NativeArray<int>(options.Lanes, Allocator.Persistent))
            using (var calls = new NativeArray<int>(options.Lanes, Allocator.Persistent))
            using (var values = new NativeArray<Value>(options.Lanes, Allocator.Persistent))
            using (var program = new NativeScriptProgram(jobModule))
            using (var batch = new NativeScriptBatch(program, options.Lanes))
            {
                batch.PrepareAll(jobModule.FindFunction("__as_validation_entry"));
                var host = new ValidationHost { Markers = markers, Calls = calls, HostValues = values };
                for (int slice = 0; slice < options.MaximumSlices; slice++)
                {
                    batch.Schedule(host, options.InstructionBudget, 1).Complete();
                    bool active = false;
                    for (int lane = 0; lane < options.Lanes; lane++) active |= batch.GetState(lane).Status == ExecutionStatus.Suspended;
                    if (!active) break;
                }
                for (int lane = 0; lane < options.Lanes; lane++)
                {
                    Require(markers[lane] == 1, "Lane " + lane + " did not execute in Burst (marker=" + markers[lane] + "). Managed fallback is failure.");
                    CheckNative(item, batch.GetState(lane), "Burst lane " + lane);
                    Require(Same(managed, batch.GetState(lane)), "Managed and Burst status/type/raw bits differ on lane " + lane + ".");
                }
            }
            result.burstExecuted = true; result.lanes = options.Lanes; result.status = "Passed";
            result.observed += "; " + options.Lanes + " real-Burst lanes matched C++ status/type/bits";
        }

        private static ScriptCompiler Compiler(string profile)
        {
            var compiler = new ScriptCompiler();
            if (profile == "math") compiler.RegisterMathFunctions();
            return compiler;
        }
        private static void CheckNative(ValidationCase item, ExecutionState state, string path)
        {
            if (item.expectedOutcome == "RuntimeFault") Require(state.Status == ExecutionStatus.Faulted, path + ": expected native runtime fault, got " + Describe(state));
            else Require(state.Status == ExecutionStatus.Finished && state.ReturnValue.Type == item.ReturnType && state.ReturnValue.Bits == item.ExpectedRawBits,
                path + ": expected " + item.ReturnType + ":" + item.expectedBits + ", got " + Describe(state));
        }
        private static bool Same(ExecutionState a, ExecutionState b)
        {
            return a.Status == b.Status && (a.Status == ExecutionStatus.Faulted ? a.Error == b.Error : a.ReturnValue.Type == b.ReturnValue.Type && a.ReturnValue.Bits == b.ReturnValue.Bits);
        }
        private static string Describe(ExecutionState state) { return state.Status + "/" + state.Error + "/" + state.ReturnValue.Type + "/" + state.ReturnValue.Bits; }
        internal static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        private sealed class Scenario
        {
            public string Id, Title, Category;
            public Action<ValidationResult> Execute;
            public Scenario(string id, string title, Action<ValidationResult> execute, string category = "Integration / Burst and jobs")
            { Id = id; Title = title; Execute = execute; Category = category; }
        }
    }
}
