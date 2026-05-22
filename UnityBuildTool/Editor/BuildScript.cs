#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEditor.Build.Reporting;
using System;
using System.IO;
using System.Linq;

public class BuildMenu {
    [MenuItem("Build Tools/Build/Windows %#w")] // Ctrl+Shift+W
    public static void BuildWindows() {
        BuildScript.PerformBuild(BuildTarget.StandaloneWindows64, incrementVersion: true);
    }

    [MenuItem("Build Tools/Build/Android %#a")] // Ctrl+Shift+A
    public static void BuildAndroid() {
        BuildScript.PerformBuild(BuildTarget.Android, incrementVersion: false); // Skip version bump for device testing
    }

    [MenuItem("Build Tools/Build/WebGL %#g")] // Ctrl+Shift+G
    public static void BuildWebGL() {
        BuildScript.PerformWebGLBuild();
    }
}

public class BuildScript {

    // ── cicd-config.json schema (project root, not version controlled) ──
    [System.Serializable]
    private class CicdConfig {
        public string ngrokDomain;    // e.g. abc.ngrok-free.dev
        public string plasticServer;  // e.g. YOURORG@cloud
    }

    // ── projects/{productKey}.json schema (written to cicd-tool/projects/) ──
    [System.Serializable]
    private class ProjectConfig {
        public string projectPath;
        public string unityExe;
        public string[] targetBranches;
        public string driveFolder;
        public string logFile;
        public int debounceMs;
        public string executeMethod;  // CI entry point — see BuildScript.BuildWindowsCI / BuildWindowsCIIncrement
    }

    [MenuItem("Build Tools/Create CICD Config File")]
    public static void CreateCicdConfigFile() {
        string assetsPath = Application.dataPath;
        string projectRoot = Directory.GetParent(assetsPath).FullName;
        string configPath = Path.Combine(projectRoot, "cicd-config.json");

        if (File.Exists(configPath)) {
            bool overwrite = EditorUtility.DisplayDialog(
                "cicd-config.json already exists",
                $"File found at:\n{configPath}\n\nOverwrite with a blank template?",
                "Overwrite", "Cancel"
            );
            if (!overwrite) return;
        }

        string template =
            "{\n" +
            "  \"ngrokDomain\": \"your-domain.ngrok-free.dev\",\n" +
            "  \"plasticServer\": \"YOURORG@cloud\"\n" +
            "}";

        File.WriteAllText(configPath, template);
        Debug.Log(
            $"✅ cicd-config.json created at:\n{configPath}\n\n" +
            "Fill in both fields:\n" +
            "  ngrokDomain   — your ngrok static domain (e.g. abc.ngrok-free.dev)\n" +
            "  plasticServer — your Plastic SCM server (e.g. YOURORG@cloud)\n\n" +
            "Then run Build Tools / Create Deployment Files."
        );
    }

