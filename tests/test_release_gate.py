import hashlib
import json
from pathlib import Path
import sys
import tempfile
import unittest
import zipfile

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'scripts'))
from bump_version import bump
from release_gate import ENTRYPOINTS, HARDWARE, REQUIRED, validate_release


class ReleaseGateTests(unittest.TestCase):
    def setUp(self):
        (ROOT / 'tmp').mkdir(exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(dir=ROOT / 'tmp')
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.version = '0.1.0-alpha.0'
        self.commit = 'a' * 40
        self.package = self.root / f'EmbeddedHelper-{self.version}-win-x64.zip'
        # 合成条目只验证发布门，不是可运行客户端或硬件证据。
        with zipfile.ZipFile(self.package, 'w') as archive:
            for entrypoint in ENTRYPOINTS:
                archive.writestr(entrypoint, b'synthetic gate fixture')
        self.digest = hashlib.sha256(self.package.read_bytes()).hexdigest()
        self.manifest = dict(schemaVersion=1, productId='embedded-helper', version=self.version,
                             commit=self.commit, platform='windows', architecture='x64',
                             deployment='self-contained-directory', packageName=self.package.name,
                             packageSha256=self.digest, packageBytes=self.package.stat().st_size)
        (self.root / 'release-manifest.json').write_text(json.dumps(self.manifest), encoding='utf-8')
        (self.root / 'SHA256SUMS').write_text(f'{self.digest}  {self.package.name}\n', encoding='utf-8')
        (self.root / 'release-notes.md').write_text('远程变量、网页RTT切换及其他模板尚未完成完整P0验收', encoding='utf-8')
        self.evidence = dict(schemaVersion=1, version=self.version, commit=self.commit,
                             packageSha256=self.digest, checks=[])
        for identifier in sorted(REQUIRED):
            method = 'hardware' if identifier in HARDWARE else 'clean-windows' if identifier == 'clean-windows' else 'automated'
            self.evidence['checks'].append(dict(id=identifier, status='passed', exitCode=0,
                                               method=method, details='synthetic contract fixture',
                                               equipment='synthetic', toolVersions='synthetic', durationSeconds=1800))
        self.evidence_path = self.root / 'evidence.json'

    def validate(self):
        self.evidence_path.write_text(json.dumps(self.evidence), encoding='utf-8')
        return validate_release(self.root, self.evidence_path, self.version, self.commit)

    def test_complete_contract_and_missing_hardware(self):
        self.assertEqual(self.validate(), [])
        self.evidence['checks'] = [x for x in self.evidence['checks'] if x['id'] != 'g4-remote-flash']
        self.assertTrue(any('g4-remote-flash' in x for x in self.validate()))

    def test_developer_preview_requires_explicit_deferred_evidence(self):
        self.manifest['releaseProfile'] = 'developer-preview'
        (self.root / 'release-manifest.json').write_text(json.dumps(self.manifest), encoding='utf-8')
        self.evidence['releaseProfile'] = 'developer-preview'
        self.evidence['checks'] = [x for x in self.evidence['checks'] if x['id'] not in HARDWARE | {'clean-windows'}]
        self.assertTrue(self.validate())
        self.evidence['deferredChecks'] = sorted(HARDWARE | {'clean-windows'})
        with (self.root / 'release-notes.md').open('a', encoding='utf-8') as stream:
            stream.write('\n开发内测；硬件未验收')
        self.assertEqual(self.validate(), [])
        self.evidence['checks'] = [x for x in self.evidence['checks'] if x['id'] != 'package-smoke']
        self.assertTrue(any('package-smoke' in x for x in self.validate()))

    def test_old_commit_and_changed_package_rejected(self):
        self.evidence['commit'] = 'b' * 40
        self.assertTrue(any('同一版本' in x for x in self.validate()))
        self.evidence['commit'] = self.commit
        self.package.write_bytes(b'changed')
        self.assertTrue(any('摘要' in x for x in self.validate()))

    def test_simulation_failed_exit_short_run_rejected(self):
        check = next(x for x in self.evidence['checks'] if x['id'] == 'serial-1mbps-duplex')
        check['method'] = 'simulation'
        check['durationSeconds'] = 3
        errors = self.validate()
        self.assertTrue(any('验证方法' in x for x in errors))
        self.assertTrue(any('持续时间' in x for x in errors))
        check['exitCode'] = 1
        self.assertTrue(any('真实通过' in x for x in self.validate()))

    def test_missing_input_and_duplicate_checks_rejected(self):
        (self.root / 'release-manifest.json').unlink()
        self.assertTrue(self.validate())
        (self.root / 'release-manifest.json').write_text(json.dumps(self.manifest), encoding='utf-8')
        self.evidence['checks'].append(self.evidence['checks'][0])
        self.assertTrue(any('重复' in x for x in self.validate()))

    def test_traversal_and_missing_entrypoint_rejected(self):
        with zipfile.ZipFile(self.package, 'a') as archive:
            archive.writestr('../escape.exe', b'bad')
        self.manifest['packageSha256'] = hashlib.sha256(self.package.read_bytes()).hexdigest()
        self.manifest['packageBytes'] = self.package.stat().st_size
        (self.root / 'release-manifest.json').write_text(json.dumps(self.manifest), encoding='utf-8')
        (self.root / 'SHA256SUMS').write_text(f"{self.manifest['packageSha256']}  {self.package.name}", encoding='utf-8')
        self.assertTrue(any('ZIP条目' in x for x in self.validate()))
        with zipfile.ZipFile(self.package, 'w') as archive:
            archive.writestr('EmbeddedHelper.exe', b'synthetic fixture')
        self.manifest['packageSha256'] = hashlib.sha256(self.package.read_bytes()).hexdigest()
        self.manifest['packageBytes'] = self.package.stat().st_size
        (self.root / 'release-manifest.json').write_text(json.dumps(self.manifest), encoding='utf-8')
        (self.root / 'SHA256SUMS').write_text(f"{self.manifest['packageSha256']}  {self.package.name}", encoding='utf-8')
        self.assertTrue(any('稳定CLI入口' in x for x in self.validate()))

    def test_version_writer_rejects_invalid_input(self):
        path = self.root / 'VERSION'
        path.write_text(self.version + '\n', encoding='utf-8')
        for invalid in ['1.2', '01.2.3', '1.2.3-01', '1.2.3+meta', self.version]:
            with self.subTest(invalid=invalid), self.assertRaises(ValueError):
                bump(path, invalid)
        self.assertEqual(path.read_text().strip(), self.version)
        self.assertEqual(bump(path, '0.1.0-alpha.1'), self.version)
        self.assertEqual(path.read_text().strip(), '0.1.0-alpha.1')

    def test_single_exe_must_match_the_tested_archive(self):
        executable = self.root / 'EmbeddedHelper.exe'
        executable.write_bytes(b'synthetic single-exe gate fixture')
        with zipfile.ZipFile(self.package, 'w') as archive:
            archive.write(executable, 'EmbeddedHelper.exe')
        self.manifest.update(deployment='self-contained-single-exe', executableName=executable.name,
                             executableSha256=hashlib.sha256(executable.read_bytes()).hexdigest(),
                             executableBytes=executable.stat().st_size,
                             packageSha256=hashlib.sha256(self.package.read_bytes()).hexdigest(),
                             packageBytes=self.package.stat().st_size)
        (self.root / 'release-manifest.json').write_text(json.dumps(self.manifest), encoding='utf-8')
        (self.root / 'SHA256SUMS').write_text(
            f"{self.manifest['packageSha256']}  {self.package.name}\n{self.manifest['executableSha256']}  EmbeddedHelper.exe\n", encoding='utf-8')
        self.evidence['packageSha256'] = self.manifest['packageSha256']
        self.assertEqual(self.validate(), [])
        executable.write_bytes(b'modified')
        self.assertTrue(any('EXE' in x for x in self.validate()))


if __name__ == '__main__':
    unittest.main()
