# Release packaging and installation

Current version: **1.0.0-rc.2**, a release candidate for Linux/amd64 Docker hosts. A tested private package on the operator's server is distinct from a public GitHub Release or registry publication. No `latest` image alias is used. Image tags include both version and source revision; `release.json` records the exact image ID.

## Delivered files

Each release directory contains the compressed Docker image, corresponding versioned source archive, `release.json`, `SHA256SUMS`, `compose.release.yml`, `release.env.example`, `LICENSE`, third-party notices, changelog, this guide, a CycloneDX SBOM and the full vulnerability report. Source includes build scripts and locked application dependencies, without runtime config, secrets, dependencies or generated binaries. Frontend license texts and NuGet-distributed notices are also retained inside the image. Rebuilding source against moving upstream base/OS feeds is not claimed to reproduce identical bytes; the image ID and archive hash identify the tested artifact.

## Install the packaged image

Obtain the complete release directory from a trusted operator/public release, then run inside it:

```bash
sha256sum -c SHA256SUMS
IMAGE_ARCHIVE=$(python3 -c "import json; print(json.load(open('release.json'))['imageArchive'])")
docker load --input "$IMAGE_ARCHIVE"
cp release.env.example .env
chmod 600 .env
```

Set the actual `PUID`/`PGID`, config/data paths, bind address and port in `.env`. Keep `GRIMARR_IMAGE` equal to the image in this package's manifest. No plaintext password belongs in `.env`. Create the config and data/download/library folders with the chosen user's ownership; the shared data parent must allow staging. The package's Compose refuses missing bind directories. Use the README storage instructions from the source bundle and initialize the password interactively as below; there are no length/composition restrictions. See PASSWORDS.md in the source for changing it later without a restart.

```bash
docker compose -f compose.release.yml run --rm --no-deps grimarr password
docker compose -f compose.release.yml up -d --no-build --pull never
docker compose -f compose.release.yml ps
docker compose -f compose.release.yml logs --tail=60 grimarr
```

The image is loaded locally, so no registry login or source build is required. Open your configured host/port, sign in and configure/test services. Readiness includes config decryption; authentication is required for `/api/system`, which reports version and source revision. The sidebar shows the version/prerelease channel.

For an existing custom deployment, retain its Compose/environment/mounts/ports and switch only its image reference to the manifest's image. Do not replace it with the generic release example. Pause automation, retain the exact old image, take a verified stopped full-config backup, start the new image, then verify books, service connections, settings and workflow states before restoring your chosen automation setting. See `UPDATE_AND_TEST.md` and `BACKUP_AND_RESTORE.md` in the source archive. Media, other services, deployment secrets and off-host backups are separate from a config archive.

To roll back, stop Grimarr and restore the matching complete pre-upgrade config into a new directory before starting its retained image with the saved deployment environment. Preserve the newer config separately. Image-only rollback across the security upgrade cannot decode new private fields. Config restore does not rewind downloads/library files changed after the backup.

## Build and verify a package

From a clean committed checkout on Linux/amd64 with Docker, Git and Python 3.10+:

```bash
mkdir -p artifacts
git archive --format=tar.gz --output=artifacts/input-source.tar.gz HEAD
python3 scripts/release.py build --source-archive artifacts/input-source.tar.gz --revision "$(git rev-parse HEAD)" --output artifacts/release --cache-dir artifacts/scanner-cache
python3 scripts/release.py verify artifacts/release
python3 tests/release_package_test.py
python3 tests/release_smoke.py --directory artifacts/release
```

The builder validates source exclusions/version agreement, builds with source/version labels, exports the image, scans it with a pinned scanner without a Docker socket/config mount, generates the inventory and checksums, and publishes into a **new** output directory. Critical/high findings or embedded secrets block packaging. Medium/low findings are retained transparently in the report. The input revision is supplied by the trusted Git checkout; checksum metadata is not an independent signed provenance attestation.

The smoke test removes only an unused candidate image tag and loads its compressed archive to verify the identical image ID. It refuses to do this while a container uses the candidate. Do this before installation/deployment. Run the complete backend/integration/browser suites and `tests/installation.py --image IMAGE --previous-image PREVIOUS --compose-file artifacts/release/compose.release.yml` as well. The CI workflow builds and uploads these artifacts only after its checks; it does not push images, tags or GitHub Releases. GitHub artifact retention is temporary, so copy approved release artifacts to durable storage.

The package is unsigned: verify checksums using a trusted copy of `SHA256SUMS`; hashes alone do not establish the publisher. Public CI, broader beta/storage coverage and an off-host whole-system restore drill remain release-readiness work. A final `1.0.0` tag/publication should follow that validation rather than relabelling this candidate as stable.
