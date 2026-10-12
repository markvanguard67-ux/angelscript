using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace AngelScript.Burst.Validation.Tests
{
    public sealed class ValidationSceneTests
    {
        [UnityTest]
        public IEnumerator SceneRunsRealSupportedChecksAndReportsFullPortBlockers()
        {
            var root = new GameObject("Validation PlayMode test");
            try
            {
                var runner = root.AddComponent<ValidationSceneRunner>(); runner.ConfigureForTests(8, false);
                yield return null; runner.Begin();
                float deadline = Time.realtimeSinceStartup + 180;
                while (runner.Running && Time.realtimeSinceStartup < deadline) yield return null;
                Assert.IsFalse(runner.Running, "Validation scene timed out.");
                Assert.IsNotNull(runner.Report); Assert.IsTrue(runner.Report.complete);
                Assert.IsTrue(runner.Report.subsetPassed, "Required subset/Burst/job/lifecycle checks failed. Inspect report: " + runner.ReportPath);
                Assert.IsTrue(runner.Report.burstVerified); Assert.Greater(runner.Report.requiredCount, 80);
                Assert.AreEqual(runner.Report.plannedCaseIds.Length, runner.Report.results.Count);
                // Known gaps may disappear during the full conversion; never require a permanent failure.
                if (!runner.Report.fullPortPassed) Assert.Greater(runner.Report.blocked + runner.Report.failed, 0);
            }
            finally { Object.Destroy(root); }
        }
        [UnityTest]
        public IEnumerator CancelledSceneDoesNotClaimCorrectness()
        {
            var root = new GameObject("Validation cancellation test");
            try
            {
                var runner = root.AddComponent<ValidationSceneRunner>(); runner.ConfigureForTests(4, false);
                yield return null; runner.Begin(); runner.Cancel();
                for (int i = 0; i < 10 && runner.Running; i++) yield return null;
                Assert.IsFalse(runner.Running); Assert.IsTrue(runner.Report.cancelled);
                Assert.IsFalse(runner.Report.subsetPassed); Assert.IsFalse(runner.Report.fullPortPassed);
            }
            finally { Object.Destroy(root); }
        }
    }
}