    [MenuItem("Build Tools/Create Deployment Files")]
    public static void CreateDeploymentFiles() {
        string assetsPath = Application.dataPath;
        string projectRoot = Directory.GetParent(assetsPath).FullName;

        // Read cicd-config.json from project root (not version controlled)
        string cicdConfigPath = Path.Combine(projectRoot, "cicd-config.json");
        if (!File.Exists(cicdConfigPath)) {
            Debug.LogError(
                $"cicd-config.json not found at:\n{cicdConfigPath}\n\n" +
                "Run Build Tools / Create CICD Config File first, then edit the values."
            );
            return;
        }

        CicdConfig cicdConfig = JsonUtility.FromJson<CicdConfig>(File.ReadAllText(cicdConfigPath));

        // Unity-LWCICD is always a sibling of Unity project folders — derive path directly
        string cicdToolPath = Path.Combine(Directory.GetParent(projectRoot).FullName, "Unity-LWCICD");

        string productName = PlayerSettings.productName;
        string productKey = productName.ToLower().Replace(" ", "-");
        string unityVersion = Application.unityVersion;

        // Deployment/ — per-project runner files, not version controlled
        string deploymentFolder = Path.Combine(projectRoot, "Deployment");
        string buildLogFolder = Path.Combine(deploymentFolder, "BuildLog");
        if (!Directory.Exists(buildLogFolder)) Directory.CreateDirectory(buildLogFolder);
        string logFile = Path.Combine(buildLogFolder, "build_log.txt");

        // Write projects/{productKey}.json into the cicd-tool folder
        string projectsFolder = Path.Combine(cicdToolPath, "projects");
        if (!Directory.Exists(projectsFolder)) Directory.CreateDirectory(projectsFolder);

        var projectConfig = new ProjectConfig {
            projectPath = projectRoot,
            unityExe = $"C:\\Program Files\\Unity\\Hub\\Editor\\{unityVersion}\\Editor\\Unity.exe",
            targetBranches = new[] { "/build" },
            driveFolder = "CONFIGURE_ME",
            logFile = logFile,
            debounceMs = 1800000,
            executeMethod = "BuildScript.BuildWindowsCI"  // swap to BuildWindowsCIIncrement for solo projects
        };
        string projectConfigPath = Path.Combine(projectsFolder, $"{productKey}.json");
        File.WriteAllText(projectConfigPath, JsonUtility.ToJson(projectConfig, true));

        // Write Deployment/create-webhook.bat and remove-webhook.bat
        string ngrokDomain = string.IsNullOrEmpty(cicdConfig.ngrokDomain) ? "CONFIGURE_NGROK_DOMAIN" : cicdConfig.ngrokDomain;
        string plasticServer = string.IsNullOrEmpty(cicdConfig.plasticServer) ? "YOURORG@cloud" : cicdConfig.plasticServer;
        string webhookUrl = $"https://{ngrokDomain}/build-hook/{productKey}";

        // Note: Plastic SCM --filter does not support branch conditions (AND branch:/build breaks the trigger).
        // Branch filtering is handled by webhook.js — non-build check-ins return 200 without triggering a build.
        string createWebhookContent =
            "@echo off\r\n" +
            $"cm tr ls after-checkin --server={plasticServer} | findstr /i \"{productKey}-deploy-trigger\" >nul 2>&1\r\n" +
            "if %errorlevel% == 0 (\r\n" +
            $"    echo [SKIP] Trigger \"{productKey}-deploy-trigger\" already exists.\r\n" +
            "    echo        Run remove-webhook.bat first to replace it.\r\n" +
            ") else (\r\n" +
            $"    cm tr create after-checkin \"{productKey}-deploy-trigger\" \"webtrigger {webhookUrl}\" --server={plasticServer} --filter=\"rep:{productName}\"\r\n" +
            "    echo [OK] Trigger created.\r\n" +
            $"    echo      Run 'cm tr ls after-checkin --server={plasticServer}' to verify.\r\n" +
            ")\r\n" +
            "@REM NOTE: Plastic SCM does not support branch conditions in --filter (AND branch:/build breaks the trigger).\r\n" +
            "@REM       Branch filtering is handled by webhook.js — non-build check-ins return 200 Branch not matched, no build runs.";

        string removeWebhookContent =
            "@echo off\r\n" +
            $"for /f \"tokens=1\" %%i in ('cm tr ls after-checkin --server={plasticServer} 2^>nul ^| findstr /i \"{productKey}-deploy-trigger\"') do set TRIGGER_POS=%%i\r\n" +
            "if not defined TRIGGER_POS (\r\n" +
            $"    echo Trigger \"{productKey}-deploy-trigger\" not found.\r\n" +
            "    exit /b 1\r\n" +
            ")\r\n" +
            "echo Removing trigger at position %TRIGGER_POS%...\r\n" +
            $"cm tr rm after-checkin %TRIGGER_POS% --server={plasticServer}\r\n" +
            "echo [OK] Trigger removed.";

        File.WriteAllText(Path.Combine(deploymentFolder, "create-webhook.bat"), createWebhookContent);
        File.WriteAllText(Path.Combine(deploymentFolder, "remove-webhook.bat"), removeWebhookContent);

        Debug.Log($"✅ Project config: {projectConfigPath}");
        Debug.Log($"✅ Deployment files: {deploymentFolder}");
        Debug.Log($"⚠️  Set 'driveFolder' in {productKey}.json before first run.");
    }

