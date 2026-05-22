const http = require('http');
const { exec } = require('child_process');
const fs = require('fs');
const path = require('path');

// ── SERVER CONFIG ────────────────────────────────────────────────────
const PORT = 3000;
// Project configs are loaded per-request from projects/{projectKey}.json
// Add a project: run "Create Deployment Files" in Unity — no restart needed.
// ────────────────────────────────────────────────────────────────────

// Per-project runtime state — persists in memory across config file reloads
const RUNTIME = {};

function getState(projectKey) {
    if (!RUNTIME[projectKey]) {
        RUNTIME[projectKey] = { lastBuildTime: 0, isBuilding: false };
    }
    return RUNTIME[projectKey];
}

function loadConfig(projectKey) {
    const configPath = path.join(__dirname, 'projects', `${projectKey}.json`);
    if (!fs.existsSync(configPath)) return null;
    try {
        return JSON.parse(fs.readFileSync(configPath, 'utf8'));
    } catch (e) {
        console.error(`[${projectKey}] Failed to parse config: ${e.message}`);
        return null;
    }
}

function getLatestBuildVersion(config) {
    const buildBase = path.join(config.projectPath, 'Builds', 'StandaloneWindows64');
    if (!fs.existsSync(buildBase)) return null;
    const dirs = fs.readdirSync(buildBase).filter(f =>
        fs.statSync(path.join(buildBase, f)).isDirectory()
    );
    if (dirs.length === 0) return null;
    dirs.sort((a, b) =>
        fs.statSync(path.join(buildBase, b)).mtimeMs -
        fs.statSync(path.join(buildBase, a)).mtimeMs
    );
    return dirs[0];
}

function zipAndUpload(projectKey, config, version, callback) {
    const buildBase = path.join(config.projectPath, 'Builds', 'StandaloneWindows64');
    const sourcePath = path.join(buildBase, version);

    // Temp zip lives in this tool's own temp/ folder (not the Unity project)
    const tempDir = path.join(__dirname, 'temp');
    if (!fs.existsSync(tempDir)) fs.mkdirSync(tempDir);
    const tempZip = path.join(tempDir, `${projectKey}-${version}.zip`);

    const projectName = path.basename(config.projectPath);
    const destDir = path.join(config.driveFolder, projectName);
    if (!fs.existsSync(destDir)) fs.mkdirSync(destDir, { recursive: true });
    const destZip = path.join(destDir, `${version}.zip`);

    const zipCmd = `powershell -NoProfile -Command "Compress-Archive -Path '${sourcePath}' -DestinationPath '${tempZip}' -Force"`;
    exec(zipCmd, (err) => {
        if (err) {
            console.error(`[${projectKey}] Zip failed: ${err.message}`);
            return callback(err);
        }
        console.log(`[${projectKey}] Zipped: ${path.basename(tempZip)}`);

        fs.copyFile(tempZip, destZip, (cpErr) => {
            if (cpErr) {
                console.error(`[${projectKey}] Copy to Google Drive failed: ${cpErr.message}`);
                return callback(cpErr);
            }
            console.log(`[${projectKey}] Uploaded: ${destZip}`);

            // Clean up temp zip
            fs.unlink(tempZip, (unlinkErr) => {
                if (unlinkErr) console.warn(`[${projectKey}] Could not delete temp zip: ${unlinkErr.message}`);
                else console.log(`[${projectKey}] Temp zip deleted.`);
            });

            callback(null, version);
        });
    });
}

const server = http.createServer((req, res) => {
    if (req.method !== 'POST') {
        res.writeHead(404); res.end('Not found'); return;
    }

    const match = req.url.match(/^\/build-hook\/(.+)$/);
    if (!match) {
        res.writeHead(404); res.end('Not found'); return;
    }

    const projectKey = match[1];
    const config = loadConfig(projectKey);
    if (!config) {
        console.warn(`[${projectKey}] Unknown project or invalid config.`);
        res.writeHead(404); res.end('Unknown project'); return;
    }

    const state = getState(projectKey);

    let body = '';
    req.on('data', chunk => { body += chunk.toString(); });
    req.on('end', () => {
        try {
            const payload = JSON.parse(body);
            console.log(`[${projectKey}] Payload: ` + JSON.stringify(payload));
            const branch = payload.PLASTIC_FULL_BRANCH_NAME;

            if (!config.targetBranches.includes(branch)) {
                console.log(`[${projectKey}] Ignored branch: ${branch}`);
                res.writeHead(200); res.end('Branch not matched'); return;
            }

            const now = Date.now();
            const timeSince = now - state.lastBuildTime;
            if (timeSince < config.debounceMs) {
                const minAgo = Math.floor(timeSince / 60000);
                console.log(`[${projectKey}] Debounced: last build ${minAgo}m ago (min: ${config.debounceMs / 60000}m).`);
                res.writeHead(200); res.end('Debounced'); return;
            }

            if (state.isBuilding) {
                console.log(`[${projectKey}] Build already in progress, skipping.`);
                res.writeHead(200); res.end('Build in progress'); return;
            }

            state.lastBuildTime = now;
            state.isBuilding = true;

            console.log(`[${projectKey}] Branch matched: ${branch}. Triggering build...`);
            res.writeHead(200); res.end('Build triggered');

            const startTime = Date.now();

            exec('cm update', { cwd: config.projectPath }, (err, stdout, stderr) => {
                if (err) {
                    console.error(`[${projectKey}] cm update failed: ` + (stderr || err.message));
                    state.isBuilding = false; return;
                }
                console.log(`[${projectKey}] cm update done.`);

                const unityCmd = `"${config.unityExe}" -batchmode -nographics -waitForAssetImport -quit` +
                    ` -logFile "${config.logFile}" -projectPath "${config.projectPath}"` +
                    ` -executeMethod ${config.executeMethod}`;

                exec(unityCmd, (err2, stdout2, stderr2) => {
                    state.isBuilding = false;
                    if (err2) {
                        console.error(`[${projectKey}] Build failed: ` + (stderr2 || err2.message));
                        return;
                    }
                    const dur = ((Date.now() - startTime) / 1000).toFixed(1);
                    console.log(`[${projectKey}] Build done in ${dur}s.`);

                    const version = getLatestBuildVersion(config);
                    if (!version) {
                        console.error(`[${projectKey}] Could not find build output folder.`);
                        return;
                    }
                    console.log(`[${projectKey}] Version: ${version}`);

                    if (config.driveFolder.startsWith('CONFIGURE')) {
                        console.log(`[${projectKey}] Upload skipped: set driveFolder in projects/${projectKey}.json`);
                        return;
                    }
                    zipAndUpload(projectKey, config, version, (err3) => {
                        if (err3) return;
                        console.log(`[${projectKey}] Pipeline complete: ${version}`);
                    });
                });
            });

        } catch (e) {
            res.writeHead(400); res.end('Invalid JSON');
        }
    });
});

server.listen(PORT, () => {
    console.log(`Webhook server listening on http://localhost:${PORT}/build-hook/{projectKey}`);
    console.log('Project configs loaded per-request from projects/');
});
