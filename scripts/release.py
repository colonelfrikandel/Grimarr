#!/usr/bin/env python3
"""Build or verify a versioned Linux/amd64 release. No publishing, credentials or Docker socket mounts."""
import argparse
import gzip
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import subprocess
import tarfile
import tempfile

SCANNER = 'aquasec/trivy@sha256:8f6bb947dbc528902e11f2f5be52f7b519f6a39d5d148f41166f2040499768fb'
ROOTS = {'.github', '.dockerignore', '.env.example', '.gitignore', 'BACKUP_AND_RESTORE.md', 'CHANGELOG.md', 'Dockerfile', 'LICENSE', 'PASSWORDS.md', 'README.md', 'RELEASE.md', 'RELEASE_STATUS.md', 'SECURITY.md', 'THIRD_PARTY_NOTICES.md', 'UPDATE_AND_TEST.md', 'VERSION', 'compose.yml', 'frontend', 'scripts', 'src', 'tests'}
FORBIDDEN = {'.git', '.tools', '.env', 'config', 'keys', 'node_modules', 'bin', 'obj', 'dist', 'wwwroot', 'artifacts', '__pycache__', 'test-results'}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def sha(path):
    h = hashlib.sha256()
    with path.open('rb') as f:
        for data in iter(lambda: f.read(1024 * 1024), b''):
            h.update(data)
    return h.hexdigest()


def safe_source(archive_path, destination):
    with tarfile.open(archive_path, 'r:*') as archive:
        members = archive.getmembers()
        names = [m.name.rstrip('/') for m in members]
        require(len(members) <= 10000 and len(names) == len(set(names)) and sum(m.size for m in members) <= 128 * 1024**2, 'Source archive exceeds limits or has duplicate entries.')
        for m, name in zip(members, names):
            p = PurePosixPath(name)
            require(name and not p.is_absolute() and '..' not in p.parts and '\\' not in name and p.parts[0] in ROOTS and not (set(p.parts) & FORBIDDEN), 'Private, generated or unsafe source path.')
            require(all(ord(c) >= 32 and ord(c) != 127 for c in name) and all(not part.startswith('.env') or name == '.env.example' for part in p.parts), 'Private environment file or control character in source.')
            require(m.isfile() or m.isdir(), 'Source links and special files are forbidden.')
            require(not name.lower().endswith(('.db', '.sqlite', '.sqlite3', '.pfx', '.pem', '.key', '.log', '.zip', '.tar.gz')), 'Unexpected private/binary source file.')
        for m, name in zip(members, names):
            target = destination / name
            if m.isdir():
                target.mkdir(parents=True, exist_ok=True)
            else:
                target.parent.mkdir(parents=True, exist_ok=True)
                with archive.extractfile(m) as source, target.open('xb') as out:
                    shutil.copyfileobj(source, out)
                target.chmod(0o755 if m.mode & 0o111 else 0o644)
        return max((m.mtime for m in members), default=0)


def source_bundle(source, output, version, epoch):
    with output.open('xb') as raw, gzip.GzipFile(filename='', mode='wb', fileobj=raw, mtime=0) as compressed, tarfile.open(fileobj=compressed, mode='w') as archive:
        for path in sorted(source.rglob('*')):
            name = f'grimarr-{version}/' + path.relative_to(source).as_posix()
            info = archive.gettarinfo(path, arcname=name)
            info.uid = info.gid = 0; info.uname = info.gname = ''; info.mtime = epoch
            if path.is_file():
                with path.open('rb') as f:
                    archive.addfile(info, f)
            else:
                archive.addfile(info)


