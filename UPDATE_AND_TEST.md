# Update Grimarr and test recovery

These instructions update Grimarr only. Keep the existing Compose configuration, config mount, media mounts, user/group, port, and service credentials. The code changes are committed locally; `git pull` on a server will only receive them after they have been pushed to that server's Git remote. A source archive can be transferred directly instead.

## 1. Prepare an update and keep the old image

In Grimarr, pause automation under Settings → Profiles. Do this before upgrading so the first checks do not start transfers or imports. Existing qBittorrent transfers continue independently.

The commands below use the previous alpha deployment's container name `grimarr-alpha` and Compose service `grimarr`. For a standard repository deployment the container is `grimarr`. Check the existing deployment before choosing the names and Compose file:

```bash
docker inspect grimarr-alpha --format '{{ index .Config.Labels "com.docker.compose.project.working_dir" }}'
docker inspect grimarr-alpha --format '{{ index .Config.Labels "com.docker.compose.project.config_files" }}'
docker inspect grimarr-alpha --format '{{ index .Config.Labels "com.docker.compose.service" }}'
```

Change into the reported deployment directory. The alpha examples below use `compose.yaml`; the repository's default file is `compose.yml`. Preserve any existing additional Compose files in subsequent commands.

Keep a local tag for the currently running image:

```bash
docker image tag "$(docker inspect --format '{{.Image}}' grimarr-alpha)" grimarr-local:before-sqlite-recovery
```

Transfer the supplied `grimarr-update.tar.gz` source archive into this deployment directory using WinSCP. Extract into a new source directory, leaving the old source intact, and build while the current version remains available:

```bash
mkdir source-sqlite-recovery
tar -xzf grimarr-update.tar.gz -C source-sqlite-recovery
docker build -t grimarr-local:sqlite-recovery ./source-sqlite-recovery
```

If the new directory already exists, choose another fresh directory and update the build command. A build failure leaves the current deployment in place.

## 2. Back up the complete config and switch images

Run this in the deployment directory. It locates the current `/config` bind mount, stops Grimarr, and archives the database plus its encryption keys. Backups contain private credentials and should stay private.

```bash
umask 077
GRIMARR_CONFIG_PATH=$(docker inspect --format '{{range .Mounts}}{{if eq .Destination "/config"}}{{.Source}}{{end}}{{end}}' grimarr-alpha)
test -n "$GRIMARR_CONFIG_PATH" && test -d "$GRIMARR_CONFIG_PATH"
# Continue only if the previous command succeeds and this is the expected config directory.
docker compose -f compose.yaml stop grimarr
sudo tar -czf "config-before-sqlite-$(date +%Y%m%d-%H%M%S).tar.gz" -C "$GRIMARR_CONFIG_PATH" .
```

Continue only after a successful backup. Do not replace your existing Compose file with the repository default: the custom alpha deployment uses its own paths and port.

Create a small override file that selects the new image while retaining the existing settings:

```bash
cat > sqlite-recovery.override.yml <<'YAML'
services:
  grimarr:
    image: grimarr-local:sqlite-recovery
YAML
docker compose -f compose.yaml -f sqlite-recovery.override.yml up -d --no-build grimarr
docker compose -f compose.yaml -f sqlite-recovery.override.yml ps
docker compose -f compose.yaml -f sqlite-recovery.override.yml logs --tail=100 grimarr
```

The `--no-build` flag selects the image built above even if the original Compose file has a different build context. Retain this override in future Compose commands until you intentionally change how the deployment is built.

## 3. Check the updated app

Open your existing Grimarr address and hard-refresh the browser. On the earlier alpha setup the port was 8788; use the actual address already configured on your server.

Confirm that:

- Existing books, preferences, paths, and selected Audiobookshelf library are intact.
- All three connection tests pass.
- Settings → Profiles shows **Automatically replace failed downloads** and **Stall timeout (minutes)**.
- Automatic replacement defaults to off; a previously saved value persists. Existing automation settings also persist, so keep automation paused until you choose a test book.
- Logs show successful startup and the container becomes healthy.

The SQLite update requires no manual database migration. Native SQLite was verified as 3.53.3 in local tests; the old vulnerable SQLitePCLRaw 2.1.11 is no longer in the resolved package graph.

## 4. Exercise recovery using a disposable test book

Use the isolated test download folder and Audiobookshelf test library. Keep automatic replacement off first, and enable automation when ready. Worker checks normally run every 15 seconds.

