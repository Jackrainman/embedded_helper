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
    def test_python_syntax_error_reports_source_line_and_nonzero(self):
        source = self.root / 'scripts/bad.py'
        source.parent.mkdir(exist_ok=True)
        source.write_text('def broken(:\n    pass\n')
        errors = verify.python_syntax_errors(self.root)
        self.assertTrue(any('bad.py:1:' in error for error in errors))
        with patch.object(verify, 'ROOT', self.root), patch.object(verify, 'validate', return_value=[]), patch.object(verify, 'environment'), patch('sys.argv', ['verify.py']), patch('builtins.print') as output:
            self.assertEqual(verify.main(), 1)
        self.assertTrue(any('bad.py:1:' in str(call) for call in output.call_args_list))

    def setUp(self):
        (ROOT / 'tmp').mkdir(exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(dir=ROOT / 'tmp')
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        for name in [*verify.DOCS, '.gitignore', '.gitattributes', 'global.json', 'VERSION',
                     'Directory.Build.props', '.editorconfig']:
            target = self.root / name
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(ROOT / name, target)
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
        fixture = dict(id='FIX-01', title='isolated dependency fixture', status='todo', priority='P2',
                       component='Harness', refs=['AC-13'], dependsOn=[], testIds=['AT-14'],
                       doneWhen='fixture only', reason='', resumeWhen='')
        ledger['tasks'] = [dict(fixture, id='FIX-02'), fixture]
        ledger['tasks'][0]['dependsOn'] = [fixture['id']]
        ledger['tasks'][1]['dependsOn'] = [ledger['tasks'][0]['id']]
        p.write_text(json.dumps(ledger), encoding='utf-8')
        self.assertTrue(any('账本' in x for x in verify.validate(self.root)))
        (self.root / 'tests/fixtures/serial.bin').write_bytes(b'corrupt')
        self.assertTrue(any('样本' in x for x in verify.validate(self.root)))
        # 规格文档已迁入.docs，留在根目录视为副本漂移。
        shutil.copy2(self.root / verify.REQUIREMENTS, self.root / '01_需求规格说明书.md')
        self.assertIn('文档不在单源清单: 01_需求规格说明书.md', verify.validate(self.root))

    def test_missing_acceptance_ledger_is_rejected(self):
        (self.root / '.harness/test.json').unlink(missing_ok=True)
        self.assertTrue(any('验收账本' in error for error in verify.validate(self.root)))

    def test_blocked_acceptance_requires_a_reason(self):
        path = self.root / '.harness/test.json'
        ledger = json.loads(path.read_text(encoding='utf-8'))
        ledger['checks'][0].update(status='blocked', reason='', resumeWhen='')
        path.write_text(json.dumps(ledger), encoding='utf-8')
        self.assertTrue(any('受阻/暂缓须填写原因和恢复条件' in error
                            for error in verify.validate(self.root)))

    def test_real_commit_hook_and_ignore_policy(self):
        def run(*args):
            return subprocess.run(['git', '-C', str(self.root), *args], capture_output=True)
        self.assertEqual(run('init', '-q').returncode, 0)
        for key, value in [('user.name', 'Harness Test'), ('user.email', 'fixture@example.invalid'),
                           ('core.hooksPath', '.githooks'), ('commit.gpgsign', 'false')]:
            self.assertEqual(run('config', key, value).returncode, 0)
        for name in ['.vscode/launch.json', '.eide/eide.yml', '.embedded_helper/project.json',
                     '.agents/skills/example/SKILL.md', '.agents/skills/ui-ux-pro-max/data/styles.csv',
                     '.agents/skills/impeccable/scripts/data/font-index.json',
                     '.agents/skills/impeccable/source.json', '.agents/skills/impeccable/LICENSE',
                     'tests/fixtures/example.elf']:
            self.assertEqual(run('check-ignore', '--no-index', name).returncode, 1, name)
        for name in ['data/local.json', 'tmp/test.bin', '.venv/example', '.codex/state',
                     '.impeccable/critique/session.json', '.impeccable/live/cache/preview.png',
                     '.agents/skills/ui-ux-pro-max/scripts/__pycache__/core.pyc',
                     'src/App/bin/example.dll', 'src/App/obj/example.json',
                     'test-results/example.trx', '.vs/example', 'tests/Example/TestResults/example.trx']:
            self.assertEqual(run('check-ignore', '--no-index', name).returncode, 0, name)
        for name in ['.agents/skills/ui-ux-pro-max/data/styles.csv',
                     '.agents/skills/impeccable/scripts/data/font-index.json']:
            path = self.root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text('upstream dataset', encoding='utf-8')
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
        with patch.object(verify.subprocess, 'run') as process, patch('builtins.print'):
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

    def test_client_preflight_checks_all_solution_and_project_references(self):
        def solution(paths):
            (self.root / verify.SOLUTION).write_text('<Solution>' + ''.join(
                f'<Project Path="{name}" />' for name in paths) + '</Solution>', encoding='utf-8')
        for name in verify.CLIENT_PROJECTS:
            path = self.root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text('<Project />', encoding='utf-8')
        solution(verify.CLIENT_PROJECTS)
        self.assertEqual(verify.client_structure_errors(self.root), [])
        for name in verify.CLIENT_PROJECTS:
            with self.subTest(missing=name):
                path = self.root / name
                path.unlink()
                with patch.object(verify.subprocess, 'run') as process, patch('builtins.print'):
                    self.assertEqual(verify.client_preflight(self.root), 2)
                    process.assert_not_called()
                path.write_text('<Project />', encoding='utf-8')
        solution(verify.CLIENT_PROJECTS[:-2] + verify.CLIENT_PROJECTS[-1:])
        self.assertTrue(verify.client_structure_errors(self.root))
        solution([*verify.CLIENT_PROJECTS, 'src/Missing/Missing.csproj'])
        self.assertTrue(verify.client_structure_errors(self.root))
        solution(verify.CLIENT_PROJECTS)
        project = self.root / verify.CLIENT_PROJECTS[0]
        project.write_text('<Project><ItemGroup><ProjectReference Include="../Missing/Missing.csproj" /></ItemGroup></Project>')
        self.assertTrue(verify.client_structure_errors(self.root))
        (self.root / verify.SOLUTION).write_text('<broken')
        with patch('builtins.print'):
            self.assertEqual(verify.client_preflight(self.root), 2)

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

    def test_skill_datasets_do_not_allow_runtime_or_nested_dependencies(self):
        for name in ['.agents/skills/ui-ux-pro-max/data/styles.csv',
                     '.agents/skills/impeccable/scripts/data/font-index.json']:
            path = self.root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text('upstream dataset', encoding='utf-8')
        self.assertEqual(verify.validate(self.root), [])
        for name in ['data/settings/window.json', '.agents/skills/unknown/data/local.json',
                     '.agents/skills/ui-ux-pro-max/data/node_modules/dependency.json',
                     '.agents/skills/impeccable/scripts/data/data/local.json']:
            with self.subTest(forbidden=name):
                path = self.root / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text('must reject', encoding='utf-8')
                self.assertIn(f'本机文件越界: {name}', verify.validate(self.root))
                path.unlink()


if __name__ == '__main__':
    unittest.main()
