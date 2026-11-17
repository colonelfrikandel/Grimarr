# Release baseline and recovery

Verified on Windows and on a Linux Docker server on 2026-11-17. Grimarr remains an early alpha.

## Implemented

- Reviewed the Seerr/Fukuro interface baseline and replaced the remaining derived shell components. Fixed leftover light backgrounds and low-contrast text found during screenshot review.
- Explicit download failures for confirmed missing torrents, qBittorrent errors/missing files, and configurable active-download stalls.
- Persistent per-book failure history and blocklist, including hash matching across release aliases. Failure history and the replacement search state are saved together.
- Same-release retry, replacement search, clear-blocklist actions, and opt-in automatic replacement. Manual choice and paused monitoring remain respected. Recovery does not delete torrents or files.
- Separate persisted import stage so import retries and restart recovery do not require a torrent still being present.
- Fresh private staging on retry, exact destination ownership checks, and validation of recovered audio after an interrupted database update.
- Added manual-selection and recovery integration suites to CI alongside existing build, browser, and Docker checks.
- Updated Microsoft.Data.Sqlite from 10.0.0 to 10.0.15, resolving SQLitePCLRaw to 2.1.13. Native SQLite reports 3.53.3. The previous NU1903 advisory is resolved without suppressing warnings.
- Backend restores audit all dependencies and treat vulnerability warnings NU1901–NU1904 as errors. A runtime check verifies that the loaded native SQLite includes the CVE-2025-6965 fix (3.50.2 or newer).

## Local verification

- Frontend TypeScript and production build passed.
- 94 backend checks passed, including native SQLite version verification, real ffprobe audio validation, failure-history persistence, stall timing, storage-error recovery, and safe import retries.
- 13 pipeline integration checks passed.
- 16 manual-selection integration checks passed.
- 21 recovery integration checks passed.
- Browser suite passed for login, navigation, settings, edition preferences, manual choices, recovery settings/actions, and mobile layout. Screenshots were reviewed.
- Test services used loopback mocks and synthetic audio. No production services or user library were modified.
- Fresh NuGet restore and transitive vulnerability audit passed against the current feed with no known vulnerable packages reported. Debug checks and the Release build passed; the Release build had zero warnings and errors.

## Remaining before release

- Run public CI; the server-side Linux checks below are separate from a GitHub Actions run.
- Expand the real-service beta across additional supported versions and storage layouts.
- Expand operational fault testing to abrupt container termination during a large copy and other storage layouts. Disk exhaustion, Linux permission denial, restart boundaries, and stale staging have been checked, but not every hardware/storage failure is simulated.
- Complete the later release work: existing-library availability checks, security review, installation/upgrade/restore checks, and release packaging.

## Quick Add Apple Books matching

Revision `5640ed0` was deployed on 2026-11-17 as `grimarr-local:apple-5640ed0`. Quick Add offers an optional Apple Books audiobook search using the detected or corrected title and author. Users compare covers, descriptions, dates, and genres, explicitly choose an edition, and can correct title/author before adding. The selected cover, description, catalog ID/link, release date, and genre persist in Grimarr and appear in book details. Changing the download source clears its catalog selection. Manual entry remains available when no suitable match exists or Apple is unavailable. Prowlarr remains available.

- TypeScript/production builds passed. Backend builds completed with zero warnings/errors; 144 backend checks passed on Windows and Linux. The complete Linux run passed 217 backend/integration checks: 144 backend, 13 pipeline, 16 manual-selection, 21 recovery, and 23 Quick Add checks.
- Browser tests passed explicit edition selection, reviewed name submission, no premature additions, empty/error/manual fallback, stale-response cancellation, source-change resets, mobile layout, and the existing recovery/Prowlarr flows.
- Twenty-five live checks passed with actual Apple Books results, selected metadata persistence across container restart, rejection of choices invalidated by restart, and real synthetic magnet/torrent-file transfers through native qBittorrent into Audiobookshelf. Full Audiobookshelf item metadata confirmed the reviewed titles/authors. Fixture torrents, indexed items, marked test media, and temporary containers were cleaned up; original records were preserved.
- Production retained all six managed books, imported paths, preferences, credentials, mounts, port, and automation/manual-selection settings. All three service connections passed and the container is healthy. A full config/keys backup, previous Compose file, and previous image are retained under `/home/frikandel/grimarr-alpha/apple-5640ed0/` and `grimarr-local:before-apple-5640ed0`.
- The deployed browser flow passed real catalog lookup, explicit selection, successfully loaded cover art, displayed details, editable names, source-change reset, Unicode torrent autofill, manual restore, preserved Prowlarr choice, unchanged production book count, and no runtime errors. Desktop/mobile screenshots were reviewed.

Only the search text goes to Apple's US audiobook catalog; source files and private magnet/tracker parameters are excluded. Searches are cached for five minutes and globally limited to 15 per minute. Selection tokens expire after 15 minutes or a server restart; users can search again or use manual details. Apple metadata saved in Grimarr does not itself guarantee the torrent's language, edition, or available seeders.

## Editable Quick Add autofill

Revision `8d59eea` was deployed after verification. Quick Add now previews magnet display names and torrent internal names, suggests title/author values, and keeps both fields editable before adding. Manual corrections survive subsequent previews; canceled or late responses cannot replace newer details. A restore button explicitly reapplies the current suggestions. Preview uses no peer/indexer lookups and creates no book or download. Missing names/authors remain available for manual entry; parsing is heuristic and should be reviewed.

