# Set or change your Grimarr password

On the current server, connect by SSH and run:

```bash
docker exec -it grimarr-alpha dotnet /app/Grimarr.dll password
```

Enter the new password twice. Input is hidden. The change takes effect immediately; no restart is needed. Existing sessions are signed out on their next request. Sign in using the new password. For a standard repository deployment, replace `grimarr-alpha` with `grimarr`.

There are no password length or character-composition rules. Empty, short, whitespace and Unicode passwords are accepted exactly, without trimming or normalization. Password changes require matching confirmation. The existing login request-size limit and login rate limit still apply.

Grimarr writes only a PBKDF2-HMAC-SHA256 hash with a fresh random 32-byte salt and 600,000 iterations into `/config/owner-password.json` (mode `0600` on Unix). It does not store the password itself or print it. The protected session state tracks a fingerprint of this salted hash and rotates its nonce when the hash changes, including when reusing a previous password. That invalidates prior cookies without a restart. Keep the full config private and include the hash file in backups. The password cannot be recovered from the hash; an operator with config access can reset it using the same command.

For a fresh source install, create/own the configured storage folders, then run:

```bash
docker compose build grimarr
docker compose run --rm --no-deps grimarr password
docker compose up -d --no-build
```

For a downloaded image package, load the image first, configure `.env` paths/IDs (without a password), then initialize with `docker compose -f compose.release.yml run --rm --no-deps grimarr password`. Afterward start the service normally.

Legacy `GRIMARR_PASSWORD` is accepted only to initialize a missing hash file during migration. Once the file exists, the file wins; editing that environment variable does not change the password. Remove the plaintext variable from Compose/`.env` and recreate the container after migration. Use the interactive command for subsequent changes. Existing historical backups may contain older plaintext deployment settings; those archives remain private and are not rewritten by password migration. A rollback to a pre-hash binary also requires its original environment/password configuration.

For automation/tests, `dotnet Grimarr.dll password --stdin` accepts JSON with string properties `password` and `confirmation` through standard input. This also supports characters such as newlines that an interactive line prompt cannot enter. Never place a real password in command arguments, shell history, source files or logs. Hashes are one-way credentials, not encryption.
