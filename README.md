# Grimarr

A self-hosted audiobook manager for Docker. Search for audiobooks through Prowlarr, download them with qBittorrent, and automatically import completed downloads into Audiobookshelf.

**Early alpha, version 0.1.** Automated tests cover the download and import pipeline using simulated services. Live-service compatibility testing is still limited.

## Features

- Password-protected responsive web interface: library, queue, activity, settings.
- Optional **Choose releases manually** setting: search a series, select up to ten results, and track each selected release separately. With this setting off, the best eligible match downloads automatically.
- Green *arr-style interface using adapted Radarr header, sidebar, toolbar, and loading components, with poster/list views and sorting/filtering.
- Apple Books/iTunes audiobook catalog search and manual title/author entry.
- Global and per-book preferences: standard/full-cast/either narration, abridged/unabridged/either, minimum seeders, M4B preference.
- Automatic Prowlarr search (audiobook category 3030), ranked selection, and scheduled searches for missing books.
- qBittorrent submission, progress tracking, and restart recovery without duplicate torrents.
- Editable service URLs, credentials, indexer IDs, storage roots, and remote path mappings.
- ffprobe audio checks, hardlink/copy imports, and atomic library imports. Original download files remain untouched for seeding.
- Audiobookshelf library selection and retryable scan requests.
- SQLite persistence; credentials and private download URLs encrypted with ASP.NET Data Protection.

## Current limits

- **Listener ratings are not available yet.** Automatic selection currently uses release matching, edition preferences, format preference, and seeder availability.
- Matching is strict: all title/author words must match, English must be identified, and the requested abridgement must be identifiable. Poorly labelled releases remain monitored. Inspect rejection reasons on the book's detail screen. Matching is heuristic and cannot guarantee recording identity.
- No archive extraction, book-pack splitting, conversion, quality upgrades, existing-library adoption, or automatic replacement of failed torrents yet.
- One managed audiobook per title/author. Preferences are editable while waiting for a release; simultaneous recordings of one book are not supported yet.
- BitTorrent v1 and hybrid torrents are supported. Pure v2 torrents and direct third-party download URLs without a Prowlarr proxy are not supported yet.
- “On your shelf” means files were imported and an Audiobookshelf scan was requested. Grimarr does not yet confirm indexing or resolve item-specific playback links.
- Pause monitoring suspends Grimarr's work, not qBittorrent's transfer. Retry resumes the current stage with the same selected release.
- **Remove book** deletes an entry that has not started downloading, including paused books with no results. Downloading and imported books cannot be removed this way. Activity history is retained; no torrent or media files are deleted.
- Audio validation checks readable audio streams and positive duration, not a full decode or the book's actual contents.

## Installation

### Requirements

- A Linux host with Docker Engine and the Docker Compose plugin. The examples below use Ubuntu-compatible shell commands.
- Git to clone the repository.
- Running Prowlarr, qBittorrent, and Audiobookshelf instances reachable from the Grimarr container.
- Filesystem access to completed downloads and the Audiobookshelf library directory.

Grimarr is built from source using the included Dockerfile. The Compose configuration starts Grimarr only; configure the other services separately.

### Download and configure

Clone the repository and create a local environment file:

```bash
git clone https://github.com/colonelfrikandel/grimarr.git
cd grimarr
cp .env.example .env
nano .env
```

Configure these values in `.env`:

| Variable | Purpose | Default |
| --- | --- | --- |
| `GRIMARR_PASSWORD` | Login password; replace the example with a unique password of at least 12 characters | Must be configured |
| `PUID` / `PGID` | Container user and group IDs with access to the storage directories | `1000` / `1000` |
| `GRIMARR_CONFIG_DIR` | Host directory for the database and encryption keys | `./config` |
| `GRIMARR_DATA_DIR` | Host directory containing downloads and the audiobook library | `/srv/media` |
| `GRIMARR_BIND_IP` | Host interface on which to expose Grimarr | `127.0.0.1` |
| `GRIMARR_PORT` | Published web interface port | `8787` |

