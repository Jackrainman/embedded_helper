import importlib.util
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('verify', ROOT / 'scripts/verify.py')
verify = importlib.util.module_from_spec(spec)
spec.loader.exec_module(verify)


class HarnessTests(unittest.TestCase):
    def setUp(self):
        (ROOT / 'tmp').mkdir(exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(dir=ROOT / 'tmp')
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        for name in [*verify.DOCS, '.gitignore', '.gitattributes']:
            shutil.copy2(ROOT / name, self.root / name)
        for name in ['.harness', '.githooks', 'scripts', 'tests/fixtures']:
            shutil.copytree(ROOT / name, self.root / name)

    def test_positive_and_negative_rules(self):
        self.assertEqual(verify.validate(self.root), [])
        p = self.root / 'README.md'
        p.write_text(p.read_text(encoding='utf-8') + '\n[bad](missing.txt)\n', encoding='utf-8')
        self.assertTrue(any('断链' in x for x in verify.validate(self.root)))
        (self.root / 'plan.md').write_text('duplicate')
        self.assertTrue(any('单源' in x for x in verify.validate(self.root)))
        p = self.root / '.harness/todo.json'
        ledger = json.loads(p.read_text(encoding='utf-8'))
        ledger['tasks'][0]['dependsOn'] = [ledger['tasks'][1]['id']]
        p.write_text(json.dumps(ledger), encoding='utf-8')
        self.assertTrue(any('账本' in x for x in verify.validate(self.root)))
        (self.root / 'tests/fixtures/serial.bin').write_bytes(b'corrupt')
        self.assertTrue(any('样本' in x for x in verify.validate(self.root)))

    def test_real_commit_hook_and_ignore_policy(self):
        def run(*args):
            return subprocess.run(['git', '-C', str(self.root), *args], capture_output=True)
        self.assertEqual(run('init', '-q').returncode, 0)
        for key, value in [('user.name', 'Harness Test'), ('user.email', 'fixture@example.invalid'),
                           ('core.hooksPath', '.githooks'), ('commit.gpgsign', 'false')]:
            self.assertEqual(run('config', key, value).returncode, 0)
        for name in ['.vscode/launch.json', '.eide/eide.yml', '.embedded_helper/project.json',
                     '.agents/skills/example/SKILL.md', 'tests/fixtures/example.elf']:
            self.assertEqual(run('check-ignore', '--no-index', name).returncode, 1, name)
        for name in ['data/local.json', 'tmp/test.bin', '.venv/example', '.codex/state']:
            self.assertEqual(run('check-ignore', '--no-index', name).returncode, 0, name)
        bad = self.root / 'plan.md'
        bad.write_text('must reject')
        self.assertEqual(run('add', '.').returncode, 0)
        # 工作树恢复并不改变索引，证明不是只检查工作树。
        bad.unlink()
        rejected = run('commit', '-m', 'negative hook fixture')
        self.assertNotEqual(rejected.returncode, 0)
        self.assertIn(b'FAIL', rejected.stdout + rejected.stderr)
        self.assertNotEqual(run('rev-parse', '--verify', 'HEAD').returncode, 0)
        self.assertEqual(run('add', '-u').returncode, 0)
        accepted = run('commit', '-m', 'positive hook fixture')
        self.assertEqual(accepted.returncode, 0, (accepted.stdout + accepted.stderr).decode(errors='replace'))


if __name__ == '__main__':
    unittest.main()
