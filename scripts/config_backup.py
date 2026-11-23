#!/usr/bin/env python3
"""Offline Grimarr config backups. Python 3.10+, Linux, Docker CLI; no third-party packages."""
import argparse
from contextlib import closing
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import shutil
import sqlite3
import stat
import subprocess
import tarfile
import tempfile
import sys

MAX_BYTES = 8 * 1024**3
MAX_FILES = 10000


def require(condition, message):
    if not condition:
        raise ValueError(message)


def digest(path):
    h = hashlib.sha256()
    with path.open('rb') as f:
        for block in iter(lambda: f.read(1024 * 1024), b''):
            h.update(block)
    return h.hexdigest()


def validate_config(config):
    require((config / 'grimarr.db').is_file(), 'Config is missing grimarr.db.')
    with closing(sqlite3.connect((config / 'grimarr.db').as_uri() + '?mode=ro', uri=True)) as db:
        require(db.execute('PRAGMA integrity_check').fetchone()[0] == 'ok', 'Database integrity check failed.')
        tables = {r[0] for r in db.execute("SELECT name FROM sqlite_master WHERE type='table'")}
        require({'settings', 'books', 'activity'} <= tables, 'Not a complete Grimarr database.')
        private = any('protected:' in r[0] for table in ('settings', 'books') for r in db.execute('SELECT json FROM ' + table))
    if private or (config / 'session-state').exists():
        require(any((config / 'keys').glob('*.xml')), 'Protection keys are missing; back up the complete config.')


def inventory(config):
    result = {}
    for path in [config, *sorted(config.rglob('*'))]:
        s = path.lstat()
        require(stat.S_ISREG(s.st_mode) or stat.S_ISDIR(s.st_mode), 'Config must contain only real files and directories, without links.')
        key = 'config' if path == config else 'config/' + path.relative_to(config).as_posix()
        result[key] = {'kind': 'directory' if path.is_dir() else 'file', 'mode': stat.S_IMODE(s.st_mode), 'uid': s.st_uid, 'gid': s.st_gid}
        if path.is_file():
            result[key].update(size=s.st_size, sha256=digest(path))
    require(len(result) <= MAX_FILES and sum(v.get('size', 0) for v in result.values()) <= MAX_BYTES, 'Config exceeds backup limits.')
    return result


def load_archive(archive):
    """Validate every member before creating a restore directory; never use extractall."""
    members = archive.getmembers()
    require(len(members) <= MAX_FILES + 1, 'Too many archive members.')
    names = [m.name.rstrip('/') for m in members]
    require(len(names) == len(set(names)), 'Duplicate archive members.')
    require(all(m.isfile() or m.isdir() for m in members), 'Archive links and special files are forbidden.')
    require(all(not PurePosixPath(n).is_absolute() and '..' not in PurePosixPath(n).parts and '\\' not in n for n in names), 'Unsafe archive member path.')
    require(sum(m.size for m in members) <= MAX_BYTES, 'Archive exceeds restore size limit.')
    require('manifest.json' in names, 'Archive has no verification manifest.')
    m = members[names.index('manifest.json')]
    require(m.isfile() and m.size <= 4 * 1024**2, 'Invalid manifest.')
    manifest = json.load(archive.extractfile(m))
    require(manifest.get('format') == 'grimarr-config-v1', 'Unsupported backup format.')
    entries = manifest['entries']
    require(set(entries) == set(names) - {'manifest.json'} and entries.get('config', {}).get('kind') == 'directory', 'Archive and manifest do not match.')
    for member, name in zip(members, names):
        if name == 'manifest.json':
            continue
        require(name == 'config' or name.startswith('config/'), 'Member is outside config.')
        e = entries[name]
        require((member.isdir() and e['kind'] == 'directory') or (member.isfile() and e['kind'] == 'file' and member.size == e['size']), 'Member type or size mismatch.')
        require(all(isinstance(e[k], int) and e[k] >= 0 for k in ('mode', 'uid', 'gid')) and e['mode'] <= 0o777, 'Invalid ownership or permissions.')
        if member.isfile():
            h = hashlib.sha256()
            with archive.extractfile(member) as f:
                for block in iter(lambda: f.read(1024 * 1024), b''):
                    h.update(block)
            require(h.hexdigest() == e['sha256'], 'Archive file checksum mismatch.')
    return manifest


def verify(path):
    with tarfile.open(path, 'r:gz') as archive:
        return load_archive(archive)


