import importlib.util
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
from unittest.mock import patch

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
        for name in [*verify.DOCS, '.gitignore', '.gitattributes', 'global.json', 'VERSION',
                     'Directory.Build.props', '.editorconfig']:
            shutil.copy2(ROOT / name, self.root / name)
        for name in ['.harness', '.githooks', '.github', 'scripts', 'tests/fixtures']:
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
        for name in ['data/local.json', 'tmp/test.bin', '.venv/example', '.codex/state',
                     'src/App/bin/example.dll', 'src/App/obj/example.json',
                     'test-results/example.trx', '.vs/example', 'tests/Example/TestResults/example.trx']:
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

    def test_dotnet_baseline_and_missing_client(self):
        with patch.object(verify.subprocess, 'run') as process:
            self.assertEqual(verify.verify_client(self.root), 2)
            process.assert_not_called()
        p = self.root / 'global.json'
        sdk = json.loads(p.read_text(encoding='utf-8'))
        sdk['sdk']['rollForward'] = 'latestMajor'
        p.write_text(json.dumps(sdk), encoding='utf-8')
        self.assertTrue(any('C#基线' in x for x in verify.validate(self.root)))

    def test_nuget_lock_and_version_source(self):
        project = self.root / 'src/App/App.csproj'
        project.parent.mkdir(parents=True)
        project.write_text('<Project><PropertyGroup><Version>9.9.9</Version></PropertyGroup></Project>')
        self.assertTrue(any('另设产品版本' in x for x in verify.validate(self.root)))
        project.write_text('<Project />')
        self.assertTrue(any('C#项目' in x for x in verify.validate(self.root)))
        (project.parent / 'packages.lock.json').write_text('{"version": 1, "dependencies": {}}')
        self.assertEqual(verify.validate(self.root), [])

    def test_workflow_pins_and_release_permissions(self):
        p = self.root / '.github/workflows/verify.yml'
        content = p.read_text(encoding='utf-8')
        p.write_text(content.replace('actions/checkout@11bd71901bbe5b1630ceea73d27597364c9af683',
                                     'actions/checkout@v4'), encoding='utf-8')
        self.assertTrue(any('Action未锁定' in x for x in verify.validate(self.root)))
        p.write_text(content.replace('contents: read', 'contents: write'), encoding='utf-8')
        self.assertTrue(any('普通验证不得' in x for x in verify.validate(self.root)))

    def test_generated_output_does_not_enter_document_scan(self):
        output = self.root / 'src/App/obj/dependency.md'
        output.parent.mkdir(parents=True)
        output.write_text('generated dependency documentation')
        self.assertEqual(verify.validate(self.root), [])


if __name__ == '__main__':
    unittest.main()
