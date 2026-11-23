#!/usr/bin/env python3
"""Linux Docker install/upgrade/restore checks. Uses only disposable configs and no service/media mounts."""
import argparse
import http.cookiejar
import importlib.util
import json
import os
from pathlib import Path
import secrets
import shutil
import sqlite3
import subprocess
import tempfile
import time
import urllib.error
import urllib.request

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--image', required=True)
parser.add_argument('--previous-image', help='Optional older Grimarr image to create the upgrade fixture.')
parser.add_argument('--report', type=Path)
parser.add_argument('--compose-file', type=Path, help='Exercise the delivered release Compose instead of the source-build example.')
args = parser.parse_args()
root = Path(tempfile.mkdtemp(prefix='grimarr-install-'))
os.chmod(root, 0o700)
token = secrets.token_hex(5)
name = 'grimarr-install-' + token
password = secrets.token_urlsafe(24)
env = root / 'app.env'
env.write_text('GRIMARR_PASSWORD=' + password + '\nGRIMARR_CONFIG=/config\n')
os.chmod(env, 0o600)
passed = []
source_root = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('config_backup', source_root / 'scripts/config_backup.py')
tool = importlib.util.module_from_spec(spec); spec.loader.exec_module(tool)
compose_args = None


def check(label, condition=True):
    assert condition, label
    passed.append(label); print('PASS:', label, flush=True)


def docker(*items, **kw):
    return subprocess.run(['docker', *map(str, items)], check=True, capture_output=True, text=True, **kw)


def inspect():
    return json.loads(docker('inspect', name).stdout)[0]


def remove():
    if subprocess.run(['docker', 'inspect', name], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL).returncode == 0:
        assert inspect()['Config']['Labels'].get('grimarr.install-test') == token
        docker('rm', '-f', name)


def start(config, image=None, **options):
    docker('run', '-d', '--name', name, '--label', 'grimarr.install-test=' + token, '--user', f'{os.getuid()}:{os.getgid()}', '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges:true', '--read-only', '--tmpfs', '/tmp:rw,nosuid,size=64m', '--env-file', env, '-p', '127.0.0.1::8787', '--mount', 'type=bind,source=' + str(config) + ',target=/config' + (',readonly' if options.get('readonly') else ''), image or args.image)


class Client:
    def __init__(self):
        port = inspect()['NetworkSettings']['Ports']['8787/tcp'][0]['HostPort']
        self.base = 'http://127.0.0.1:' + port
        self.opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))

    def api(self, route, data=None, method=None):
        r = urllib.request.Request(self.base + '/api/' + route, data=None if data is None else json.dumps(data).encode(), method=method, headers={'X-Grimarr': '1', 'Content-Type': 'application/json'})
        with self.opener.open(r, timeout=10) as response:
            return json.loads(response.read() or b'null')

    def ready(self):
        for _ in range(100):
            if not inspect()['State']['Running']:
                raise AssertionError('Install fixture exited before readiness')
            try:
                with urllib.request.urlopen(self.base + '/health', timeout=1) as r:
                    if r.status == 200:
                        self.api('login', {'password': password})
                        return self
            except (urllib.error.URLError, ConnectionError):
                pass
            time.sleep(0.1)
        raise AssertionError('Install fixture never became ready')


def not_ready(label):
    for _ in range(100):
        if not inspect()['State']['Running']:
            check(label); return
        time.sleep(0.1)
    raise AssertionError(label + ': invalid config was accepted')