def verify(directory):
    require(directory.is_dir() and not directory.is_symlink(), 'Release directory must exist.')
    entries = {}
    for line in (directory / 'SHA256SUMS').read_text().splitlines():
        match = re.fullmatch(r'([0-9a-f]{64})  ([A-Za-z0-9._-]+)', line)
        require(match is not None and match[2] not in entries, 'Invalid or duplicate checksum entry.')
        entries[match[2]] = match[1]
    require(entries and set(entries) | {'SHA256SUMS'} == {p.name for p in directory.iterdir()}, 'Missing or unexpected release files.')
    for name, checksum in entries.items():
        path = directory / name
        require(path.is_file() and not path.is_symlink() and sha(path) == checksum, 'Release checksum mismatch.')
    manifest = json.loads((directory / 'release.json').read_text())
    require(manifest['format'] == 'grimarr-release-v1' and re.fullmatch(r'[0-9a-f]{40}', manifest['revision']), 'Invalid release manifest.')
    require(all(name in entries for name in (manifest['sourceArchive'], manifest['imageArchive'], 'sbom.cdx.json', 'vulnerabilities.json', 'compose.release.yml', 'release.env.example', 'LICENSE', 'THIRD_PARTY_NOTICES.md', 'CHANGELOG.md', 'RELEASE.md')), 'Incomplete release.')
    require(manifest['platform'] == 'linux/amd64' and not manifest['scanCounts']['CRITICAL'] and not manifest['scanCounts']['HIGH'] and manifest['embeddedSecrets'] == 0, 'Release security/platform gate failed.')
    require(json.loads((directory / 'sbom.cdx.json').read_text())['bomFormat'] == 'CycloneDX', 'Missing CycloneDX inventory.')
    return manifest


