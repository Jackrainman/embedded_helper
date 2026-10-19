import ctypes
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('gui_smoke', ROOT / 'scripts/gui_smoke.py')
gui = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gui)


class GuiSmokeTests(unittest.TestCase):
    def setUp(self):
        (ROOT / 'tmp').mkdir(exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(dir=ROOT / 'tmp')
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.app = self.root / 'built client'
        self.app.mkdir()
        (self.app / 'EmbeddedHelper.exe').write_bytes(b'fake application; wrapper test only')
        (self.app / 'data').mkdir()
        (self.app / 'data/private.json').write_text('must not copy')
        fixtures = self.root / 'tests/fixtures'
        project = fixtures / 'G4 中文 空格/.eide'
        project.mkdir(parents=True)
        (project / 'eide.yml').write_text('immutable fixture')
        (fixtures / 'scalars.elf').write_bytes(b'fixture')
        self.root_patch = patch.object(gui, 'ROOT', self.root)
        self.root_patch.start()
        self.addCleanup(self.root_patch.stop)

    def fake_gui(self, command, workspace, timeout):
        self.assertFalse((workspace / 'app/data').exists())
        qa = Path(command[-1])
        (qa / 'project/.eide/eide.yml').write_text('edited isolated fixture')
        gui.write_json(qa / 'smoke.json', {
            'status': 'passed', 'exitCode': 0, 'pages': 5, 'projectSections': 3,
            'observationSettings': 'roundtrip-and-missing-config-passed',
            'serialSmoke': {'status': 'passed', 'source': 'synthetic', 'hardware': 'not-tested'}})
        for name in gui.SCREENSHOTS:
            (qa / name).write_bytes(gui.PNG_SIGNATURE + b'wrapper fixture, not a real screenshot')
        return 0

    def runs(self):
        return list((self.root / 'test-results/gui-smoke/Release').glob('run-*'))

    def test_success_cleans_workspace_preserves_evidence_and_replaces_previous_success(self):
        with patch.object(gui, 'run_process', side_effect=self.fake_gui), patch('builtins.print'):
            self.assertEqual(gui.run_smoke(self.app), 0)
            self.assertEqual(gui.run_smoke(self.app), 0)
        self.assertEqual(list((self.root / 'tmp').iterdir()), [])
        self.assertEqual(len(self.runs()), 1)
        result = json.loads((self.runs()[0] / 'run.json').read_text(encoding='utf-8'))
        self.assertFalse(result['workspaceRetained'])
        self.assertEqual(len(list(self.runs()[0].glob('*.png'))), len(gui.SCREENSHOTS))
        self.assertEqual((self.root / 'tests/fixtures/G4 中文 空格/.eide/eide.yml').read_text(encoding='utf-8'), 'immutable fixture')
        self.assertEqual((self.app / 'data/private.json').read_text(encoding='utf-8'), 'must not copy')

    def test_failed_process_and_incomplete_report_preserve_workspace(self):
        def incomplete(command, workspace, timeout):
            self.fake_gui(command, workspace, timeout)
            (Path(command[-1]) / gui.SCREENSHOTS[0]).unlink()
            return 0
        for result in [lambda *args: 9, incomplete]:
            with patch.object(gui, 'run_process', side_effect=result), patch('builtins.print'):
                self.assertEqual(gui.run_smoke(self.app), 1)
        self.assertEqual(len(self.runs()), 2)
        for run in self.runs():
            metadata = json.loads((run / 'run.json').read_text(encoding='utf-8'))
            self.assertEqual(metadata['status'], 'failed')
            self.assertTrue(Path(metadata['workspace']).is_dir())

    def test_failed_self_check_is_not_accepted_even_when_process_exits_zero(self):
        def failed(command, workspace, timeout):
            self.fake_gui(command, workspace, timeout)
            gui.write_json(Path(command[-1]) / 'smoke.json', {'status': 'failed', 'exitCode': 1})
            return 0
        with patch.object(gui, 'run_process', side_effect=failed), patch('builtins.print'):
            self.assertEqual(gui.run_smoke(self.app), 1)

    def test_missing_input_and_invalid_timeout_never_launch_application(self):
        with patch.object(gui, 'run_process') as launch:
            with self.assertRaises(FileNotFoundError):
                gui.run_smoke(self.root / 'missing')
            for timeout in [0, -1, float('nan'), float('inf')]:
                with self.assertRaises(ValueError):
                    gui.run_smoke(self.app, timeout=timeout)
            launch.assert_not_called()

    def test_missing_or_failed_serial_interaction_evidence_is_rejected(self):
        def missing_serial(command, workspace, timeout):
            self.fake_gui(command, workspace, timeout)
            path = Path(command[-1]) / 'smoke.json'
            report = json.loads(path.read_text(encoding='utf-8'))
            report.pop('serialSmoke')
            gui.write_json(path, report)
            return 0
        with patch.object(gui, 'run_process', side_effect=missing_serial), patch('builtins.print'):
            self.assertEqual(gui.run_smoke(self.app), 1)

    def test_explicit_keep_and_cleanup_boundary(self):
        with patch.object(gui, 'run_process', side_effect=self.fake_gui), patch('builtins.print'):
            self.assertEqual(gui.run_smoke(self.app, configuration='Debug', keep_workspace=True), 0)
        self.assertEqual(len(list((self.root / 'tmp').iterdir())), 1)
        with self.assertRaises(ValueError):
            gui.remove_run(self.app, self.root / 'tmp')
        self.assertTrue((self.app / 'data/private.json').is_file())

    @unittest.skipUnless(os.name == 'nt', 'Windows process-tree regression')
    def test_timeout_terminates_real_parent_and_child_without_gui_or_devices(self):
        workspace = self.root / 'process-tree'
        workspace.mkdir()
        child_pid = workspace / 'child.pid'
        code = ('import pathlib,subprocess,sys,time; '
                'p=subprocess.Popen([sys.executable,"-c","import time; time.sleep(120)"]); '
                'pathlib.Path("child.pid").write_text(str(p.pid)); time.sleep(120)')
        with self.assertRaises(subprocess.TimeoutExpired):
            gui.run_process([sys.executable, '-c', code], workspace, 3)
        self.assertTrue(child_pid.is_file())
        kernel = ctypes.windll.kernel32
        kernel.OpenProcess.restype = ctypes.c_void_p
        kernel.WaitForSingleObject.argtypes = [ctypes.c_void_p, ctypes.c_uint32]
        kernel.CloseHandle.argtypes = [ctypes.c_void_p]
        handle = kernel.OpenProcess(0x00100000, False, int(child_pid.read_text(encoding='utf-8')))
        if handle:
            try:
                self.assertEqual(kernel.WaitForSingleObject(handle, 0), 0)
            finally:
                kernel.CloseHandle(handle)


if __name__ == '__main__':
    unittest.main()