| Check | Action | Expected result |
| --- | --- | --- |
| Restart recovery | Restart only Grimarr during a test download: `docker compose -f compose.yaml -f sqlite-recovery.override.yml restart grimarr`. | Same book and torrent continue; no duplicate torrent is submitted. |
| Missing torrent | Remove the test torrent from qBittorrent **without deleting its files**. | After the next poll and a two-minute confirmation window, Grimarr shows **Download failed** and a blocked release in its history. |
| Retry same release | Click **Retry this release** on the failed book. | The block is lifted, the same torrent is resubmitted if missing, and existing files remain. If the torrent already exists, Grimarr reconnects without submitting another one. |
| Replacement | Make the test torrent missing again, wait for failure, then click **Search for replacement**. | The failed release remains blocked. Another eligible release is selected, or the book stays monitored if none exist. With manual selection on, it waits for your choice. |
| Automatic replacement | Turn off manual selection, enable **Automatically replace failed downloads**, then repeat the missing-torrent test. | A confirmed failure triggers a replacement search without a click. The old files remain. No eligible alternatives means the book stays monitored. |
| Pause protection | Pause monitoring on the test book, or pause/stop its torrent in qBittorrent. | Paused monitoring prevents Grimarr from advancing it. A paused/stopped torrent is excluded from stall detection; Grimarr's pause control does not pause qBittorrent. |
| Stall timeout | Set the timeout to 15 minutes and observe an actively tracked, incomplete test torrent that makes no progress. | It fails after 15 minutes without progress, then follows your replacement setting. Paused/queued/checking/moving/allocating states do not trigger this timeout. A progressing torrent should stay active. |
| Connection outage | Temporarily give Grimarr an unreachable qBittorrent URL. Restore the correct URL afterward and click **Retry now**. | A connection error retains the selected release and does not block or replace it. Tracking resumes after the connection is restored. Do not stop a shared download service to test this. |
| Import recovery | In the isolated setup, use a deliberately wrong download-path mapping for a completed test download. Wait for **Import needs attention**, restore the mapping, then click **Retry import**. | The original files remain intact and import succeeds without another download. After the import-failure state has been recorded, retry also works if the torrent is removed while retaining its files. |
| Scan recovery | In the isolated setup, temporarily use an unreachable Audiobookshelf URL while the test book completes. Restore it and retry. | The book remains **Import complete** / scan-pending with its imported files intact. Retry requests the scan without downloading/importing a second copy. |
| Clear blocklist | On a book with failure history, click **Clear failed-release blocklist**, then search releases again. | Previously blocked releases can be selected again, and history remains visible. An existing tracked release can still be disallowed as a duplicate. |

Return the timeout to 1440 minutes (24 hours), or your preferred setting, after testing. Set it to 0 to disable stall detection. Choose whether to leave automatic replacement enabled. Old failed torrents are retained and can still consume bandwidth or space; manage them explicitly in qBittorrent.

Do not induce full-disk or permission failures on your real library. Those need a disposable storage environment. Grimarr distinguishes imported files, requested scans, and confirmed indexing. Review held duplicate matches or differing indexed metadata rather than assuming a successful scan response proves availability.

## Rollback

Pause automation before switching back. For a rollback across the security-hardening upgrade, stop Grimarr and restore its corresponding **complete pre-upgrade config backup, including encryption keys, into a fresh directory**. Preserve ownership and point the config mount at that restored directory before starting the older image. The newer identifier encryption is not understood by older binaries, so an image-only rollback is insufficient. Keep the newer config separately. Then create a rollback override selecting the retained image and recreate only Grimarr using the restored config:

```bash
cat > rollback.override.yml <<'YAML'
services:
  grimarr:
    image: grimarr-local:before-sqlite-recovery
YAML
docker compose -f compose.yaml -f rollback.override.yml up -d --no-build grimarr
```

The older app may not understand newly persisted recovery states. If rollback is necessary after using recovery, stop Grimarr and restore the complete pre-update config backup into a fresh config directory, preserving ownership, then point the config mount at that restored directory before starting the old image. Retain the newer config separately. Restoring config does not undo torrents added or library files imported since the backup, so review those separately. Do not roll back the database while Grimarr is running.

## Automated checks for a source checkout

With .NET 10, Node.js 24, and FFmpeg installed, run from the source root:

```bash
dotnet restore tests/Grimarr.Tests/Grimarr.Tests.csproj --force --no-cache
dotnet run --project tests/Grimarr.Tests --no-restore
dotnet build src/Grimarr -c Release --no-restore
dotnet list tests/Grimarr.Tests package --include-transitive --vulnerable --no-restore
cd frontend
npm ci --ignore-scripts
npm audit --audit-level=high
npm run build
npx playwright install chromium --with-deps
cd ..
mkdir -p src/Grimarr/wwwroot
cp -r frontend/dist/. src/Grimarr/wwwroot/
node tests/integration.mjs
node tests/manual-selection.mjs
node tests/recovery.mjs
node tests/quick-add.mjs
node tests/library-availability.mjs
node tests/security.mjs
node tests/ui.mjs
```

The Node suites launch their own backend processes with temporary databases and loopback mock services. The browser suite uses local port 8787; keep it free when running that suite. These tests do not use your deployed credentials or media library.
