# Config backup, upgrade and restoration

The Linux host tool `scripts/config_backup.py` requires Python 3.10+ and Docker CLI access. It backs up the complete `/config` bind mount: SQLite database/WAL, encryption keys, session state and other config files. It does not back up audio, downloads, Audiobookshelf, Compose, `.env` or images. Keep separate private copies of those deployment files, a matching image, and your media/service backups. Only one Grimarr instance may write a config directory; coordinate other writers before taking a backup.

Since 1.0.0-rc.2 the complete config also includes `owner-password.json`, the salted password hash. Restore it with the matching keys/session/database. The plaintext password is not needed in the current container environment; see [PASSWORDS.md](PASSWORDS.md) for initialization/reset. Historical pre-hash binaries still require their original environment password for rollback.

## Create and verify a backup

Use the actual container name (`grimarr` by default; the custom alpha uses `grimarr-alpha`). Run as an operator who can read all config files and access Docker. Use a private existing backup directory outside `/config`:

```bash
install -d -m 0700 "$HOME/grimarr-backups"
python3 scripts/config_backup.py backup --container grimarr --output "$HOME/grimarr-backups/config-$(date +%Y%m%d-%H%M%S).tar.gz"
python3 scripts/config_backup.py verify "$HOME/grimarr-backups/CHOSEN-BACKUP.tar.gz"
```

The tool briefly stops that container, checks SQLite integrity, archives files with numeric ownership/modes and SHA-256 hashes, verifies the archive, and restarts the same container if it was running. An initially stopped container stays stopped. A backup failure still attempts to restart a previously running container. It never overwrites an existing archive. Published archives are mode `0600`; links and special files in config are rejected. Check that the restarted app becomes healthy and its authenticated books/settings load. Backup/restart errors return a nonzero exit code.

Keep several dated backups, retain at least one off-host copy, and perform a restore drill after copying one back. Hashes detect accidental corruption; they do not authenticate an archive from an untrusted source. Archives can contain old plaintext identifiers and encryption keys: protect them like passwords. Do not commit or expose them through a web server.

## Restore into a new directory

Stop the instance that will use the restored config. Keep its current config unchanged. Pick a destination that does **not** exist:

```bash
python3 scripts/config_backup.py restore "$HOME/grimarr-backups/CHOSEN-BACKUP.tar.gz" --destination /srv/grimarr/config-restored
```

The destination's parent must already exist. The tool checks every member and hash before extraction, rejects traversal/absolute paths, links, special files, duplicate members, unsupported manifests and oversized archives, then checks database integrity/completeness. It preserves numeric ownership and modes. If the backup belongs to a different user, run with the privileges needed to restore those IDs; do not make config world-readable to work around ownership errors. The command does not start Grimarr or overwrite an existing config.

Point `GRIMARR_CONFIG_DIR` (or your custom Compose `/config` source) at the new directory, select the matching retained image, retain your original password/environment and media mounts, then start only Grimarr. The archive's manifest records image ID/tag and user, without recording container environment secrets. Record `docker image inspect IMAGE --format '{{.Id}}'` when retaining a tag: a reused tag can point at a different image. Keep deployment secrets in a separate private backup.

On first startup, Grimarr validates the stored books, connection secrets and protected session state before advertising health. Missing/wrong keys, corrupt encrypted state or unreadable config fail startup. Restore the complete matching archive rather than deleting keys/session state. Once healthy, sign in and check books, workflow stages, preferences and all service connections before allowing automation. In an isolated drill, disable automation in the cloned database **before** starting it, mount no production media, and block service access. Never run two automated copies against the same torrents/library.

Config restoration does not rewind qBittorrent transfers or library files changed since the backup. Review these external changes before resuming. Back up and restore those services/media independently when a complete system rollback is needed.

## Upgrade and roll back

Build a distinct candidate image while the current instance runs. Pause automation, retain the exact old image, take a verified complete config backup, and preserve Compose/`.env` privately. Update only the Grimarr image/source reference; retain paths, IDs, ports, mounts, credentials and preferences. Start and verify health, books, settings and service connections, then restore your chosen automation state. See [UPDATE_AND_TEST.md](UPDATE_AND_TEST.md) for the custom alpha upgrade checklist.

For rollback, use the **old image plus its complete corresponding pre-upgrade config** in a fresh directory, with the saved deployment environment. Older binaries cannot decode identifiers encrypted by the security upgrade; an image-only rollback is insufficient. Keep the failed/newer config for diagnosis. Existing earlier operator tar backups remain usable through their documented manual full-config restoration; they are not the new manifest format and the tool deliberately refuses to treat them as verified archives.

## Repeatable checks

```bash
python3 tests/config_backup_test.py
python3 tests/installation.py --image YOUR-CANDIDATE --previous-image YOUR-RETAINED-OLD-IMAGE
```

The Docker suite uses disposable private configs, an isolated Compose project and random loopback ports, with automation off and no media/service mounts. It checks clean installation, missing bind directories, frontend delivery, upgrades with private queued state, restart, live/stopped backup, full restore/rollback, missing keys, corrupt database/session, config permission failure and invalid passwords. Archive tests cover corrupt/truncated files and unsafe members. CI runs these checks against its built image; the optional previous-image argument adds a real prior-version upgrade path. Broader multi-host storage layouts and every historical version remain outside this matrix.