Use `id -u` and `id -g` to find the current user's IDs, or specify another user/group with the required storage permissions.

### Storage

Set `GRIMARR_DATA_DIR` to a common parent such as `/srv/media`. Create or select two separate, non-nested folders under it:

```text
/srv/media/
├── downloads/
└── audiobooks/
```

These appear inside Grimarr as `/data/downloads` and `/data/audiobooks`. A single common `/data` mount allows hardlinks when both folders share a filesystem. The parent `/data` must also be writable: imports are staged in `/data/.grimarr-staging/` before being moved into the library.

Create your config directory and ensure its owner matches `PUID:PGID`. For the default path and current user:

```bash
mkdir -p config
chmod 700 config
```

### Start Grimarr

The default `GRIMARR_BIND_IP=127.0.0.1` permits access from the Docker host only. To access Grimarr from other devices, set it to the host's LAN IP or `0.0.0.0` to listen on all host interfaces. A Tailscale IP can also be used when Tailscale is configured on the host.

```bash
docker compose up -d --build
docker compose logs -f grimarr
```

Open `http://localhost:8787` on the Docker host, or `http://SERVER_IP:8787` when listening on a network interface. Sign in with `GRIMARR_PASSWORD`. Substitute the configured port if it differs from `8787`.

The initial build requires internet access to download images and dependencies. Catalog and indexer searches contact their upstream services, and catalog covers load from the catalog's image servers. The web interface assets are served locally.

## Service configuration

In **Settings**, enter and test your service URLs and credentials:

| Service | Example host URL | Credentials |
| --- | --- | --- |
| Prowlarr | `http://192.168.1.10:9696` | API key |
| qBittorrent | `http://192.168.1.10:8080` | Web UI username/password |
| Audiobookshelf | `http://192.168.1.10:13378` | API token with library/scan access |

Test Audiobookshelf to load and select your library. Its internal Docker port is often 80, mapped to host port 13378. Every URL/port is editable.

URLs must be reachable **from Grimarr's container**. `localhost` means the container itself. Docker service names require a shared Docker network; published host addresses can be used without joining that network. If qBittorrent uses a VPN container, use the Web UI address exposed by that setup. Use an Audiobookshelf URL also reachable by the browser for the “Open Audiobookshelf” button.

Configure the download/library roots, and optionally the save path as seen by qBittorrent. Audiobookshelf must watch the same host library folder Grimarr imports into.

If qBittorrent reports `/downloads/Some Book`, but Grimarr sees `/data/downloads/Some Book`, add a path mapping:

| qBittorrent prefix | Grimarr prefix |
| --- | --- |
| `/downloads` | `/data/downloads` |

These must refer to the same files. Mapping translates paths; it does not transfer files between machines. Symlinks in import paths are rejected; mount the real folder.

Choose your defaults, enable automation, and save. Add a book to search on the next worker cycle (normally within 15 seconds). Books without eligible releases are searched at the configured interval. Service/import errors retry after five minutes, or use **Retry now**.

For manual downloads, turn on **Settings → Profiles → Choose releases manually**. New books wait for your choice. Open a book, edit **Release search** if needed (for example, `Mage Tank` rather than a catalog subtitle), and click **Search download options**. Check one or more results and click **Download selected**. Each choice becomes a separate tracked entry, named after its release; the first replaces the original waiting entry. On a completed book, **Choose more downloads** adds new entries and preserves the existing book. Already tracked releases are rejected. Choices can override automatic title/edition/language/seeder matching, so inspect the displayed warnings. Torrent validation and Audiobook Bay detail verification still apply. A multi-book pack stays a single download; this does not split packs. Results expire after 30 minutes or a server restart; search again if needed. Existing downloads keep progressing when you switch modes. If searching/importing is disabled, selections are queued until it is enabled.

## Backups and troubleshooting

### Audiobook Bay through Jackett

