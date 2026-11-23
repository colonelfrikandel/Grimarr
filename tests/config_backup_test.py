"""Config archive safety tests; no Docker or external services required."""
import importlib.util
from contextlib import closing
import io
import json
import os
from pathlib import Path
import sqlite3
import tarfile
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('config_backup', Path(__file__).resolve().parents[1] / 'scripts/config_backup.py')
tool = importlib.util.module_from_spec(spec)
spec.loader.exec_module(tool)


class Archives(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.config = self.root / 'config'
        self.config.mkdir(mode=0o700)
        with closing(sqlite3.connect(self.config / 'grimarr.db')) as db, db:
            for name in ('settings', 'books', 'activity'):
                db.execute('CREATE TABLE ' + name + ' (json TEXT)')
            db.execute('INSERT INTO books VALUES (?)', ('{"title":"Restore fixture"}',))
        (self.config / 'keys').mkdir()
        (self.config / 'keys/key.xml').write_text('fixture key bytes')
        os.chmod(self.config / 'keys/key.xml', 0o600)
        self.archive = self.root / 'config.tar.gz'

    def write(self, transform=lambda m: m, extra=None, omit=None):
        manifest = {'format': 'grimarr-config-v1', 'imageTag': 'fixture:v1', 'entries': tool.inventory(self.config)}
        manifest = transform(manifest)
        with tarfile.open(self.archive, 'w:gz') as t:
            for p in [self.config, *self.config.rglob('*')]:
                name = 'config' if p == self.config else 'config/' + p.relative_to(self.config).as_posix()
                if name != omit:
                    t.add(p, arcname=name, recursive=False)
            data = json.dumps(manifest).encode()
            m = tarfile.TarInfo('manifest.json'); m.size = len(data)
            t.addfile(m, io.BytesIO(data))
            if extra:
                m, data = extra
                t.addfile(m, io.BytesIO(data) if m.isfile() else None)

    def test_round_trip_contents_and_private_modes(self):
        self.write()
        target = self.root / 'restored'
        tool.restore(self.archive, target)
        self.assertEqual(tool.inventory(target), tool.inventory(self.config))
        tool.validate_config(target)

    def test_existing_destination_is_untouched(self):
        self.write()
        with self.assertRaises(ValueError):
            tool.restore(self.archive, self.config)
        self.assertEqual((self.config / 'keys/key.xml').read_text(), 'fixture key bytes')

    def test_changed_file_checksum_rejected_before_restore(self):
        def wrong(m):
            m['entries']['config/grimarr.db']['sha256'] = '0' * 64
            return m
        self.write(transform=wrong)
        with self.assertRaises(ValueError):
            tool.restore(self.archive, self.root / 'restored')
        self.assertFalse((self.root / 'restored').exists())

    def test_missing_key_member_rejected(self):
        self.write(omit='config/keys/key.xml')
        with self.assertRaises(ValueError):
            tool.verify(self.archive)

    def test_traversal_absolute_link_special_and_duplicate_rejected(self):
        for name, kind in [('../escape', tarfile.REGTYPE), ('/escape', tarfile.REGTYPE), ('config/link', tarfile.SYMTYPE), ('config/hardlink', tarfile.LNKTYPE), ('config/pipe', tarfile.FIFOTYPE), ('config/grimarr.db', tarfile.REGTYPE)]:
            with self.subTest(name=name):
                m = tarfile.TarInfo(name); m.type = kind; m.linkname = '/etc/passwd'
                self.write(extra=(m, b''))
                with self.assertRaises(ValueError):
                    tool.verify(self.archive)

    def test_truncated_gzip_rejected_without_destination(self):
        self.write()
        self.archive.write_bytes(self.archive.read_bytes()[:80])
        with self.assertRaises((EOFError, tarfile.TarError)):
            tool.restore(self.archive, self.root / 'restored')
        self.assertFalse((self.root / 'restored').exists())

    def test_encrypted_config_without_keys_is_incomplete(self):
        (self.config / 'keys/key.xml').unlink()
        with closing(sqlite3.connect(self.config / 'grimarr.db')) as db, db:
            db.execute('INSERT INTO settings VALUES (?)', ('{"secret":"protected:fixture"}',))
        with self.assertRaises(ValueError):
            tool.validate_config(self.config)

    def test_unsupported_format_and_oversize_rejected(self):
        for transform in [lambda m: {**m, 'format': 'other'}, lambda m: {**m, 'entries': {}}]:
            self.write(transform=transform)
            with self.assertRaises(ValueError):
                tool.verify(self.archive)
        self.write()
        old = tool.MAX_BYTES
        try:
            tool.MAX_BYTES = 1
            with self.assertRaises(ValueError):
                tool.verify(self.archive)
        finally:
            tool.MAX_BYTES = old


if __name__ == '__main__':
    unittest.main()
