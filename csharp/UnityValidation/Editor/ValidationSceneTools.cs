using System;
using System.Collections;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AngelScript.Burst.Validation.Editor
{
    public static class ValidationSceneTools
    {
        public const string ScenePath = "Assets/Scenes/AngelScriptValidation.unity";
        [MenuItem("AngelScript/Validation/Create and open test scene")]
        public static void CreateAndOpen()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            CreateScene(ScenePath, true);
        }
        public static Scene CreateScene(string path, bool open)
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, open ? NewSceneMode.Single : NewSceneMode.Additive);
            scene.name = "AngelScript Validation";
            GameObject root = new GameObject("AngelScript Validation Laboratory"); SceneManager.MoveGameObjectToScene(root, scene);
            root.AddComponent<ValidationSceneRunner>();
            var cameraObject = new GameObject("Validation Camera"); SceneManager.MoveGameObjectToScene(cameraObject, scene);
            cameraObject.tag = "MainCamera"; Camera camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(0.035f, 0.055f, 0.085f);
            cameraObject.transform.position = new Vector3(0, 8, -11); cameraObject.transform.LookAt(new Vector3(0, 0, 3));
            var lightObject = new GameObject("Validation Light"); SceneManager.MoveGameObjectToScene(lightObject, scene);
            Light light = lightObject.AddComponent<Light>(); light.type = LightType.Directional; light.intensity = 1.2f;
            lightObject.transform.rotation = Quaternion.Euler(50, -30, 0);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            if (!EditorSceneManager.SaveScene(scene, path)) throw new InvalidOperationException("Could not save validation scene: " + path);
            AssetDatabase.Refresh(); if (open) Selection.activeGameObject = root;
            return scene;
        }
        [MenuItem("AngelScript/Validation/Run checks in Editor and export JSON")]
        public static void RunEditor()
        {
            var report = new ValidationReport(); IEnumerator run = ValidationSuite.Run(ValidationOptions.Quick, report);
            while (run.MoveNext()) { } // Each case is bounded; all native buffers are disposed before each yield.
            var disposable = run as IDisposable; if (disposable != null) disposable.Dispose();
            string path = Path.GetFullPath("TestResults/angelscript-validation-editor.json"); ValidationSceneRunner.WriteReport(report, path);
            Debug.Log("Validation JSON: " + path + "; subset=" + report.subsetPassed + "; full-port=" + report.fullPortPassed);
        }
        // Command-line entry points. Exceptions/failures are nonzero, never a zero-test success.
        public static void CreateSceneBatch()
        {
            try { CreateScene(ScenePath, true); EditorApplication.Exit(0); }
            catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
        }
        public static void RunBatch()
        {
            int exit = 1;
            try
            {
                var report = new ValidationReport(); IEnumerator run = ValidationSuite.Run(ValidationOptions.Quick, report);
                while (run.MoveNext()) { }
                var disposable = run as IDisposable; if (disposable != null) disposable.Dispose();
                string output = Argument("--angelscript-report") ?? "TestResults/angelscript-validation-editor.json";
                ValidationSceneRunner.WriteReport(report, output);
                bool full = Array.IndexOf(Environment.GetCommandLineArgs(), "--angelscript-full-port") >= 0;
                exit = (full ? report.fullPortPassed : report.subsetPassed) ? 0 : 1;
            }
            catch (Exception error) { Debug.LogException(error); }
            EditorApplication.Exit(exit);
        }
        [MenuItem("AngelScript/Validation/Build IL2CPP validation player")]
        public static void BuildPlayerMenu()
        {
            BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
            if (target != BuildTarget.StandaloneWindows64 && target != BuildTarget.StandaloneLinux64 && target != BuildTarget.StandaloneOSX)
            { EditorUtility.DisplayDialog("Validation player", "Choose a standalone target with its IL2CPP module installed. WebGL/mobile need target-specific runners.", "OK"); return; }
            string extension = target == BuildTarget.StandaloneWindows64 ? "exe" : target == BuildTarget.StandaloneOSX ? "app" : "";
            string output = EditorUtility.SaveFilePanel("Build validation player", "Builds", "AngelScriptValidation", extension);
            if (string.IsNullOrEmpty(output)) return;
            BuildValidationPlayer(output, target);
        }
        public static BuildReport BuildValidationPlayer(string output, BuildTarget target)
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) throw new OperationCanceledException("Scene save cancelled.");
            if (!File.Exists(ScenePath)) { Scene created = CreateScene(ScenePath, false); EditorSceneManager.CloseScene(created, true); }
            BuildTargetGroup group = BuildPipeline.GetBuildTargetGroup(target);
            ScriptingImplementation previous = PlayerSettings.GetScriptingBackend(group);
            try
            {
                PlayerSettings.SetScriptingBackend(group, ScriptingImplementation.IL2CPP);
                var options = new BuildPlayerOptions { scenes = new[] { ScenePath }, locationPathName = output, target = target,
                    options = BuildOptions.Development | BuildOptions.AllowDebugging };
                BuildReport report = BuildPipeline.BuildPlayer(options);
                if (report.summary.result != BuildResult.Succeeded) throw new BuildFailedException("Validation player build failed: " + report.summary.result);
                Debug.Log("IL2CPP validation player built: " + output + ". Run with --angelscript-validation --angelscript-report <path>. Building is not execution evidence.");
                return report;
            }
            finally { PlayerSettings.SetScriptingBackend(group, previous); }
        }
        public static void BuildPlayerBatch()
        {
            try
            {
                string output = Argument("--angelscript-player");
                if (string.IsNullOrEmpty(output)) throw new ArgumentException("Pass --angelscript-player <output executable/app path>.");
                BuildValidationPlayer(output, EditorUserBuildSettings.activeBuildTarget); EditorApplication.Exit(0);
            }
            catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
        }
        private static string Argument(string key)
        { string[] args = Environment.GetCommandLineArgs(); int index = Array.IndexOf(args, key); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
    }
}
