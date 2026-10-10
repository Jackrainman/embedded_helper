"""M0目录式打包与隔离PATH启动测量，结果仅写临时产物。"""
import json
import os
from pathlib import Path
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[1]


def main():
    subprocess.run([sys.executable, '-m', 'PyInstaller', '--noconfirm', '--clean', '--onedir',
                    '--name', 'embedded-helper-probe', '--distpath', str(ROOT / 'dist'),
                    '--workpath', str(ROOT / 'build'), '--specpath', str(ROOT / 'build'),
                    str(ROOT / 'prototypes/gui_probe.py')], cwd=ROOT, check=True)
    package = ROOT / 'dist/embedded-helper-probe'
    env = dict(os.environ)
    env['PATH'] = os.environ.get('SystemRoot', 'C:/Windows') + '/System32'
    env.pop('PYTHONPATH', None)
    env.pop('PYTHONHOME', None)
    samples = []
    for i in range(2):
        report = ROOT / f'tmp/gui-frozen-{i}.json'
        started = time.perf_counter()
        subprocess.run([str(package / 'embedded-helper-probe.exe'), '--seconds', '3', '--report', str(report)],
                       cwd=ROOT / 'tmp', env=env, timeout=60, check=True)
        samples.append({'wallSeconds': time.perf_counter() - started,
                        **json.loads(report.read_text(encoding='utf-8'))})
    data = {'commit': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT).decode().strip(),
            'dirty': bool(subprocess.check_output(['git', 'status', '--porcelain'], cwd=ROOT)),
            'bytes': sum(p.stat().st_size for p in package.rglob('*') if p.is_file()),
            'samples': samples, 'unverified': ['clean Windows machine', 'hardware', 'installer']}
    (ROOT / 'tmp/build-probe.json').write_text(json.dumps(data, indent=2), encoding='utf-8')
    print(json.dumps(data, indent=2))
    return 0


if __name__ == '__main__':
    sys.exit(main())
