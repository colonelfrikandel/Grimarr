#!/usr/bin/env python3
"""Verify a packaged release and round-trip its unused image through Docker save/load."""
import argparse
import importlib.util
import json
from pathlib import Path
import secrets
import subprocess
import tarfile
import tempfile

spec = importlib.util.spec_from_file_location('release', Path(__file__).resolve().parents[1] / 'scripts/release.py')
tool = importlib.util.module_from_spec(spec); spec.loader.exec_module(tool)
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--directory', required=True, type=Path)
parser.add_argument('--report', type=Path)
args = parser.parse_args(); manifest = tool.verify(args.directory); passed = []


def check(label, condition=True):
    assert condition, label
    passed.append(label); print('PASS:', label, flush=True)


check('All delivered files match SHA-256 checksums and release gates')
with tarfile.open(args.directory / manifest['sourceArchive']) as archive:
    prefix = 'grimarr-' + manifest['version'] + '/'
    names = {m.name for m in archive.getmembers()}
    check('Corresponding source contains version, backend/frontend, build/lock files and license', all(prefix + name in names for name in ['VERSION', 'Dockerfile', 'src/Grimarr/Program.cs', 'src/Grimarr/packages.lock.json', 'frontend/package-lock.json', 'scripts/config_backup.py', 'LICENSE']))
    provenance = json.load(archive.extractfile(prefix + 'RELEASE_SOURCE.json'))
    check('Source provenance matches exact packaged revision', provenance['revision'] == manifest['revision'])
    check('Source archive contains no runtime config, secrets or generated dependencies', all(not (set(Path(name).parts[1:]) & tool.FORBIDDEN) for name in names))
check('Delivered Compose selects packaged image without requiring a source build', 'build:' not in (args.directory / 'compose.release.yml').read_text() and manifest['image'] in (args.directory / 'compose.release.yml').read_text())
check('Delivered example selects packaged image without storing a plaintext password', 'GRIMARR_IMAGE=' + manifest['image'] in (args.directory / 'release.env.example').read_text() and 'GRIMARR_PASSWORD=' not in (args.directory / 'release.env.example').read_text())
image = manifest['image']
info = json.loads(subprocess.check_output(['docker', 'image', 'inspect', image]))[0]
assert info['Id'] == manifest['imageId'] and info['Config']['Labels']['org.opencontainers.image.revision'] == manifest['revision']
assert not subprocess.check_output(['docker', 'ps', '-aq', '--filter', 'ancestor=' + info['Id']]).strip(), 'Only an unused candidate image can be round-tripped'
subprocess.run(['docker', 'image', 'rm', image], check=True, stdout=subprocess.DEVNULL)
subprocess.run(['docker', 'load', '--input', str(args.directory / manifest['imageArchive'])], check=True, stdout=subprocess.DEVNULL)
loaded = json.loads(subprocess.check_output(['docker', 'image', 'inspect', image]))[0]
check('Compressed exported image loads to the identical image ID and version/revision labels', loaded['Id'] == manifest['imageId'] and loaded['Config']['Labels'] == info['Config']['Labels'])
name = 'grimarr-release-notices-' + secrets.token_hex(5)
try:
    subprocess.run(['docker', 'create', '--name', name, image], check=True, stdout=subprocess.DEVNULL)
    with tempfile.TemporaryDirectory() as folder:
        for source, filename in [('/app/LICENSE', 'LICENSE'), ('/app/THIRD_PARTY_NOTICES.md', 'NOTICES'), ('/app/wwwroot/third-party-licenses.txt', 'frontend-licenses')]:
            subprocess.run(['docker', 'cp', name + ':' + source, str(Path(folder) / filename)], check=True, stdout=subprocess.DEVNULL)
        check('Exported runtime retains GPL license and full frontend dependency notices', 'GNU GENERAL PUBLIC LICENSE' in (Path(folder) / 'LICENSE').read_text() and all(package in (Path(folder) / 'frontend-licenses').read_text() for package in ['react', 'react-dom', 'lucide-react', 'scheduler', 'classnames']))
finally:
    subprocess.run(['docker', 'rm', name], check=True, stdout=subprocess.DEVNULL)
if args.report: args.report.write_text(json.dumps({'passed': passed, 'version': manifest['version'], 'revision': manifest['revision'], 'image': image, 'imageId': manifest['imageId']}, indent=2))
print(f'{len(passed)} release artifact checks passed.')
