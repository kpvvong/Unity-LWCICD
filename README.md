# Unity-LWCICD
<img width="1274" height="712" alt="lwcicd_snapshot1" src="https://github.com/user-attachments/assets/b23e5a5e-e722-4c33-a657-46c0dc04d018" />

**Lightweight CI/CD for Unity — automated builds on an idle local PC triggered by Plastic SCM check-ins.**

When a developer merges to the `/build` branch, Plastic SCM fires a webhook to this server. The server runs `cm update`, kicks off a Unity batchmode build, zips the output, and copies it to Google Drive. No cloud VMs, no Jenkins, no paid services.

---

## How it works

```
Developer checks in to /build
  → Plastic SCM cloud fires POST to ngrok URL
    → webhook.js receives payload, identifies project by URL path
      → cm update → Unity -batchmode build → zip → copy to Google Drive
```

- **Multi-project** — one server handles all Unity projects on the machine, routed by `/build-hook/{projectKey}`
- **Per-request config** — add a new project by dropping a JSON file, no server restart needed
- **No external npm packages** — Node.js stdlib only

---

## Repo structure

```
Unity-LWCICD/
├── webhook.js          ← Node HTTP server (multi-project, per-request config load)
├── start.bat           ← Launches ngrok tunnel + webhook server
├── tunnel.bat          ← ngrok http --domain=YOUR_DOMAIN 3000
├── package.json        ← Node package (no dependencies)
├── .gitignore          ← excludes projects/ and temp/
├── projects/           ← gitignored — one JSON per Unity project, auto-generated
│   └── {productKey}.json
├── temp/               ← gitignored — transient zip files, auto-deleted after upload
└── UnityBuildTool/     ← Unity package (add via Package Manager)
    ├── package.json
    └── Editor/
        └── BuildScript.cs
```

---

## Prerequisites