Add Jackett's **AudioBook Bay** indexer (`https://audiobookbay.lu/`) as a Generic Torznab indexer in Prowlarr, using its per-indexer Torznab URL and API key. Select that Prowlarr indexer ID in Grimarr. Disable RSS for this bridge and use conservative query limits.

Grimarr reads the structured language and abridgement fields from matching Audiobook Bay detail pages and validates the page's public v1 info hash before constructing a magnet. It only contacts HTTPS detail URLs on `audiobookbay.lu`, does not follow redirects, bounds response size/time, spaces requests, and caches results. Up to three plausible detail pages are fetched per search; unavailable or unverified details remain ineligible. No login or paid direct-download links are used.

Jackett's Audiobook Bay seeder count is a placeholder. Grimarr displays it as **unknown**, gives no seeder score, and requires **minimum seeders = 0** to allow those releases. This does not guarantee peers are available. Individual episodes/parts are rejected for whole-book requests, and explicit book numbers must match. Audio Immersion Tunnel editions are treated as dramatized.

Keep automation off while testing the connection and inspecting release reasons. The normal title, author, language, and edition checks still apply; a working indexer does not guarantee a matching release.

Back up the **entire config directory**, including the database and `keys/`. Stop Grimarr while copying SQLite files, or use a SQLite-aware backup. The keys are needed to decrypt saved credentials. Protect access to this folder: its keys and database together can recover credentials.

- **No download:** enable automation and inspect release rejection reasons. Indexers must support audiobook/category 3030 searches.
- **Connection fails:** check service addresses from the container's network, credentials, and qBittorrent's Web UI access rules.
- **Files not found:** check Docker mounts and path mappings.
- **Access denied:** the container UID/GID must read downloads and write the library and its parent.
- **Imported but not visible:** verify Audiobookshelf watches the same host folder. Scan failures preserve imports and retry separately.
- **Torrent missing/error:** resolve in qBittorrent and retry in Grimarr. Grimarr does not delete or replace torrents automatically.

## Development and tests

Requires .NET 10 SDK, Node.js 24, and FFmpeg (`ffprobe` on PATH). The Docker image includes FFmpeg.

```bash
cd frontend
npm ci
npm run build
cd ..
mkdir -p src/Grimarr/wwwroot
cp -r frontend/dist/. src/Grimarr/wwwroot/
export GRIMARR_PASSWORD='a-long-local-development-password'
export GRIMARR_CONFIG="$PWD/config"
export ASPNETCORE_URLS='http://127.0.0.1:8787'
dotnet run --project src/Grimarr
```

For frontend development, `npm run dev` in `frontend` proxies API requests to port 8787.

```bash
dotnet run --project tests/Grimarr.Tests
node tests/integration.mjs
node tests/manual-selection.mjs
# Install Chromium once: cd frontend && npx playwright install chromium && cd ..
node tests/ui.mjs
```

The integration test starts temporary loopback mock services and the real backend. It exercises authentication, search/download, restarts, imports, and scan recovery without contacting configured services or downloading real torrents. Build the Debug backend first. `DOTNET_EXE` and `FFPROBE_DIR` can override tool paths.

## Code map

| File | Responsibility |
| --- | --- |
| `src/Grimarr/Program.cs` | API, authentication, settings validation |
| `src/Grimarr/Ranking.cs` | Matching, edition filters, scoring |
| `src/Grimarr/Integrations.cs` | Prowlarr, qBittorrent, Audiobookshelf clients |
| `src/Grimarr/Worker.cs` | Search → download → import → scan workflow |
| `src/Grimarr/Importer.cs` | Storage validation and staged imports |
| `src/Grimarr/Store.cs` | SQLite and secret protection |
| `frontend/src/` | React/TypeScript interface |

Inspired by [Radarr](https://github.com/Radarr/Radarr) and [Sonarr](https://github.com/Sonarr/Sonarr). Independent of Servarr. See [LICENSE](LICENSE) for GPL-3.0 terms.

The frontend reuses selected Radarr presentation components, adapted to Grimarr's API and audiobook data. Source revision, file provenance, and modifications are recorded in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
