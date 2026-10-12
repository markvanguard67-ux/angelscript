using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace AngelScript.Burst.Validation
{
    /// <summary>Attach to the generated/checked-in validation scene. Runs the real suite, never simulates Burst.</summary>
    public sealed class ValidationSceneRunner : MonoBehaviour
    {
        [SerializeField] private bool runOnStart = true;
        [SerializeField] private int lanes = 32;
        [SerializeField] private int instructionBudget = 20000;
        [SerializeField] private bool requireSafetyChecks = true;
        [SerializeField] private bool createLaneVisualization = true;
        public ValidationReport Report { get; private set; }
        public bool Running { get; private set; }
        public string ReportPath { get; private set; }
        public event Action<ValidationReport> Completed;
        private bool cancellation, quitWhenDone, requireFullPort;
        private Vector2 scroll, detailsScroll;
        private int selected = -1;
        private readonly List<GameObject> laneObjects = new List<GameObject>();
        private string failure = "", lastCase = "Ready";
        private GUIStyle heading, body;

        private void Start()
        {
            string[] args = Environment.GetCommandLineArgs();
            quitWhenDone = Has(args, "--angelscript-validation");
            requireFullPort = Has(args, "--angelscript-full-port");
            if (Has(args, "--angelscript-release-checks")) requireSafetyChecks = false;
            string explicitReport = Argument(args, "--angelscript-report");
            if (!string.IsNullOrEmpty(explicitReport)) ReportPath = Path.GetFullPath(explicitReport);
            else if (string.IsNullOrEmpty(ReportPath)) ReportPath = Path.Combine(Application.persistentDataPath, "AngelScriptValidation", "latest.json");
            if (runOnStart || quitWhenDone) Begin();
        }
        public void ConfigureForTests(int laneCount = 8, bool visualize = false)
        {
            lanes = laneCount; runOnStart = false; createLaneVisualization = visualize;
            ReportPath = Path.Combine(Application.persistentDataPath, "AngelScriptValidation", "Tests", Guid.NewGuid().ToString("N") + ".json");
        }
        public void Begin()
        {
            if (Running) return;
            if (string.IsNullOrEmpty(ReportPath)) ReportPath = Path.Combine(Application.persistentDataPath, "AngelScriptValidation", "latest.json");
            failure = ""; selected = -1; cancellation = false; Report = new ValidationReport(); Running = true;
            StartCoroutine(Run());
        }
        public void Cancel() { cancellation = true; }
        private IEnumerator Run()
        {
            var options = new ValidationOptions { Lanes = lanes, InstructionBudget = instructionBudget, RequireSafetyChecks = requireSafetyChecks };
            IEnumerator suite = ValidationSuite.Run(options, Report, result => lastCase = result.title, () => cancellation, Visualize);
            while (true)
            {
                bool more;
                try { more = suite.MoveNext(); }
                catch (Exception error)
                {
                    failure = error.ToString();
                    Report.results.Add(new ValidationResult { id = "harness-initialization", title = "Harness initialization", category = "Harness", phase = "Current",
                        requiredForSubset = true, status = "Failed", detail = failure });
                    Report.Finish(false); break;
                }
                if (!more) break;
                yield return suite.Current;
            }
            var disposable = suite as IDisposable; if (disposable != null) disposable.Dispose();
            Running = false;
            try { WriteReport(Report, ReportPath); }
            catch (Exception error) { failure = "Could not export report: " + error; Report.subsetPassed = Report.fullPortPassed = false; }
            Debug.Log("AngelScript validation: subset=" + Report.subsetPassed + ", full-port=" + Report.fullPortPassed + ", passed=" + Report.passed +
                ", failed=" + Report.failed + ", blocked=" + Report.blocked + ". Report: " + ReportPath, this);
            if (Completed != null) Completed(Report);
            // Used only by a standalone validation player. Editor automation uses test-runner XML instead.
            if (quitWhenDone && !Application.isEditor) Application.Quit((requireFullPort ? Report.fullPortPassed : Report.subsetPassed) ? 0 : 1);
        }
        public static void WriteReport(ValidationReport report, string path)
        {
            string full = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(full));
            string temporary = full + ".tmp";
            File.WriteAllText(temporary, JsonUtility.ToJson(report, true), new UTF8Encoding(false));
            if (File.Exists(full)) File.Delete(full);
            File.Move(temporary, full);
        }
        private void OnDestroy()
        {
            cancellation = true;
            // Suite methods finish/dispose their native data before yielding; no job resources are abandoned here.
            if (Running && Report != null) { Report.Finish(true); Running = false; }
        }
        private void Visualize(float[] heights)
        {
            if (!createLaneVisualization) return;
            foreach (GameObject value in laneObjects) if (value != null) Destroy(value);
            laneObjects.Clear(); int count = Math.Min(heights.Length, 64);
            for (int i = 0; i < count; i++)
            {
                GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube); cube.name = "VM lane " + i;
                cube.transform.SetParent(transform, false);
                cube.transform.localPosition = new Vector3((i % 8 - 3.5f) * 1.15f, heights[i] * 0.5f, i / 8 * 1.15f);
                cube.transform.localScale = new Vector3(0.75f, heights[i], 0.75f);
                Collider collider = cube.GetComponent<Collider>(); if (collider != null) Destroy(collider);
                laneObjects.Add(cube);
            }
        }
        private void OnGUI()
        {
            if (heading == null) { heading = new GUIStyle(GUI.skin.label) { fontSize = 23, fontStyle = FontStyle.Bold, wordWrap = true }; body = new GUIStyle(GUI.skin.label) { wordWrap = true }; }
            float width = Mathf.Min(Screen.width - 24, 970); float height = Mathf.Max(180, Screen.height - 24);
            GUILayout.BeginArea(new Rect(12, 12, width, height), GUI.skin.box);
            GUILayout.Label("ANGELSCRIPT  /  VALIDATION LAB", heading);
            GUILayout.Label("Real C++ oracle · managed core · Burst jobs · native ownership · future-port blockers", body);
            GUILayout.BeginHorizontal();
            GUI.enabled = !Running; if (GUILayout.Button("Run all checks", GUILayout.Width(150))) Begin();
            GUI.enabled = Running; if (GUILayout.Button("Cancel", GUILayout.Width(85))) Cancel(); GUI.enabled = true;
            if (!Running && Report != null && GUILayout.Button("Export JSON", GUILayout.Width(110)))
            { try { WriteReport(Report, ReportPath); } catch (Exception error) { failure = error.ToString(); } }
            GUILayout.EndHorizontal();
            GUILayout.Label(Running ? "RUNNING  " + lastCase : Report == null ? "Ready. Press Run all checks." :
                "SUBSET: " + (Report.subsetPassed ? "PASS" : "NOT VERIFIED") + "    FULL PORT: " + (Report.fullPortPassed ? "SELECTED GATES PASS" : "NOT READY") +
                "    " + Report.passed + " passed / " + Report.failed + " failed / " + Report.blocked + " blocked", heading);
            GUILayout.Label("Unsupported features are BLOCKED, never successful rejection tests. Managed fallback fails Burst checks. A finite scene run is not a full conformance certificate.", body);
            if (Report != null && Report.environment != null) GUILayout.Label(Report.environment.unityVersion + " · " + Report.environment.platform + " · " + Report.environment.backend +
                " · Burst=" + Report.environment.burstEnabled + " · Safety=" + Report.environment.safetyChecks + " · Workers=" + Report.environment.jobWorkers, body);
            if (!string.IsNullOrEmpty(failure)) GUILayout.Label(failure, body);
            GUILayout.Label("Report: " + ReportPath, body);
            scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(Mathf.Max(100, height * 0.42f)));
            if (Report != null) for (int i = 0; i < Report.results.Count; i++)
            {
                ValidationResult result = Report.results[i];
                GUI.contentColor = result.status == "Passed" ? new Color(0.55f, 1, 0.7f) : result.status == "Blocked" ? new Color(1, 0.8f, 0.35f) : new Color(1, 0.5f, 0.45f);
                if (GUILayout.Button(result.status.ToUpperInvariant() + "  [" + result.phase + "] " + result.title + "  (" + result.milliseconds.ToString("F1") + " ms)")) selected = i;
            }
            GUI.contentColor = Color.white; GUILayout.EndScrollView();
            detailsScroll = GUILayout.BeginScrollView(detailsScroll);
            if (Report != null && selected >= 0 && selected < Report.results.Count)
            {
                ValidationResult result = Report.results[selected];
                GUILayout.Label(result.id + " / " + result.category, heading);
                GUILayout.Label("Expected: " + result.expected + "\nObserved: " + result.observed + "\n" + result.detail, body);
                if (!string.IsNullOrEmpty(result.source)) GUILayout.TextArea(result.source);
            }
            else GUILayout.Label("Select a result to inspect exact expectations, diagnostics, raw return bits and repro source. Lane bars behind this panel are outputs from the repeatability job.", body);
            GUILayout.EndScrollView(); GUILayout.EndArea();
        }
        private static bool Has(string[] args, string value) { return Array.IndexOf(args, value) >= 0; }
        private static string Argument(string[] args, string value) { int i = Array.IndexOf(args, value); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
    }
}
