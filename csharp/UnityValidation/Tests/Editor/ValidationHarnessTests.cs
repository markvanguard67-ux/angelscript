using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using AngelScript.Burst.Validation.Editor;

namespace AngelScript.Burst.Validation.Tests
{
    public sealed class ValidationHarnessTests
    {
        [Test]
        public void NativeCorpusHasProvenanceAndRealFutureFixtures()
        {
            ValidationCorpus corpus = ValidationCorpus.Load();
            Assert.Greater(corpus.caseCount, 100);
            Assert.AreEqual(64, corpus.nativeSourceSha256.Length);
            var features = new HashSet<string>(); int current = 0, future = 0;
            foreach (ValidationCase item in corpus.cases)
            {
                features.Add(item.feature);
                if (item.phase == "Current") current++;
                if (item.phase == "Future") { future++; Assert.AreNotEqual("CompileRejected", item.expectedOutcome); }
            }
            Assert.Greater(current, 80); Assert.Greater(future, 20);
            foreach (string feature in new[] { "objects", "interfaces", "inheritance", "handles", "gc-cycles", "references", "arrays", "strings", "enums", "namespaces",
                "funcdefs", "module-imports", "bytecode-serialization", "registered-value-types", "gc-accounting", "addon-parity" }) Assert.IsTrue(features.Contains(feature), feature);
        }
        [Test]
        public void EmptyIncompleteCancelledAndBlockedReportsCannotClaimSuccess()
        {
            var empty = new ValidationReport(); empty.Finish(false); Assert.IsFalse(empty.subsetPassed); Assert.IsFalse(empty.fullPortPassed);
            var report = new ValidationReport(); var plan = new List<string>();
            for (int i = 0; i < 25; i++)
            {
                string id = i == 0 ? "integration-burst-kernel" : "current-" + i; plan.Add(id);
                report.results.Add(new ValidationResult { id = id, phase = "Current", requiredForSubset = true, status = "Passed", burstExecuted = i == 0 });
            }
            plan.Add("future-object-heap"); report.plannedCaseIds = plan.ToArray();
            report.Finish(false); Assert.IsFalse(report.subsetPassed, "Incomplete plan must not pass.");
            report.results.Add(new ValidationResult { id = "future-object-heap", phase = "Future", status = "Blocked" });
            report.Finish(false); Assert.IsTrue(report.subsetPassed); Assert.IsFalse(report.fullPortPassed);
            report.results[0].burstExecuted = false; report.Finish(false); Assert.IsFalse(report.subsetPassed, "Managed fallback must not pass.");
            report.results[0].burstExecuted = true; report.Finish(true); Assert.IsFalse(report.subsetPassed); Assert.IsFalse(report.complete);
        }
        [Test]
        public void SceneGeneratorCreatesSavedRunnerAndCamera()
        {
            const string path = "Assets/AngelScriptValidationSceneTest.unity";
            Scene scene = default(Scene);
            try
            {
                scene = ValidationSceneTools.CreateScene(path, false);
                bool runner = false, camera = false;
                foreach (UnityEngine.GameObject root in scene.GetRootGameObjects())
                {
                    runner |= root.GetComponent<ValidationSceneRunner>() != null;
                    camera |= root.GetComponent<UnityEngine.Camera>() != null;
                }
                Assert.IsTrue(runner); Assert.IsTrue(camera); Assert.IsTrue(File.Exists(path));
                Assert.IsFalse(scene.isDirty);
            }
            finally { if (scene.IsValid()) EditorSceneManager.CloseScene(scene, true); AssetDatabase.DeleteAsset(path); }
        }
        [Test]
        public void ReportExportPreservesPlanAndRawBitStrings()
        {
            string path = Path.Combine(Path.GetTempPath(), "angelscript-validation-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var report = new ValidationReport { plannedCaseIds = new[] { "raw" } };
                report.results.Add(new ValidationResult { id = "raw", expected = "UInt64:18446744073709551615", observed = "UInt64:18446744073709551615", status = "Passed" });
                ValidationSceneRunner.WriteReport(report, path);
                var loaded = UnityEngine.JsonUtility.FromJson<ValidationReport>(File.ReadAllText(path));
                Assert.AreEqual(report.results[0].observed, loaded.results[0].observed); Assert.AreEqual("raw", loaded.plannedCaseIds[0]);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }
    }
}
