using System;
using System.IO;
using LightNet;
using LightNet.Edgegap.Sample;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace LightNet.Edgegap.Editor
{
    /// <summary>
    /// Builds the relay sample. From the menu (Edgegap > Relay Sample) or the command line:<br/>
    /// Unity -batchmode -quit -projectPath . -executeMethod LightNet.Edgegap.Editor.RelaySampleBuild.BuildWindows<br/>
    /// (also BuildLinux, BuildMac). Output goes to Builds/&lt;platform&gt;/.
    /// </summary>
    public static class RelaySampleBuild
    {
        public const string ScenePath = "Assets/Edgegap/Sample/RelaySample.unity";

        [MenuItem("Edgegap/Relay Sample/Create Scene")]
        public static void CreateScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            GameObject camera = GameObject.FindWithTag("MainCamera");
            camera.transform.SetPositionAndRotation(new Vector3(0f, 8f, -8f), Quaternion.Euler(45f, 0f, 0f));

            GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";

            GameObject network = new GameObject("Network");
            network.AddComponent<NetworkComponent>();
            network.AddComponent<RelaySample>();

            EditorSceneManager.SaveScene(scene, ScenePath);
            AddToBuildSettings();
            Debug.Log("Created " + ScenePath);
        }

        [MenuItem("Edgegap/Relay Sample/Build Windows")]
        public static void BuildWindows()
        {
            Build(BuildTarget.StandaloneWindows64, "Builds/Windows/RelaySample.exe");
        }

        [MenuItem("Edgegap/Relay Sample/Build Linux")]
        public static void BuildLinux()
        {
            Build(BuildTarget.StandaloneLinux64, "Builds/Linux/RelaySample.x86_64");
        }

        [MenuItem("Edgegap/Relay Sample/Build macOS")]
        public static void BuildMac()
        {
            Build(BuildTarget.StandaloneOSX, "Builds/macOS/RelaySample.app");
        }

        static void Build(BuildTarget target, string output)
        {
            if (!File.Exists(ScenePath))
            {
                CreateScene();
            }
            AddToBuildSettings();

            var options = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = output,
                target = target,
                options = BuildOptions.None
            };
            var report = BuildPipeline.BuildPlayer(options);
            Debug.Log($"Build {target}: {report.summary.result}, {report.summary.totalSize / (1024 * 1024)} MB -> {output}");
            if (Application.isBatchMode && report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
            {
                EditorApplication.Exit(1);
            }
        }

        static void AddToBuildSettings()
        {
            var scenes = EditorBuildSettings.scenes;
            if (Array.Exists(scenes, s => s.path == ScenePath))
            {
                return;
            }
            var list = new EditorBuildSettingsScene[scenes.Length + 1];
            list[0] = new EditorBuildSettingsScene(ScenePath, true);
            Array.Copy(scenes, 0, list, 1, scenes.Length);
            EditorBuildSettings.scenes = list;
        }
    }
}
