# Changelog

## 1.0.0-rc.1 — 2026-11-23

First packaged release candidate for Linux/amd64 Docker installations. This is a prerelease, not a claim that the outstanding beta/CI/disaster-recovery matrix is complete.

- Prowlarr search/manual release selection, plus Quick Add for info hashes, magnets and torrent files.
- Editable title/author detection and explicit Apple Books edition selection; selected artwork, descriptions, dates, genres/tags and complete provider metadata are retained during Audiobookshelf import.
- Persistent failed-release history, same-release/import retries, optional replacement, pause protection, stall/missing-torrent checks and restart recovery.
- Existing-library duplicate review and confirmed Audiobookshelf indexing with item-specific links and metadata review.
- Owner authentication/session revocation, bounded requests/downloads, protected private identifiers, local-only audio probes, validated torrents and restricted filesystem paths.
- Startup config validation, verified complete config backup/restore tooling, non-root install checks and documented full-config rollback.
- Version/revision reporting, image provenance labels, corresponding source archive, offline image export, SHA-256 checksums, CycloneDX inventory, vulnerability report and third-party notices.

### Upgrade notes

Back up the complete config, including encryption keys and session state, while Grimarr is stopped. The tool handles stopping/restarting the named app. Keep a matching previous image and deployment environment. Upgrades across the security change invalidate older-format cookies and encrypt identifiers that older binaries cannot decode. Roll back using the old image **and its corresponding full pre-upgrade config**; keep media and download-service changes under review separately. Preserve existing mounts, paths, UID/GID, password and automation preferences.

### Known limits

Only Linux/amd64 bind-mount deployments are included in this candidate's verified packaging matrix. Broader storage/version/platform coverage, public CI and off-host whole-system recovery remain outstanding. Runtime OS scans retain vendor-unfixed medium/low findings; consult the packaged report rather than treating the image as vulnerability-free. Hashes detect corruption and do not constitute a publisher signature. Torrent availability still depends on peers; audio validation does not verify an entire recording's identity or fully decode it. See README, SECURITY and RELEASE_STATUS for the complete scope.