    public static void PerformBuild(BuildTarget target, bool incrementVersion) {
        string productName = PlayerSettings.productName;
        string version = PlayerSettings.bundleVersion;

        // Parse and optionally increment patch version
        string[] parts = version.Split('.');
        if (parts.Length != 3) {
            Debug.LogError("Version must be in format Major.Minor.Patch (e.g., 1.0.0)");
            EditorApplication.Exit(1);
            return;
        }

        if (incrementVersion) {
            int patch = int.Parse(parts[2]) + 1;
            version = $"{parts[0]}.{parts[1]}.{patch}";
            PlayerSettings.bundleVersion = version;
        }

        // Define build path and filename
        string buildFolder = $"Builds/{target}/{version}";
        string fileName = target == BuildTarget.Android
            ? $"{productName}.apk"
            : $"{productName}.exe";
        string fullPath = Path.Combine(buildFolder, fileName);

        if (!Directory.Exists(buildFolder)) {
            Directory.CreateDirectory(buildFolder);
        }

        // Use scenes from Build Settings
        string[] scenes = EditorBuildSettings.scenes
            .Where(s => s.enabled)
            .Select(s => s.path)
            .ToArray();

        // Run the build
        BuildReport report = BuildPipeline.BuildPlayer(
            scenes,
            fullPath,
            target,
            BuildOptions.None
        );

        if (report.summary.result == BuildResult.Succeeded) {
            Debug.Log($"✅ Build succeeded: {fullPath}");
            Debug.Log($"📦 Version: {version} | Target: {target}");
        } else {
            Debug.LogError($"❌ Build failed: {report.summary.result}");
            EditorApplication.Exit(1);
        }
    }

    public static void PerformWebGLBuild() {
        string productName = PlayerSettings.productName;
        string version = PlayerSettings.bundleVersion;

        // Use productName as last segment → Unity names Build/ files after it
        // e.g. Builds/WebGL/0.1.5/MyGame/ → MyGame.loader.js, MyGame.data, etc.
        // File names stay stable across version bumps; only the parent folder changes.
        string buildFolder = $"Builds/WebGL/{version}/{productName}";

        if (!Directory.Exists(buildFolder)) {
            Directory.CreateDirectory(buildFolder);
        }

        string[] scenes = EditorBuildSettings.scenes
            .Where(s => s.enabled)
            .Select(s => s.path)
            .ToArray();

        BuildReport report = BuildPipeline.BuildPlayer(
            scenes,
            buildFolder,
            BuildTarget.WebGL,
            BuildOptions.None
        );

        if (report.summary.result == BuildResult.Succeeded) {
            string builtAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            string versionJson = $"{{\"version\":\"{version}\",\"product\":\"{productName}\",\"builtAt\":\"{builtAt}\"}}";
            File.WriteAllText(Path.Combine(buildFolder, "version.json"), versionJson);
            Debug.Log($"✅ WebGL build succeeded: {buildFolder}");
            Debug.Log($"📦 Version: {version}");
        } else {
            Debug.LogError($"❌ WebGL build failed: {report.summary.result}");
            EditorApplication.Exit(1);
        }
    }

    // ── CI entry points — called by webhook.js via -executeMethod ──────────
    // Team projects:  set executeMethod = "BuildScript.BuildWindowsCI"
    // Solo projects:  set executeMethod = "BuildScript.BuildWindowsCIIncrement"

    public static void BuildWindowsCI() {
        PerformBuild(BuildTarget.StandaloneWindows64, incrementVersion: false);
    }

    public static void BuildWindowsCIIncrement() {
        PerformBuild(BuildTarget.StandaloneWindows64, incrementVersion: true);
    }
}
#endif
