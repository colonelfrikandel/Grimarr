# Grimarr

An audiobook manager for your own Docker stack. Add a book, find a matching torrent through Prowlarr, download with qBittorrent, and import into Audiobookshelf.

**Early alpha, version 0.1.** The pipeline is tested with simulated services. Compatibility with your live stack still needs verification.

## Included

- Password-protected responsive web interface: library, queue, activity, settings.
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

- **Listener-rating integration is not implemented.** The ranking model supports sourced recording ratings and vote counts, but the current adapters do not supply verified recording ratings. The UI reports them as unavailable; no ratings are invented.
- Matching is strict: all title/author words must match, English must be identified, and the requested abridgement must be identifiable. Poorly labelled releases remain monitored. Inspect rejection reasons on the book's detail screen. Matching is heuristic and cannot guarantee recording identity.
- No archive extraction, book-pack splitting, conversion, quality upgrades, existing-library adoption, or automatic replacement of failed torrents yet.
- One managed audiobook per title/author. Preferences are editable while waiting for a release; simultaneous recordings of one book are not supported yet.
- BitTorrent v1 and hybrid torrents are supported. Pure v2 torrents and direct third-party download URLs without a Prowlarr proxy are not supported yet.
- “On your shelf” means files were imported and an Audiobookshelf scan was requested. Grimarr does not yet confirm indexing or resolve item-specific playback links.
- Pause monitoring suspends Grimarr's work, not qBittorrent's transfer. Retry resumes the current stage with the same selected release.
- Audio validation checks readable audio streams and positive duration, not a full decode or the book's actual contents.

## Ubuntu Docker installation

Clone this repository to your Ubuntu server and work from its directory:

```bash
cp .env.example .env
nano .env
```

Set a unique `GRIMARR_PASSWORD` of at least 12 characters. Set `PUID`/`PGID` to a user/group with access to your storage (`id -u` / `id -g` show your current IDs).

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

Choose `GRIMARR_BIND_IP`: `127.0.0.1` (default, Ubuntu host only), your LAN IP, your Tailscale IP, or `0.0.0.0` (all interfaces, for both LAN and Tailscale).

```bash
docker compose up -d --build
docker compose logs -f grimarr
```

Open `http://YOUR_SERVER_IP:8787` and sign in with your configured password.

The image build needs internet to download dependencies. Catalog/indexer searches contact their upstream services; no public hosting or shared cloud account is required. Fonts/UI assets are bundled locally; catalog covers load from the catalog's image servers.

## Connect your stack

In **Settings**, enter and test your service URLs and credentials:

| Service | Example host URL | Credentials |
| --- | --- | --- |
| Prowlarr | `http://192.168.1.10:9696` | API key |
| qBittorrent | `http://192.168.1.10:8080` | Web UI username/password |
| Audiobookshelf | `http://192.168.1.10:13378` | API token with library/scan access |

Test Audiobookshelf to load and select your library. Its internal Docker port is often 80, mapped to host port 13378. Every URL/port is editable.

URLs must be reachable **from Grimarr's container**. `localhost` means the container itself. Docker service names require a shared Docker network; this Compose file can use published LAN/Tailscale addresses without joining that network. If qBittorrent uses a VPN container, use the Web UI address exposed by that setup. Use an Audiobookshelf URL also reachable by your browser for the “Open Audiobookshelf” button.

Configure the download/library roots, and optionally the save path as seen by qBittorrent. Audiobookshelf must watch the same host library folder Grimarr imports into.

If qBittorrent reports `/downloads/Some Book`, but Grimarr sees `/data/downloads/Some Book`, add a path mapping:

| qBittorrent prefix | Grimarr prefix |
| --- | --- |
| `/downloads` | `/data/downloads` |

These must refer to the same files. Mapping translates paths; it does not transfer files between machines. Symlinks in import paths are rejected; mount the real folder.

Choose your defaults, enable automation, and save. Add a book to search on the next worker cycle (normally within 15 seconds). Books without eligible releases are searched at the configured interval. Service/import errors retry after five minutes, or use **Retry now**.

Your friend can clone the same repository and provide their own `.env`, mounts, connections, and preferences. Each installation is independent.

## Backups and troubleshooting

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
# Install Chromium once: cd frontend && npx playwright install chromium && cd ..
node tests/ui.mjs
```

The integration test starts temporary loopback mock services and the real backend. It exercises authentication, search/download, restarts, imports, and scan recovery without contacting your stack or downloading real torrents. Build the Debug backend first. `DOTNET_EXE` and `FFPROBE_DIR` can override tool paths.

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
