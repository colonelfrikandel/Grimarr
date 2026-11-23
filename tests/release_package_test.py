"""Release checksum, source exclusion and deterministic archive tests, without Docker."""
import importlib.util
import io
import json
from pathlib import Path
import tarfile
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('release', Path(__file__).resolve().parents[1] / 'scripts/release.py')
tool = importlib.util.module_from_spec(spec); spec.loader.exec_module(tool)


class Packaging(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(); self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.bundle = self.root / 'bundle'; self.bundle.mkdir()
        self.manifest = {'format': 'grimarr-release-v1', 'revision': 'a' * 40, 'platform': 'linux/amd64', 'sourceArchive': 'source.tar.gz', 'imageArchive': 'image.tar.gz', 'scanCounts': {'CRITICAL': 0, 'HIGH': 0}, 'embeddedSecrets': 0}
        for name in ('source.tar.gz', 'image.tar.gz', 'vulnerabilities.json', 'compose.release.yml', 'release.env.example', 'LICENSE', 'THIRD_PARTY_NOTICES.md', 'CHANGELOG.md', 'RELEASE.md'):
            (self.bundle / name).write_text('fixture')
        (self.bundle / 'sbom.cdx.json').write_text('{"bomFormat":"CycloneDX"}')
        self.finish()

    def finish(self):
        (self.bundle / 'release.json').write_text(json.dumps(self.manifest))
        (self.bundle / 'SHA256SUMS').write_text(''.join(tool.sha(p) + '  ' + p.name + '\n' for p in sorted(self.bundle.iterdir()) if p.name != 'SHA256SUMS'))

    def test_complete_fixture_verifies(self):
        self.assertEqual(tool.verify(self.bundle)['revision'], 'a' * 40)

    def test_tampered_missing_and_extra_files_rejected(self):
        path = self.bundle / 'LICENSE'; path.write_text('changed')
        with self.assertRaises(ValueError): tool.verify(self.bundle)
        self.finish(); path.unlink()
        with self.assertRaises(ValueError): tool.verify(self.bundle)
        path.write_text('restored'); self.finish()
        (self.bundle / 'unexpected.txt').write_text('private')
        with self.assertRaises(ValueError): tool.verify(self.bundle)

    def test_unsafe_duplicate_and_truncated_checksums_rejected(self):
        path = self.bundle / 'SHA256SUMS'; text = path.read_text()
        for bad in [text + text.splitlines()[0] + '\n', 'a' * 64 + '  ../escape\n', '', text[:30]]:
            path.write_text(bad)
            with self.assertRaises(ValueError): tool.verify(self.bundle)

    def test_high_findings_secrets_and_wrong_platform_block_release(self):
        for key, value in [('scanCounts', {'CRITICAL': 0, 'HIGH': 1}), ('embeddedSecrets', 1), ('platform', 'linux/arm64')]:
            before = self.manifest[key]; self.manifest[key] = value; self.finish()
            with self.assertRaises(ValueError): tool.verify(self.bundle)
            self.manifest[key] = before

    def test_private_runtime_and_unsafe_source_entries_rejected_before_extraction(self):
        for name in ['.env', '.env.production', 'config/grimarr.db', '.tools/private', 'frontend/node_modules/private', 'src/Grimarr/bin/Grimarr.dll', '../escape', '/escape', 'README.md\nprivate']:
            archive = self.root / 'source.tar'
            with tarfile.open(archive, 'w') as t:
                m = tarfile.TarInfo(name); m.size = 1; t.addfile(m, io.BytesIO(b'x'))
            target = self.root / 'extracted'; target.mkdir(exist_ok=True)
            with self.assertRaises(ValueError): tool.safe_source(archive, target)
            self.assertEqual(list(target.iterdir()), [])

    def test_source_symlinks_rejected(self):
        archive = self.root / 'source.tar'
        with tarfile.open(archive, 'w') as t:
            m = tarfile.TarInfo('README.md'); m.type = tarfile.SYMTYPE; m.linkname = '/etc/passwd'; t.addfile(m)
        with self.assertRaises(ValueError): tool.safe_source(archive, self.root)

    def test_source_bundle_has_stable_bytes_and_normalized_ownership(self):
        source = self.root / 'source'; source.mkdir(); (source / 'VERSION').write_text('1.0.0-rc.1\n')
        a = self.root / 'a.tar.gz'; b = self.root / 'b.tar.gz'
        tool.source_bundle(source, a, '1.0.0-rc.1', 100)
        (source / 'VERSION').touch()
        tool.source_bundle(source, b, '1.0.0-rc.1', 100)
        self.assertEqual(tool.sha(a), tool.sha(b))
        with tarfile.open(a) as t:
            self.assertTrue(all(m.uid == 0 and m.gid == 0 and m.mtime == 100 for m in t.getmembers()))


if __name__ == '__main__': unittest.main()