def backup(container, output):
    output = output.absolute()
    require(output.parent.is_dir() and not output.exists() and not output.is_symlink(), 'Choose a new backup filename in an existing private directory.')
    raw = subprocess.check_output(['docker', 'inspect', container])
    info = json.loads(raw)[0]
    require(not info['State'].get('Paused') and not info['State'].get('Restarting'), 'Container must not be paused or restarting.')
    mounts = [m for m in info['Mounts'] if m['Destination'] == '/config']
    require(len(mounts) == 1 and mounts[0]['Type'] == 'bind', 'Backup requires one /config bind mount.')
    config = Path(mounts[0]['Source'])
    require(config.is_dir() and config.resolve() == config and config != Path('/'), 'Config must be a real absolute directory.')
    require(not output.resolve().is_relative_to(config), 'Store the backup outside the config directory.')
    was_running = info['State']['Running']
    temporary = None
    try:
        if was_running:
            subprocess.run(['docker', 'stop', '--time', '60', info['Id']], check=True, stdout=subprocess.DEVNULL)
        require(not json.loads(subprocess.check_output(['docker', 'inspect', info['Id']]))[0]['State']['Running'], 'Container must remain stopped during backup.')
        # Only this container may write the config. Other writers require operator coordination.
        inventory(config)  # Reject links before opening database or key files.
        validate_config(config)
        entries = inventory(config)
        manifest = {'format': 'grimarr-config-v1', 'imageId': info['Image'], 'imageTag': info['Config']['Image'], 'user': info['Config']['User'], 'entries': entries}
        fd, name = tempfile.mkstemp(prefix='.grimarr-backup-', dir=output.parent)
        os.close(fd)
        temporary = Path(name)
        with tarfile.open(temporary, 'w:gz') as archive:
            archive.add(config, arcname='config', recursive=True)
            data = json.dumps(manifest, sort_keys=True).encode()
            import io
            m = tarfile.TarInfo('manifest.json')
            m.size, m.mode = len(data), 0o600
            archive.addfile(m, io.BytesIO(data))
        verify(temporary)
        with temporary.open('rb') as f:
            os.fsync(f.fileno())
        os.link(temporary, output)  # Atomic publication, cannot replace an existing backup.
        print('Verified private config backup created. Image: ' + manifest['imageTag'])
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)
        if was_running:
            subprocess.run(['docker', 'start', info['Id']], check=True, stdout=subprocess.DEVNULL)


def restore(archive_path, destination):
    destination = destination.absolute()
    require(destination.parent.is_dir() and not destination.exists() and not destination.is_symlink(), 'Restore requires a new destination in an existing parent directory.')
    with tarfile.open(archive_path, 'r:gz') as archive:
        manifest = load_archive(archive)
        stage = Path(tempfile.mkdtemp(prefix='.grimarr-restore-', dir=destination.parent))
        try:
            entries = manifest['entries']
            for name, entry in sorted(entries.items(), key=lambda pair: (len(PurePosixPath(pair[0]).parts), pair[0])):
                path = stage / name
                require(path.resolve().is_relative_to(stage), 'Unsafe restore path.')
                if entry['kind'] == 'directory':
                    path.mkdir(mode=0o700)
                else:
                    with archive.extractfile(name) as src, path.open('xb') as target:
                        shutil.copyfileobj(src, target)
            validate_config(stage / 'config')
            # Validate before applying restrictive directory modes. Preserve numeric ownership.
            for name, entry in sorted(entries.items(), reverse=True):
                path = stage / name
                s = path.stat()
                if (s.st_uid, s.st_gid) != (entry['uid'], entry['gid']):
                    os.chown(path, entry['uid'], entry['gid'])
                os.chmod(path, entry['mode'])
            # Reserve the final name without replacing any operator data, even after a race.
            destination.mkdir(mode=0o700)
            try:
                # Replace only the empty directory this process just reserved.
                os.rename(stage / 'config', destination)
            except BaseException:
                destination.rmdir()
                raise
            print('Verified config restored into a new directory. Use matching image: ' + manifest['imageTag'])
        finally:
            shutil.rmtree(stage)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest='command', required=True)
    p = commands.add_parser('backup', help='Briefly stop this container, verify/archive its complete config, restart if previously running.')
    p.add_argument('--container', required=True)
    p.add_argument('--output', required=True, type=Path)
    p = commands.add_parser('verify', help='Check archive paths, file hashes, completeness and size limits without extracting.')
    p.add_argument('archive', type=Path)
    p = commands.add_parser('restore', help='Restore into a new directory; never start an app or overwrite existing data.')
    p.add_argument('archive', type=Path)
    p.add_argument('--destination', required=True, type=Path)
    args = parser.parse_args()
    try:
        if args.command == 'backup':
            backup(args.container, args.output)
        elif args.command == 'restore':
            restore(args.archive, args.destination)
        else:
            result = verify(args.archive)
            print('Archive checks passed. Matching image: ' + result['imageTag'])
    except (ValueError, OSError, EOFError, KeyError, TypeError, tarfile.TarError, sqlite3.Error, subprocess.SubprocessError) as ex:
        # Never emit archive/database contents, environment, credentials or raw integration URLs.
        print('Config operation failed (' + type(ex).__name__ + '). Check paths, permissions, archive integrity and complete config/keys. Existing config is not replaced.', file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