try:
    config = root / 'config'; config.mkdir(mode=0o700)
    data = root / 'data'; data.mkdir()
    compose_env = root / 'compose.env'
    compose_env.write_text(env.read_text() + f'PUID={os.getuid()}\nPGID={os.getgid()}\nGRIMARR_IMAGE={args.image}\nGRIMARR_CONFIG_DIR={config}\nGRIMARR_DATA_DIR={data}\nGRIMARR_BIND_IP=127.0.0.1\nGRIMARR_PORT=0\n')
    override = root / 'compose.override.json'
    override.write_text(json.dumps({'services': {'grimarr': {'container_name': name, 'labels': {'grimarr.install-test': token}}}}))
    compose_args = ['compose', '--project-name', name, '--env-file', str(compose_env), '-f', str(args.compose_file or source_root / 'compose.yml'), '-f', str(override)]
    config.rmdir()
    missing = subprocess.run(['docker', *compose_args, 'up', '-d', '--no-build', 'grimarr'], capture_output=True, text=True)
    check('Repository Compose rejects missing bind directories instead of creating root-owned storage', missing.returncode != 0 and not config.exists())
    remove(); config.mkdir(mode=0o700)
    docker('run', '--rm', '-i', '--user', f'{os.getuid()}:{os.getgid()}', '--env', 'GRIMARR_CONFIG=/config', '--mount', 'type=bind,source=' + str(config) + ',target=/config', args.image, 'password', '--stdin', input=json.dumps({'password': password, 'confirmation': password}))
    docker(*compose_args, 'up', '-d', '--no-build', 'grimarr')
    c = Client().ready()
    release = c.api('system')
    labels = inspect()['Config']['Labels']
    check('Installed API reports the packaged version and source revision', release['version'] == (source_root / 'VERSION').read_text().strip() and release['revision'] == labels['org.opencontainers.image.revision'])
    try:
        urllib.request.urlopen(c.base + '/api/system')
        raise AssertionError('Build information should require owner authentication')
    except urllib.error.HTTPError as error:
        check('Build information is restricted to authenticated owners', error.code == 401)
    check('Repository Compose installs as configured non-root UID with dropped capabilities and private config', inspect()['Config']['User'] == f'{os.getuid()}:{os.getgid()}' and inspect()['HostConfig']['CapDrop'] == ['ALL'])
    check('Fresh install has an empty library and automation disabled', c.api('books') == [] and not c.api('settings')['settings']['automationEnabled'])
    with urllib.request.urlopen(c.base) as response:
        html = response.read().decode()
    import re
    asset = re.search(r'src="([^"]+\.js)"', html)[1]
    with urllib.request.urlopen(c.base + '/' + asset.lstrip('/')) as response:
        check('Fresh installed image serves HTML and its hashed frontend asset', response.status == 200 and len(response.read()) > 1000)
    remove()

    # Create encrypted workflow state with the actual previous image where supplied.
    legacy = root / 'legacy config'; legacy.mkdir(mode=0o700)
    start(legacy, args.previous_image)
    check('Custom non-root install supports read-only container root', inspect()['HostConfig']['ReadonlyRootfs'])
    c = Client().ready()
    s = c.api('settings')['settings']
    s.update(automationEnabled=False, manualSelection=True, automaticFailedDownloadRecovery=False,
             prowlarr={'url': 'http://127.0.0.1:9', 'apiKey': 'private-indexer-fixture'},
             qbittorrent={'url': 'http://127.0.0.1:9', 'username': 'fixture', 'password': 'private-download-fixture'},
             audiobookshelf={'url': 'http://127.0.0.1:9', 'apiKey': 'private-library-fixture'}, libraryId='fixture',
             downloadRoot='/data/downloads', libraryRoot='/data/audiobooks')
    c.api('settings', s, 'PUT')
    c.api('books/quick-add', {'title': 'Restore fixture', 'author': 'Fixture author', 'source': 'magnet:?xt=urn:btih:' + 'a' * 40 + '&tr=https%3A%2F%2Ftracker.example%2Fprivate'})
    before = c.api('books'); settings_before = c.api('settings'); activity_before = c.api('activity')
    archive = root / 'before config.tar.gz'
    tool.backup(name, archive)
    check('Backup stops/restarts only its running fixture and publishes mode 0600', inspect()['State']['Running'] and archive.stat().st_mode & 0o777 == 0o600)
    Client().ready()
    tool.verify(archive)
    remove()
    tool.restore(archive, root / 'upgraded')
    start(root / 'upgraded')
    upgraded = Client().ready()
    check('Upgrade retains queued private release identity and complete public book state', upgraded.api('books') == before)
    check('Upgrade preserves settings, decryptable service secrets and activity', upgraded.api('settings') == settings_before and upgraded.api('activity') == activity_before and all(upgraded.api('settings')['secrets'].values()))
    docker('restart', name)
    upgraded = Client().ready()
    check('Restart preserves workflow identity without any duplicate record', upgraded.api('books') == before)
    link = root / 'upgraded/unsafe-link'
    link.symlink_to(root / 'upgraded/grimarr.db')
    try:
        tool.backup(name, root / 'rejected.tar.gz')
        raise AssertionError('Symlink config was backed up')
    except ValueError:
        pass
    finally:
        link.unlink()
    upgraded = Client().ready()
    check('Backup validation failure restarts the original app without publishing an archive', not (root / 'rejected.tar.gz').exists() and upgraded.api('books') == before)
    original_hash = tool.digest(archive)
    for rejected in (archive, root / 'upgraded/nested.tar.gz'):
        try:
            tool.backup(name, rejected)
            raise AssertionError('Unsafe backup destination was accepted')
        except ValueError:
            pass
    check('Backup refuses overwrite and config-contained output without losing existing archive', tool.digest(archive) == original_hash and inspect()['State']['Running'])
    # Save a new checked archive while stopped and ensure backup leaves a stopped app stopped.
    docker('stop', name)
    tool.backup(name, root / 'after.tar.gz')
    check('Backing up a stopped app does not start it', not inspect()['State']['Running'])
    remove()
    tool.restore(root / 'after.tar.gz', root / 'restored')
    start(root / 'restored')
    restored = Client().ready()
    check('New-version backup restores books, preferences, activity and decrypted secrets', restored.api('books') == before and restored.api('settings') == settings_before and restored.api('activity') == activity_before)
    remove()
    tool.restore(archive, root / 'rollback')
    start(root / 'rollback', args.previous_image)
    rollback = Client().ready()
    check('Full pre-upgrade config plus matching old image supports rollback', rollback.api('books') == before and rollback.api('settings') == settings_before)
    remove()

    # These cases must exit before health can advertise a usable restored application.
    for label, mutate in [
        ('Missing protection keys fail startup before healthy status', lambda p: shutil.rmtree(p / 'keys')),
        ('Corrupt session state fails startup before healthy status', lambda p: (p / 'session-state').write_text('invalid session')),
        ('Corrupt database fails startup before healthy status', lambda p: (p / 'grimarr.db').write_bytes(b'not sqlite')),
        ('Missing database with restored protection state fails instead of creating an empty library', lambda p: (p / 'grimarr.db').unlink()),
    ]:
        bad = root / secrets.token_hex(4)
        tool.restore(root / 'after.tar.gz', bad)
        mutate(bad); start(bad); not_ready(label); remove()
    readonly = root / 'readonly'; readonly.mkdir(mode=0o700)
    start(readonly, readonly=True); not_ready('Unwritable config fails startup'); remove()
    password = 'x'
    env.write_text('GRIMARR_PASSWORD=x\nGRIMARR_CONFIG=/config\n')
    short = root / 'short'; short.mkdir(mode=0o700)
    start(short); Client().ready(); check('Single-character password is accepted'); remove()
    check('Invalid fixtures did not change the known-good backup', tool.verify(archive)['format'] == 'grimarr-config-v1')
    if args.report:
        args.report.write_text(json.dumps({'passed': passed, 'image': args.image, 'previousImage': args.previous_image, 'isolation': 'Disposable configs only; no media mounts; automation disabled; no service calls'}, indent=2))
    print(f'{len(passed)} installation/upgrade/restore checks passed.')
finally:
    remove()
    if compose_args:
        docker(*compose_args, 'down')
    assert root.name.startswith('grimarr-install-') and root.parent == Path(tempfile.gettempdir()) and not root.is_symlink()
    shutil.rmtree(root)