def build(args):
    output = args.output.absolute()
    require(not output.exists() and not output.is_symlink() and output.parent.is_dir(), 'Choose a new output directory under an existing parent.')
    require(re.fullmatch(r'[0-9a-f]{40}', args.revision), 'Supply the exact 40-character source revision.')
    stage = Path(tempfile.mkdtemp(prefix='.grimarr-release-', dir=output.parent))
    try:
        source = stage / 'source'; source.mkdir()
        epoch = safe_source(args.source_archive, source)
        version = (source / 'VERSION').read_text().strip()
        require(re.fullmatch(r'\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?', version), 'Invalid VERSION.')
        package = json.loads((source / 'frontend/package.json').read_text())
        lock = json.loads((source / 'frontend/package-lock.json').read_text())
        require(package['version'] == version == lock['version'] == lock['packages']['']['version'], 'Version files disagree.')
        image = f'grimarr:{version}-{args.revision[:12]}'
        artifacts = stage / 'artifacts'; artifacts.mkdir()
        metadata = {'version': version, 'revision': args.revision, 'sourceArchiveSha256': sha(args.source_archive)}
        (source / 'RELEASE_SOURCE.json').write_text(json.dumps(metadata, indent=2) + '\n')
        source_name = f'grimarr-{version}-source.tar.gz'
        source_bundle(source, artifacts / source_name, version, epoch)
        print('Building exact release image:', image, flush=True)
        subprocess.run(['docker', 'build', '--pull', '--build-arg', 'GRIMARR_VERSION=' + version, '--build-arg', 'SOURCE_REVISION=' + args.revision, '-t', image, str(source)], check=True)
        info = json.loads(subprocess.check_output(['docker', 'image', 'inspect', image]))[0]
        require(info['Os'] == 'linux' and info['Architecture'] == 'amd64', 'Only Linux/amd64 is verified for this release.')
        require(info['Config']['Labels']['org.opencontainers.image.revision'] == args.revision and info['Config']['Labels']['org.opencontainers.image.version'] == version, 'Image metadata does not match source.')
        image_name = f'grimarr-{version}-linux-amd64.docker.tar.gz'
        raw_image = stage / 'image.tar'
        subprocess.run(['docker', 'save', '--output', str(raw_image), image], check=True)
        cache = args.cache_dir.absolute(); cache.mkdir(parents=True, exist_ok=True)
        temp = stage / 'scan-tmp'; temp.mkdir()
        scanner = ['docker', 'run', '--rm', '--user', f'{os.getuid()}:{os.getgid()}', '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges:true', '--read-only', '-v', str(raw_image) + ':/input/image.tar:ro', '-v', str(cache) + ':/cache', '-v', str(temp) + ':/tmp', '-v', str(artifacts) + ':/report', SCANNER]
        subprocess.run([*scanner, 'image', '--quiet', '--input', '/input/image.tar', '--cache-dir', '/cache', '--scanners', 'vuln,secret', '--format', 'json', '--output', '/report/vulnerabilities.json', '--timeout', '10m'], check=True)
        report = json.loads((artifacts / 'vulnerabilities.json').read_text())
        vulnerabilities = [v for result in report.get('Results', []) for v in result.get('Vulnerabilities') or []]
        secrets = [s for result in report.get('Results', []) for s in result.get('Secrets') or []]
        counts = {level: sum(v['Severity'] == level for v in vulnerabilities) for level in ('CRITICAL', 'HIGH', 'MEDIUM', 'LOW', 'UNKNOWN')}
        require(not counts['CRITICAL'] and not counts['HIGH'] and not secrets, 'Critical/high vulnerability or embedded-secret release gate failed.')
        subprocess.run([*scanner, 'convert', '--format', 'cyclonedx', '--output', '/report/sbom.cdx.json', '/report/vulnerabilities.json'], check=True)
        with raw_image.open('rb') as src, (artifacts / image_name).open('xb') as raw, gzip.GzipFile(filename='', mode='wb', fileobj=raw, mtime=0, compresslevel=1) as compressed:
            shutil.copyfileobj(src, compressed)
        for name in ('LICENSE', 'THIRD_PARTY_NOTICES.md', 'CHANGELOG.md', 'RELEASE.md', 'PASSWORDS.md'):
            shutil.copyfile(source / name, artifacts / name)
        compose = (source / 'compose.yml').read_text().replace('    build: .\n', '').replace('${GRIMARR_IMAGE:-grimarr:local}', '${GRIMARR_IMAGE:-' + image + '}')
        (artifacts / 'compose.release.yml').write_text(compose)
        (artifacts / 'release.env.example').write_text((source / '.env.example').read_text().replace('GRIMARR_IMAGE=grimarr:local', 'GRIMARR_IMAGE=' + image))
        manifest = {'format': 'grimarr-release-v1', **metadata, 'platform': 'linux/amd64', 'image': image, 'imageId': info['Id'], 'sourceArchive': source_name, 'imageArchive': image_name, 'scanCounts': counts, 'embeddedSecrets': len(secrets), 'uniqueCves': len({v['VulnerabilityID'] for v in vulnerabilities}), 'scanner': SCANNER, 'status': 'release-candidate' if '-' in version else 'release', 'signature': 'unsigned; SHA-256 provides integrity, not publisher authentication'}
        (artifacts / 'release.json').write_text(json.dumps(manifest, indent=2) + '\n')
        (artifacts / 'SHA256SUMS').write_text(''.join(sha(p) + '  ' + p.name + '\n' for p in sorted(artifacts.iterdir())))
        verify(artifacts)
        output.mkdir()
        try:
            os.rename(artifacts, output)
        except BaseException:
            output.rmdir()
            raise
        print('Verified release created:', output, flush=True)
    finally:
        shutil.rmtree(stage)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest='command', required=True)
    p = commands.add_parser('build')
    p.add_argument('--source-archive', required=True, type=Path, help='Unprefixed git archive of the exact source revision.')
    p.add_argument('--revision', required=True)
    p.add_argument('--output', required=True, type=Path)
    p.add_argument('--cache-dir', required=True, type=Path)
    p = commands.add_parser('verify')
    p.add_argument('directory', type=Path)
    args = parser.parse_args()
    if args.command == 'build':
        build(args)
    else:
        manifest = verify(args.directory)
        print('All release checksums and manifest gates passed:', manifest['version'], manifest['revision'])


if __name__ == '__main__':
    main()
