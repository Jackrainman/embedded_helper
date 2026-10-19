"""Run the built WPF client's offline self-check in a disposable workspace."""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import math
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import uuid

ROOT = Path(__file__).resolve().parents[1]
PNG_SIGNATURE = b'\x89PNG\r\n\x1a\n'
SCREENSHOTS = [f'{page}{size}.png' for size in ['', '-minimum']
               for page in [*[f'page-{i}' for i in range(5)],
                            *[f'project-section-{i}' for i in range(3)]]]
SCREENSHOTS += [f'page-1-{name}.png' for name in
                ['missing', 'missing-remote', 'missing-sdk', 'rtt-only', 'rtt-only-minimum', 'plot']]
SCREENSHOTS += ['page-2-log.png', 'page-2-frozen.png', 'page-2-frozen-minimum.png']


def write_json(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')


def remove_run(path, parent):
    if path.is_symlink() or path.resolve().parent != parent.resolve():
        raise ValueError(f'拒绝清理运行目录以外的路径: {path}')
    shutil.rmtree(path)


def run_process(command, workspace, timeout):
    options = {}
    if os.name == 'nt':
        startup = subprocess.STARTUPINFO()
        startup.dwFlags |= subprocess.STARTF_USESHOWWINDOW
        startup.wShowWindow = 0
        options = {'startupinfo': startup, 'creationflags': subprocess.CREATE_NEW_PROCESS_GROUP}
    with (workspace / 'stdout.log').open('wb') as stdout, (workspace / 'stderr.log').open('wb') as stderr:
        process = subprocess.Popen(command, cwd=workspace, stdout=stdout, stderr=stderr, **options)
        try:
            return process.wait(timeout=timeout)
        except (subprocess.TimeoutExpired, KeyboardInterrupt):
            try:
                if os.name == 'nt':
                    stopped = subprocess.run(['taskkill', '/PID', str(process.pid), '/T', '/F'],
                                             capture_output=True, timeout=10, check=False)
                    (workspace / 'termination.log').write_bytes(stopped.stdout + stopped.stderr)
            finally:
                if process.poll() is None:
                    process.kill()
                process.wait(timeout=10)
            raise


def validate_result(qa):
    result = json.loads((qa / 'smoke.json').read_text(encoding='utf-8-sig'))
    if (not isinstance(result, dict) or result.get('status') != 'passed'
            or result.get('exitCode') != 0 or result.get('pages') != 5
            or result.get('projectSections') != 3
            or result.get('observationSettings') != 'roundtrip-and-missing-config-passed'
            or not isinstance(result.get('serialSmoke'), dict)
            or result['serialSmoke'].get('status') != 'passed'
            or result['serialSmoke'].get('source') != 'synthetic'
            or result['serialSmoke'].get('hardware') != 'not-tested'):
        raise ValueError('GUI自检未通过全部页面及观察配置检查。')
    for name in SCREENSHOTS:
        with (qa / name).open('rb') as screenshot:
            if screenshot.read(8) != PNG_SIGNATURE:
                raise ValueError(f'截图无效: {name}')
    return result


def run_smoke(app_directory, configuration='Release', timeout=60, keep_workspace=False):
    if configuration not in {'Debug', 'Release'} or not math.isfinite(timeout) or timeout <= 0:
        raise ValueError('configuration须为Debug/Release，timeout须为正有限数。')
    source = Path(app_directory).resolve()
    executable = source / 'EmbeddedHelper.exe'
    if not executable.is_file():
        raise FileNotFoundError(f'缺少客户端构建产物: {executable}；请先显式构建或指定--app-dir。')
    fixtures = ROOT / 'tests/fixtures'
    project = fixtures / 'G4 中文 空格'
    if not (project / '.eide/eide.yml').is_file() or not (fixtures / 'scalars.elf').is_file():
        raise FileNotFoundError('缺少只读GUI样本。')
    temporary = ROOT / 'tmp'
    temporary.mkdir(exist_ok=True)
    workspace = Path(tempfile.mkdtemp(prefix='gui-smoke-', dir=temporary))
    evidence = ROOT / 'test-results/gui-smoke' / configuration / ('run-' + uuid.uuid4().hex)
    evidence.mkdir(parents=True)
    metadata = {'status': 'failed', 'configuration': configuration, 'applicationDirectory': str(source),
                'workspace': str(workspace), 'startedAt': datetime.now(timezone.utc).isoformat(),
                'method': 'automated', 'hardware': 'not-tested'}
    try:
        app = workspace / 'app'
        qa = workspace / 'qa'
        shutil.copytree(source, app, ignore=shutil.ignore_patterns('data'))
        shutil.copytree(project, qa / 'project')
        shutil.copy2(fixtures / 'scalars.elf', qa / 'scalars.elf')
        with (app / 'EmbeddedHelper.exe').open('rb') as stream:
            metadata['executableSha256'] = hashlib.file_digest(stream, 'sha256').hexdigest()
        code = run_process([str(app / 'EmbeddedHelper.exe'), '--smoke', str(qa)], workspace, timeout)
        metadata['processExitCode'] = code
        if code:
            raise RuntimeError(f'GUI进程退出码: {code}')
        result = validate_result(qa)
        metadata.update(status='passed', version=result.get('version'), screenshots=len(SCREENSHOTS))
    except (OSError, ValueError, RuntimeError, subprocess.SubprocessError, KeyboardInterrupt) as error:
        metadata['error'] = f'{type(error).__name__}: {error}'
    finally:
        for name in ['stdout.log', 'stderr.log', 'termination.log']:
            if (workspace / name).is_file():
                shutil.copy2(workspace / name, evidence / name)
        qa = workspace / 'qa'
        if qa.is_dir():
            for path in qa.iterdir():
                if path.is_file() and (path.suffix == '.png' or path.name == 'smoke.json'):
                    shutil.copy2(path, evidence / path.name)
        metadata['finishedAt'] = datetime.now(timezone.utc).isoformat()
        metadata['workspaceRetained'] = True
        if metadata['status'] == 'passed' and not keep_workspace:
            try:
                remove_run(workspace, temporary)
                metadata['workspaceRetained'] = False
            except OSError as error:
                metadata['status'] = 'failed'
                metadata['error'] = f'运行副本清理失败: {error}'
        write_json(evidence / 'run.json', metadata)
        if metadata['status'] == 'passed':
            latest = evidence.parent / 'latest.json'
            try:
                previous = json.loads(latest.read_text(encoding='utf-8')) if latest.is_file() else {}
            except (OSError, ValueError):
                previous = {}
            write_json(latest, {'run': evidence.name})
            old_name = previous.get('run', '') if isinstance(previous, dict) else ''
            old_name = old_name if isinstance(old_name, str) else ''
            old = evidence.parent / old_name
            if (old_name.startswith('run-') and len(old_name) == 36
                    and all(c in '0123456789abcdef' for c in old_name[4:])
                    and old.parent == evidence.parent and old != evidence and not old.is_symlink()
                    and (old / 'run.json').is_file()
                    and json.loads((old / 'run.json').read_text(encoding='utf-8')).get('status') == 'passed'):
                remove_run(old, evidence.parent)
    print(json.dumps(metadata, ensure_ascii=False))
    print('GUI验证证据:', evidence)
    return 0 if metadata['status'] == 'passed' else 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--configuration', choices=['Debug', 'Release'], default='Release')
    parser.add_argument('--app-dir', type=Path, help='显式指定已有WPF客户端构建目录；不构建')
    parser.add_argument('--timeout', type=float, default=60, help='GUI超时秒数，默认60')
    parser.add_argument('--keep-workspace', action='store_true', help='成功后也保留隔离运行副本')
    args = parser.parse_args()
    directory = args.app_dir or ROOT / f'src/EmbeddedHelper.App/bin/{args.configuration}/net10.0-windows'
    try:
        return run_smoke(directory, args.configuration, args.timeout, args.keep_workspace)
    except (OSError, ValueError) as error:
        print('FAIL', error, file=sys.stderr)
        return 1


if __name__ == '__main__':
    sys.exit(main())