- All 200 backend/integration checks passed on Linux: 128 backend checks, 13 pipeline checks, 16 manual-selection checks, 21 recovery checks, and 22 Quick Add checks.
- The browser suite passed automatic magnet and UTF-8 torrent suggestions, submitted corrections, preservation of edits, restore behavior, stale-response cancellation, and no premature additions.
- Nineteen live checks passed using synthetic downloads with real qBittorrent and Audiobookshelf. Both magnet and torrent-file previews populated details without creating a torrent. Corrected titles/authors were then saved, imported, and verified in Audiobookshelf's full item metadata. Fixtures were cleaned up and existing items retained.
- A full config/keys backup and the previous Docker image were retained. All six existing managed books, imported paths, preferences, service credentials, mounts, port, and automation settings were preserved. All three service connections passed, and the deployed container is healthy.
- The actual server UI passed magnet/file autofill, Unicode handling, manual correction protection, restore behavior, preserved Prowlarr choice, mobile layout, unchanged book count, and no runtime errors.

The Linux run also exposed a pre-existing recovery-test race: a persisted torrent hash can precede the remote add request. The test now waits for the downloading state before asserting submission side effects.

## Quick Add verification and deployment

Revision `6d344fe` adds **Quick Add** alongside **Search with Prowlarr** in the Add audiobook dialog. It accepts hexadecimal/base32 v1 info hashes, magnets, and v1/hybrid torrent files up to 16 MB. Direct releases use the existing category/tag, download tracking, validation, import, and scan pipeline. Private magnet parameters and uploaded metadata are encrypted and omitted from public API responses. Failures require an explicit retry or a choice to search Prowlarr for a replacement.

- 115 backend checks plus 66 integration checks passed on Linux (181 total), including 16 Quick Add integration checks. The browser suite passed all three input forms, validation errors, mobile layout, and switching back to Prowlarr.
- 17 live checks passed with the production image, the actual qBittorrent client, a temporary local seeder, and Audiobookshelf. All three input types completed and produced indexed audio items with matching content. Public discovery was disabled on the seeder and peer addresses were supplied explicitly; public DHT availability was not assumed.
- Duplicate torrent rejection, category/tracking tags, operation without Prowlarr configured, and compatibility with manual-selection mode were verified. Test torrents, library entries, marked test media, and temporary containers were cleaned up; original torrents and library items were retained.
- The new image was deployed on the Linux server after retaining the original image and a full config/keys backup. All six existing managed books, their imported paths, settings, credentials, mounts, port, and automation/manual-selection preferences were preserved. The container is healthy and all service connection tests passed.
- The actual deployed UI passed login, both acquisition choices, hash/magnet and file-upload controls, invalid-input handling, mobile layout, and unchanged book-count checks.

Quick Add does not fix missing peers or unavailable torrent metadata. A torrent upload supplies metadata but still needs the audio pieces. A user-provided release bypasses Prowlarr's edition/language matching and should be checked before adding.

## Linux server verification

Application revision `a49b52b` was built and deployed with Docker while preserving the existing config, media mounts, port, container user, credentials, and six managed book entries. A complete pre-update config backup and the original image were retained for rollback.

- All 144 backend/integration checks and the desktop/mobile browser suite passed in a Linux container as UID 1000.
- The deployed interface passed actual login, existing-library display, recovery-control/default, and mobile-layout checks.
- The native library copied from the deployed image reported SQLite 3.53.3.
- 23 live checks passed using synthetic private torrents, real qBittorrent 5.1.4, and an existing isolated Audiobookshelf test library. The indexer responses were synthetic fixtures; real Prowlarr connectivity was verified separately.
- Live checks covered restart recovery, stopped-torrent protection, stalled-release blocking, same-release retry, manual and automatic replacement, monitoring pause, the actual two-minute missing-torrent grace, manual-mode protection, blocklist clearing, connection outages, wrong mappings, import retry after torrent removal, scan-outage recovery, actual Audiobookshelf indexing, and atomic-import restart recovery.
- Stall timestamps were accelerated only in an isolated test database; a full 15-minute wall-clock stall was not awaited.
- Nine additional non-root storage checks passed, including actual ENOSPC in a disposable 2 MB tmpfs, source preservation, no partial library item, successful retry after freeing space, denied Linux permissions and retry, and symlink rejection.
- Synthetic torrents, media, library entries, and temporary test containers were removed. Existing torrents and library items were retained. Original automation and manual-selection settings were restored; automatic replacement remained disabled.

Retained failed torrents can occupy qBittorrent queue slots. If a replacement remains queued, stop or otherwise manage old transfers in the download client. Audiobookshelf can retain missing-item records after files are removed; test cleanup explicitly removed only the synthetic item's database record.

## Recovery defaults and limits

Automatic replacement is disabled by default. Missing torrents have a two-minute confirmation window. Active-download stall detection defaults to 24 hours and can be disabled. Connectivity failures do not create failed-release blocks. Import failures wait for an explicit retry after the underlying issue is fixed.

An old failed torrent is retained and can continue transferring. Blocklists are per tracked book, not global. Audio validation checks readable streams and duration rather than decoding the entire book. An Audiobookshelf scan request still does not confirm indexing.

See [UPDATE_AND_TEST.md](UPDATE_AND_TEST.md) for deployment, rollback, and a manual recovery checklist. The SQLite change follows the [Microsoft.Data.Sqlite 10.0.15 dependencies](https://www.nuget.org/packages/Microsoft.Data.Sqlite/10.0.15) and the [CVE-2025-6965 advisory](https://github.com/advisories/GHSA-2m69-gcr7-jv3q).
