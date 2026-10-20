import importlib.util
from pathlib import Path
import subprocess
import shutil
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('version_guard', ROOT / 'scripts/version_guard.py')
guard = importlib.util.module_from_spec(spec)
spec.loader.exec_module(guard)


class VersionGuardTests(unittest.TestCase):
    def test_classification_and_prerelease(self):
        for message, expected in [('fix: layout', '0.3.1-alpha.1'),
                                  ('feat(viewer): export', '0.4.0-alpha.1'),
                                  ('feat!: incompatible config', '1.0.0-alpha.1'),
                                  ('fix: config\n\nBREAKING CHANGE: schema', '1.0.0-alpha.1')]:
            self.assertEqual(guard.next_version('0.3.0-alpha.7', guard.level_for(message)), expected)
        self.assertEqual(guard.next_version('1.2.3', 'patch'), '1.2.4')
        with self.assertRaises(ValueError):
            guard.validate_change('0.3.0-alpha.1', '0.3.0-alpha.2', 'fix: UI')
        with self.assertRaises(ValueError):
            guard.validate_change('0.3.0-alpha.1', '0.3.1-alpha.1', 'feat: export')

    def test_real_index_auto_bump_and_readonly_check(self):
        with tempfile.TemporaryDirectory(dir=ROOT / 'tmp') as temp:
            root = Path(temp)
            def git(*args):
                return guard.git(root, *args)
            git('init', '-q')
            git('config', 'user.name', 'Version Test')
            git('config', 'user.email', 'test@example.invalid')
            git('config', 'commit.gpgsign', 'false')
            git('config', 'core.hooksPath', 'unused-hooks')
            (root / 'VERSION').write_text('0.3.0-alpha.1\n')
            (root / 'src').mkdir()
            (root / 'src/code.cs').write_text('before')
            git('add', '.')
            git('commit', '-qm', 'initial')
            (root / 'src/code.cs').write_text('after')
            git('add', '.')
            guard.prepare(root, 'feat: add feature')
            self.assertEqual(git('show', ':VERSION'), '0.4.0-alpha.1')
            guard.prepare(root, 'feat: add feature')  # 重试不能重复升级
            self.assertEqual(git('show', ':VERSION'), '0.4.0-alpha.1')
            git('commit', '-qm', 'feat: add feature')
            guard.check(root, 'HEAD')
            (root / 'src/code.cs').write_text('another')
            git('add', '.')
            (root / 'VERSION').write_text('0.9.0-alpha.1\n')
            with self.assertRaises(ValueError):
                guard.prepare(root, 'fix: another')
            (root / 'VERSION').write_text('0.4.0-alpha.1\n')
            git('commit', '-qm', 'fix: bypass hook')
            with self.assertRaises(ValueError):
                guard.check(root, 'HEAD')

    def test_scope(self):
        self.assertFalse(guard.product_changed(['README.md', 'tests/test_ui.py']))
        self.assertTrue(guard.product_changed(['src/old.cs']))  # 删除也必须升版
        self.assertTrue(guard.product_changed(['scripts/package_client.py']))

    def test_real_hook_includes_version_in_commit(self):
        with tempfile.TemporaryDirectory(dir=ROOT / 'tmp') as temp:
            root = Path(temp)
            def git(*args):
                return guard.git(root, *args)
            git('init', '-q')
            git('config', 'user.name', 'Hook Test')
            git('config', 'user.email', 'test@example.invalid')
            git('config', 'commit.gpgsign', 'false')
            (root / 'VERSION').write_text('0.3.0-alpha.1\n')
            (root / 'src').mkdir()
            (root / 'src/code.cs').write_text('before')
            git('add', '.')
            git('commit', '-qm', 'initial')
            (root / 'scripts').mkdir()
            shutil.copy2(ROOT / 'scripts/version_guard.py', root / 'scripts/version_guard.py')
            (root / '.hooks').mkdir()
            hook = root / '.hooks/pre-commit'
            hook.write_text('#!/bin/sh\nexec python scripts/version_guard.py --prepare\n')
            hook.chmod(0o755)
            git('config', 'core.hooksPath', '.hooks')
            (root / 'src/code.cs').write_text('after')
            git('add', '.')
            git('commit', '-qm', 'fix: verify actual commit')
            self.assertEqual(git('show', 'HEAD:VERSION'), '0.3.1-alpha.1')
            self.assertEqual(git('status', '--porcelain'), '')