| Tool | Notes |
|------|-------|
| Node.js (LTS) | [nodejs.org](https://nodejs.org) |
| ngrok | [ngrok.com](https://ngrok.com) — free account, one static domain included |
| Unity Hub + Editor | Target version must match the project |
| Plastic SCM CLI | `cm` must be accessible from terminal |
| Google Drive desktop app | Only needed if using the upload feature |

---

## Setup

### A — Build machine (once per machine)

**1. ngrok**

```
ngrok config add-authtoken <YOUR_TOKEN>
```

Edit `tunnel.bat` and set your free static domain:
```bat
ngrok http --domain=your-domain.ngrok-free.dev 3000
```

**2. Clone this repo** as a sibling of your Unity project folders:

```
C:\dev\
├── Unity-LWCICD\       ← this repo
├── MyGame\             ← Unity project
└── AnotherGame\        ← another Unity project
```

The sibling layout is required — `BuildScript.cs` auto-derives the path to `Unity-LWCICD` from the Unity project root.

**3. Start the server**

Double-click `start.bat`. Two windows open:

| Window | Expected output |
|--------|----------------|
| Tunnel | `Forwarding https://your-domain.ngrok-free.dev -> http://localhost:3000` |
| Webhook | `Webhook server listening on http://localhost:3000/build-hook/{projectKey}` |

---

### B — Unity project (once per project)

**1. Add the Unity package**

In Unity: **Package Manager → + → Add package from disk** → select `Unity-LWCICD/UnityBuildTool/package.json`

Unity writes to `Packages/manifest.json`:
```json
"com.kpvv.lwcicd": "file:../../Unity-LWCICD/UnityBuildTool"
```

Commit `Packages/manifest.json` — teammates get the package automatically on next `cm update`.

**2. Create `cicd-config.json`**

**Build Tools → Create CICD Config File** — generates the file in the project root (not version controlled).

Fill in two fields:
```json
{
  "ngrokDomain": "your-domain.ngrok-free.dev",
  "plasticServer": "YOURORG@cloud"
}
```

**3. Generate deployment files**

**Build Tools → Create Deployment Files** — auto-generates:

| File | Location |
|------|----------|
| `{productKey}.json` | `Unity-LWCICD/projects/` |
| `create-webhook.bat` | `Deployment/` |
| `remove-webhook.bat` | `Deployment/` |

**4. Set upload folder**

Open `Unity-LWCICD/projects/{productKey}.json` and set `driveFolder`:
```json
"driveFolder": "G:\\My Drive\\__UnityBuilds"
```

Any cloud storage with a local sync folder works — Google Drive, OneDrive, Dropbox, or a custom symlink. The server just copies the zip to the path; whatever is syncing that folder handles the upload.

Set to `"CONFIGURE_ME"` to skip upload entirely.

**5. Register the Plastic SCM trigger**

From the Unity project root:
```
Deployment\create-webhook.bat
```

Verify it was created:
```
cm tr ls after-checkin --server=YOURORG@cloud
```

Note the position number — you'll need it if you ever want to remove it.

> **Note — trigger fires on every check-in to the repo, not just `/build`.**
> Plastic SCM's `--filter` option does not support branch conditions (`AND branch:/build` syntax was tested and breaks the trigger entirely). Branch filtering is handled by `webhook.js` instead: check-ins on other branches receive a `200 Branch not matched` response and no build runs. The only visible side effect is an error popup on the committing client when the build server is offline. This is a known Plastic SCM limitation with no current workaround.

**6. Verify**

```
curl -X POST http://localhost:3000/build-hook/{productKey} -H "Content-Type: application/json" -d "{\"PLASTIC_FULL_BRANCH_NAME\":\"/build\"}"
```

Expected response: `Build triggered`. Watch the Webhook window for build progress.

---

## Configuration reference

### `cicd-config.json` (Unity project root, not version controlled)

| Field | Description |
|-------|-------------|
| `ngrokDomain` | Your ngrok static domain, e.g. `abc.ngrok-free.dev` |
| `plasticServer` | Plastic SCM server, e.g. `YOURORG@cloud` |

`cicdToolPath` is not required — derived automatically from the project's sibling folder layout.

### `projects/{productKey}.json` (auto-generated, not version controlled)

| Field | Description |
|-------|-------------|
| `projectPath` | Absolute path to the Unity project root |
| `unityExe` | Absolute path to `Unity.exe` |
| `targetBranches` | Array of branch names that trigger a build, e.g. `["/build"]` |
| `driveFolder` | Local sync folder for any cloud storage (Google Drive, OneDrive, Dropbox, or a symlink). The zip is copied here and synced by the desktop client. Set to `CONFIGURE_ME` to skip upload |
| `logFile` | Path to the Unity batchmode log file |
| `debounceMs` | Minimum ms between builds per project (default 1800000 = 30 min) |
| `executeMethod` | CI entry point — `BuildWindowsCI`, `BuildAndroidCI`, `BuildWebGLCI`, and their `*Increment` variants. See version strategy below |

---

## Version strategy

| Scenario | `executeMethod` | Behaviour |
|----------|----------------|-----------|
| Team project — Windows | `BuildScript.BuildWindowsCI` (default) | No version increment — developer bumps version manually before merging to `/build` |
| Solo project — Windows | `BuildScript.BuildWindowsCIIncrement` | Patch version auto-increments on every CI build |
| Team project — Android | `BuildScript.BuildAndroidCI` | No version increment |
| Solo project — Android | `BuildScript.BuildAndroidCIIncrement` | Patch version auto-increments on every CI build |
| Team project — WebGL | `BuildScript.BuildWebGLCI` | No version increment |
| Solo project — WebGL | `BuildScript.BuildWebGLCIIncrement` | Patch version auto-increments on every CI build |

Manual Editor builds (**Ctrl+Shift+W**) always increment the patch version regardless of this setting.

---

## Adding a second project (server already running)

1. Run **Build Tools → Create CICD Config File** in the new project and fill in the two fields
2. Run **Build Tools → Create Deployment Files**
3. Set `driveFolder` in `projects/{productKey}.json`
4. Run `Deployment\create-webhook.bat`

No server restart needed — configs are loaded per-request.

---

## Editor menu reference

| Menu item | Shortcut | Description |
|-----------|----------|-------------|
| Build Tools / Create CICD Config File | — | Generates `cicd-config.json` template in project root |
| Build Tools / Create Deployment Files | — | Generates `projects/{key}.json` and `Deployment/` bat files |
| Build Tools / Build / Windows | Ctrl+Shift+W | Manual Windows build, increments patch version |
| Build Tools / Build / Android | Ctrl+Shift+A | Manual Android build, no version increment |
| Build Tools / Build / WebGL | Ctrl+Shift+G | WebGL build to `Builds/WebGL/{version}/{productName}/` |
