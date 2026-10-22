import copy
import importlib.util
import json
from pathlib import Path
import shutil
import tempfile
import unittest
from html.parser import HTMLParser

ROOT = Path(__file__).resolve().parents[1]


class PageParser(HTMLParser):
    def __init__(self):
        super().__init__()
        self.tags = []
        self.links = []
        self.text = []

    def handle_starttag(self, tag, attrs):
        self.tags.append(tag)
        if tag == 'a':
            self.links.append(dict(attrs).get('href', ''))

    def handle_data(self, data):
        self.text.append(data)


class LedgerTests(unittest.TestCase):
    def setUp(self):
        (ROOT / 'tmp').mkdir(exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(dir=ROOT / 'tmp')
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        for name in ['.harness', '.docs']:
            shutil.copytree(ROOT / name, self.root / name)
        self.todo = {'schemaVersion': 2, 'tasks': []}
        self.acceptance = json.loads((self.root / '.harness/test.json').read_text(encoding='utf-8'))
        self.acceptance['checks'] = [self.acceptance['checks'][0]]
        self.check = self.acceptance['checks'][0]
        module = ROOT / 'scripts/harness_ledger.py'
        self.assertTrue(module.exists(), '验收校验和看板生成入口尚未实现')
        spec = importlib.util.spec_from_file_location('harness_ledger_test', module)
        self.ledger = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.ledger)

    def save(self):
        for filename, value in [('todo.json', self.todo), ('test.json', self.acceptance)]:
            (self.root / '.harness' / filename).write_text(json.dumps(value), encoding='utf-8')

    def errors(self):
        self.save()
        return self.ledger.validate_ledgers(self.root)

    def test_historical_pass_is_valid_without_local_ignored_evidence(self):
        self.assertEqual(self.errors(), [])

    def test_missing_reason_or_resume_condition_cannot_hide_blocked_work(self):
        for status in ['blocked', 'paused']:
            self.check.update(status=status, reason='', resumeWhen='')
            self.assertTrue(self.errors())
            self.check.update(reason='远端关闭', resumeWhen='用户恢复远端')
            self.assertEqual(self.errors(), [])

    def test_pass_requires_evidence_artifact_identity_and_actual_method(self):
        original = copy.deepcopy(self.check)
        for field in ['evidence', 'candidate', 'environment', 'recordedAt']:
            self.acceptance['checks'][0] = copy.deepcopy(original)
            self.acceptance['checks'][0]['lastResult'].pop(field)
            self.assertTrue(self.errors(), field)
        self.acceptance['checks'][0] = copy.deepcopy(original)
        self.acceptance['checks'][0]['lastResult']['method'] = 'automated'
        self.assertTrue(self.errors())

    def test_completed_check_cannot_have_missing_or_failed_result(self):
        original = copy.deepcopy(self.check['lastResult'])
        for result in [None, dict(original, outcome='failed')]:
            self.check['lastResult'] = result
            self.assertTrue(self.errors())

    def test_duplicate_ids_unknown_requirement_and_dangling_tests_are_rejected(self):
        self.acceptance['checks'].append(copy.deepcopy(self.check))
        self.assertTrue(self.errors())
        self.acceptance['checks'].pop()
        self.check['refs'] = ['AC-9999']
        self.assertTrue(self.errors())
        self.check['refs'] = ['AC-04']
        self.todo['tasks'] = [self.task('DEV-01', [], ['AT-999'])]
        self.assertTrue(self.errors())

    @staticmethod
    def task(identifier, dependencies, tests):
        return dict(id=identifier, title='修复具体问题', component='Harness', priority='P1',
                    status='todo', refs=['NFR-05'], dependsOn=dependencies, testIds=tests,
                    doneWhen='修复原因明确且关联的回归测试可运行。', reason='', resumeWhen='')

    def test_dependency_cycle_and_removed_dependency_are_rejected(self):
        self.todo['tasks'] = [self.task('DEV-01', ['DEV-02'], ['AT-01']),
                              self.task('DEV-02', ['DEV-01'], [])]
        self.assertTrue(self.errors())
        self.todo['tasks'].pop()
        self.assertTrue(self.errors())

    def test_malformed_containers_produce_diagnostics_not_tracebacks(self):
        self.acceptance['checks'] = [None]
        self.assertTrue(self.errors())
        self.acceptance['checks'] = []
        self.todo['tasks'] = {'invalid': 1}
        self.assertTrue(self.errors())

    def test_evidence_paths_cannot_escape_repository_or_run_scripts(self):
        for path in ['../secret', 'C:/secret', '/secret', 'javascript:alert(1)',
                     '//host/share', r'..\secret', 'test-results/payload.html']:
            self.check['lastResult']['evidence'] = [{'path': path, 'label': '证据'}]
            self.assertTrue(self.errors(), path)

    def test_report_distinguishes_previous_package_pass_and_current_readiness(self):
        self.acceptance['candidate']['packageSha256'] = 'a' * 64
        self.save()
        output = self.root / 'test-results/看板 空格/index.html'
        self.ledger.build_report(self.root, output)
        parser = PageParser()
        parser.feed(output.read_text(encoding='utf-8'))
        text = ' '.join(parser.text)
        self.assertIn('待复验', text)
        self.assertIn('上次结果：通过', text)
        self.assertIn('证据未随当前副本提供', text)

    def test_report_escapes_text_and_links_only_existing_safe_evidence(self):
        payload = '<img src=x onerror=alert(1)></script>中文 & "特殊字符"'
        self.check['title'] = payload
        self.save()
        output = self.root / 'test-results/index.html'
        self.ledger.build_report(self.root, output)
        parser = PageParser()
        parser.feed(output.read_text(encoding='utf-8'))
        self.assertNotIn('img', parser.tags)
        self.assertIn(payload, ''.join(parser.text))
        self.assertTrue(any(link.startswith('file:') for link in parser.links))
        self.assertFalse(any('desktop-delivery.json' in link for link in parser.links))

    def test_invalid_ledger_does_not_overwrite_existing_report(self):
        output = self.root / 'test-results/keep.html'
        output.parent.mkdir(parents=True)
        output.write_text('previous good report', encoding='utf-8')
        self.check['status'] = 'unknown'
        self.save()
        with self.assertRaises(ValueError):
            self.ledger.build_report(self.root, output)
        self.assertEqual(output.read_text(encoding='utf-8'), 'previous good report')

    def test_output_cannot_overwrite_a_ledger_or_source_file(self):
        self.save()
        for name in ['.harness/todo.json', 'scripts/harness_report.html']:
            output = self.root / name
            output.parent.mkdir(parents=True, exist_ok=True)
            if not output.exists():
                output.write_text('source template', encoding='utf-8')
            before = output.read_bytes()
            with self.assertRaises(ValueError):
                self.ledger.build_report(self.root, output)
            self.assertEqual(output.read_bytes(), before)


if __name__ == '__main__':
    unittest.main()
