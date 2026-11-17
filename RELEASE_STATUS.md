# Release baseline and recovery

Verified locally on Windows on 2026-11-17. Grimarr remains an early alpha.

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

- Run Linux CI and a Docker image smoke test; Docker is unavailable in this local environment. CI results are not claimed by local checks.
- Run a fresh real-service beta across supported qBittorrent/Prowlarr/Audiobookshelf versions and storage layouts.
- Expand operational fault testing to actual disk exhaustion, Linux ownership/permissions, and abrupt container termination during a large copy. Current tests cover I/O failure after staging, canceled validation, restart boundaries, and stale staging; they do not simulate every hardware/storage failure.
- Complete the later release work: existing-library availability checks, security review, installation/upgrade/restore checks, and release packaging.

## Recovery defaults and limits

Automatic replacement is disabled by default. Missing torrents have a two-minute confirmation window. Active-download stall detection defaults to 24 hours and can be disabled. Connectivity failures do not create failed-release blocks. Import failures wait for an explicit retry after the underlying issue is fixed.

An old failed torrent is retained and can continue transferring. Blocklists are per tracked book, not global. Audio validation checks readable streams and duration rather than decoding the entire book. An Audiobookshelf scan request still does not confirm indexing.

See [UPDATE_AND_TEST.md](UPDATE_AND_TEST.md) for deployment, rollback, and a manual recovery checklist. The SQLite change follows the [Microsoft.Data.Sqlite 10.0.15 dependencies](https://www.nuget.org/packages/Microsoft.Data.Sqlite/10.0.15) and the [CVE-2025-6965 advisory](https://github.com/advisories/GHSA-2m69-gcr7-jv3q).
