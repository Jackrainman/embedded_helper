import hashlib
import json
from pathlib import Path
import shutil
import sys
import tempfile
import unittest
import zipfile

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from prototypes.tool_cache import install, resolve, safe_name


class ToolCacheTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(dir=ROOT / 'tmp')
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.portable = self.root / '电脑 A 便携目录'
        self.project = self.root / '中文 工程'
        (self.project / '.embedded_helper').mkdir(parents=True)

    def package(self, version, extra=None, **overrides):
        manifest = dict(manifestSchema=1, toolId='debug-lan', version=version,
                        platform='windows', architecture='x64', cliApiMajor=1,
                        upstreamCommit='7f4b9da49bc458454bfcf8a18277e5ee5ce89d5b',
                        configSchemaRange={'min': 1, 'max': 1}, entrypoint='probe.txt')
        manifest.update(overrides)
        archive = self.root / (version + '.zip')
        with zipfile.ZipFile(archive, 'w') as z:
            z.writestr('manifest.json', json.dumps(manifest))
            z.writestr('probe.txt', version)
            if extra:
                z.writestr(extra, 'bad')
        return archive, hashlib.sha256(archive.read_bytes()).hexdigest()

    def select(self, version, digest):
        lock = {'schemaVersion': 1, 'tools': {'debug-lan': {
            'version': version, 'sha256': digest, 'cliApiMajor': 1}}}
        (self.project / '.embedded_helper/tools.lock.json').write_text(json.dumps(lock), encoding='utf-8')

    def test_two_versions_rollback_move_and_missing(self):
        digests = {}
        for version in ['0.1.0', '0.1.1']:
            package, digest = self.package(version)
            install(package, digest, self.portable / 'data/tools')
            digests[version] = digest
        for version in ['0.1.0', '0.1.1', '0.1.0']:
            self.select(version, digests[version])
            self.assertEqual(resolve(self.project, self.portable).read_text(), version)
        moved = self.root / '电脑 B 移动目录'
        shutil.copytree(self.portable, moved)
        self.assertEqual(resolve(self.project, moved).read_text(), '0.1.0')
        self.select('9.9.9', '0' * 64)
        with self.assertRaises(ValueError):
            resolve(self.project, moved)

    def test_invalid_packages_never_replace_old_version(self):
        package, digest = self.package('0.1.0')
        cache = self.portable / 'data/tools'
        install(package, digest, cache)
        with self.assertRaises(ValueError):
            install(package, digest, cache)
        with self.assertRaises(ValueError):
            install(package, '0' * 64, cache)
        with self.assertRaises(ValueError):
            safe_name('folder\\escape')
        for path in ['../escape', '/absolute', 'C:/escape', 'NUL', 'probe.txt']:
            package, digest = self.package('0.1.1', path)
            with self.assertRaises(ValueError, msg=path):
                install(package, digest, cache)
        package, digest = self.package('0.1.1', cliApiMajor=2)
        with self.assertRaises(ValueError):
            install(package, digest, cache)
        self.assertEqual((cache / '0.1.0/probe.txt').read_text(), '0.1.0')
        self.assertFalse((cache / '0.1.1').exists())
