using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Unity.Burst;
using Unity.Jobs.LowLevel.Unsafe;
using UnityEngine;

namespace AngelScript.Burst.Validation
{
    [Serializable]
    public sealed class ValidationCase
    {
        public string id, title, category, phase, feature, source, declaration, profile, scenario;
        public string expectedOutcome, expectedBits, nativeDetail;
        public bool RequiredForSubset { get { return phase == "Current"; } }
        public ScriptType ReturnType
        {
            get
            {
                string name = declaration.Split(' ')[0];
                switch (name)
                {
                    case "void": return ScriptType.Void;
                    case "bool": return ScriptType.Bool;
                    case "int8": return ScriptType.Int8;
                    case "uint8": return ScriptType.UInt8;
                    case "int16": return ScriptType.Int16;
                    case "uint16": return ScriptType.UInt16;
                    case "int": case "int32": return ScriptType.Int32;
                    case "uint": case "uint32": return ScriptType.UInt32;
                    case "int64": return ScriptType.Int64;
                    case "uint64": return ScriptType.UInt64;
                    case "float": return ScriptType.Float;
                    case "double": return ScriptType.Double;
                    default: throw new InvalidOperationException("Unknown oracle return type: " + name);
                }
            }
        }
        public ulong ExpectedRawBits { get { return ulong.Parse(expectedBits, CultureInfo.InvariantCulture); } }
    }

    [Serializable]
    public sealed class ValidationCorpus
    {
        public int schemaVersion, caseCount;
        public string oracle, nativeSourceSha256, recipeSha256;
        public ValidationCase[] cases;
        public static ValidationCorpus Load()
        {
            TextAsset asset = Resources.Load<TextAsset>("AngelScriptValidation/Corpus");
            if (asset == null) throw new InvalidOperationException("Native oracle Corpus.json is missing. Install the validation package and generate its corpus.");
            var corpus = JsonUtility.FromJson<ValidationCorpus>(asset.text);
            if (corpus == null || corpus.schemaVersion != 1 || corpus.cases == null || corpus.caseCount != corpus.cases.Length || corpus.caseCount < 30)
                throw new InvalidOperationException("Invalid or empty native oracle corpus; refusing to report a zero-test success.");
            if (string.IsNullOrEmpty(corpus.nativeSourceSha256) || corpus.nativeSourceSha256.Length != 64 ||
                string.IsNullOrEmpty(corpus.recipeSha256) || corpus.recipeSha256.Length != 64)
                throw new InvalidOperationException("Native corpus provenance is missing.");
            var ids = new HashSet<string>();
            foreach (ValidationCase item in corpus.cases)
            {
                if (item == null || string.IsNullOrEmpty(item.id) || !ids.Add(item.id)) throw new InvalidOperationException("Duplicate/missing corpus ID.");
                if (item.phase != "Current" && item.phase != "Conformance" && item.phase != "Future") throw new InvalidOperationException("Unknown corpus phase: " + item.id);
                if (item.scenario == "Source")
                {
                    if (string.IsNullOrEmpty(item.source) || string.IsNullOrEmpty(item.declaration)) throw new InvalidOperationException("Missing source/entry: " + item.id);
                    ScriptType type = item.ReturnType;
                    if (!ScriptTypes.IsValid(type)) throw new InvalidOperationException("Invalid return type.");
                    if (item.expectedOutcome == "Finished") { ulong bits = item.ExpectedRawBits; }
                    else if (item.expectedOutcome != "CompileRejected" && item.expectedOutcome != "RuntimeFault") throw new InvalidOperationException("Invalid oracle outcome: " + item.id);
                }
                else if (item.scenario != "Capability" || item.expectedOutcome != "Capability") throw new InvalidOperationException("Unknown scenario: " + item.id);
            }
            return corpus;
        }
        public static string AssetHash()
        {
            TextAsset asset = Resources.Load<TextAsset>("AngelScriptValidation/Corpus");
            using (SHA256 hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(asset.text))).Replace("-", "").ToLowerInvariant();
        }
    }

    [Serializable]
    public sealed class ValidationResult
    {
        public string id, title, category, phase, status, expected, observed, detail, source;
        public bool requiredForSubset, burstExecuted;
        public int lanes;
        public double milliseconds;
        public static ValidationResult For(ValidationCase item)
        {
            return new ValidationResult { id = item.id, title = item.title, category = item.category, phase = item.phase,
                requiredForSubset = item.RequiredForSubset, expected = item.expectedOutcome + ":" + item.expectedBits, source = item.source, status = "NotRun" };
        }
    }

    [Serializable]
    public sealed class ValidationEnvironment
    {
        public string unityVersion, platform, backend, processor, buildGuid, utc, nativeSourceSha256, recipeSha256, corpusAssetSha256;
        public bool editor, burstEnabled, safetyChecks, developmentBuild;
        public int jobWorkers, pointerBits, bytecodeVersion;
        public static ValidationEnvironment Capture(ValidationCorpus corpus)
        {
            return new ValidationEnvironment {
                unityVersion = Application.unityVersion, platform = Application.platform.ToString(), processor = SystemInfo.processorType,
                buildGuid = Application.buildGUID, utc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                editor = Application.isEditor, burstEnabled = BurstCompiler.IsEnabled, developmentBuild = Debug.isDebugBuild,
                jobWorkers = JobsUtility.JobWorkerCount, pointerBits = IntPtr.Size * 8, bytecodeVersion = Compilation.CompiledModule.BytecodeVersion,
                nativeSourceSha256 = corpus.nativeSourceSha256, recipeSha256 = corpus.recipeSha256, corpusAssetSha256 = ValidationCorpus.AssetHash(),
#if ENABLE_IL2CPP
                backend = "IL2CPP",
#else
                backend = "Mono/managed",
#endif
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                safetyChecks = true
#else
                safetyChecks = false
#endif
            };
        }
    }

    [Serializable]
    public sealed class ValidationReport
    {
        public int schemaVersion = 1;
        public ValidationEnvironment environment;
        public List<ValidationResult> results = new List<ValidationResult>();
        public bool complete, cancelled, subsetPassed, fullPortPassed, burstVerified;
        public int passed, failed, blocked, requiredCount;
        public string[] plannedCaseIds;
        public string limitation = "Finite selected-case evidence only. Full-port readiness also requires upstream regression migration, native heap/API instrumentation, platform coverage and security/performance review.";
        public void Finish(bool wasCancelled)
        {
            cancelled = wasCancelled; complete = !wasCancelled;
            passed = failed = blocked = requiredCount = 0;
            var executed = new HashSet<string>();
            bool planComplete = plannedCaseIds != null && plannedCaseIds.Length == results.Count;
            foreach (ValidationResult item in results) planComplete &= executed.Add(item.id);
            if (plannedCaseIds != null) foreach (string id in plannedCaseIds) planComplete &= executed.Contains(id);
            subsetPassed = complete && planComplete; fullPortPassed = complete && planComplete; burstVerified = false;
            foreach (ValidationResult item in results)
            {
                if (item.status == "Passed") passed++; else if (item.status == "Blocked") blocked++; else failed++;
                if (item.requiredForSubset) { requiredCount++; subsetPassed &= item.status == "Passed"; }
                fullPortPassed &= item.status == "Passed";
                if (item.id == "integration-burst-kernel") burstVerified = item.status == "Passed" && item.burstExecuted;
            }
            // No zero-test, no managed-fallback success. Future gaps never count as passes.
            subsetPassed &= requiredCount >= 20 && burstVerified;
            fullPortPassed &= subsetPassed && blocked == 0 && failed == 0;
        }
    }

    public sealed class ValidationOptions
    {
        public int Lanes = 32;
        public int InstructionBudget = 20000;
        public int MaximumSlices = 200;
        public bool RequireSafetyChecks = true;
        public static ValidationOptions Quick { get { return new ValidationOptions(); } }
        public void Validate()
        {
            if (Lanes < 2 || Lanes > 1024 || InstructionBudget < 1 || InstructionBudget > 1000000 || MaximumSlices < 1 || MaximumSlices > 10000)
                throw new ArgumentOutOfRangeException("options", "Use 2..1024 lanes, 1..1,000,000 instructions/slice and 1..10,000 slices.");
        }
    }
}
