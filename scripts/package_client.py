"""Build a portable candidate, test the extracted bytes, and bind honest evidence."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time
import zipfile

ROOT = Path(__file__).resolve().parents[1]


def run(command, **kwargs):
    subprocess.run(command, cwd=ROOT, check=True, **kwargs)


def write_json(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--output', type=Path, default=ROOT / 'dist/candidate')
    args = parser.parse_args()
    version = (ROOT / 'VERSION').read_text().strip()
    commit = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip()
    if subprocess.check_output(['git', 'status', '--porcelain'], cwd=ROOT):
        raise RuntimeError('Commit source and documentation before creating a candidate.')
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    package = output / f'EmbeddedHelper-{version}-win-x64.zip'
    if package.exists():
        raise RuntimeError('Candidate already exists; select a new output directory.')
    run([sys.executable, 'scripts/verify.py', '--client'])
    (ROOT / 'tmp').mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='package-', dir=ROOT / 'tmp') as temp:
        temporary = Path(temp)
        publish = temporary / 'publish'
        for project in ['App', 'Worker', 'Cli']:
            run(['dotnet', 'publish', f'src/EmbeddedHelper.{project}/EmbeddedHelper.{project}.csproj',
                 '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '--no-restore',
                 '-p:PublishSingleFile=false', '-p:PublishTrimmed=false', '-o', str(publish)])
        for required in ['EmbeddedHelper.exe', 'EmbeddedHelper.Worker.exe', 'embedded-helper-cli.exe',
                         'hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll', 'PresentationFramework.dll']:
            if not (publish / required).is_file():
                raise RuntimeError(f'Missing portable runtime file: {required}')
        shutil.copy2(ROOT / 'packaging/使用说明.txt', publish)
        shutil.copy2(ROOT / 'packaging/THIRD-PARTY-NOTICES.txt', publish)
        for generated in publish.glob('*.pdb'):
            generated.unlink()
        with zipfile.ZipFile(package, 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
            for file in sorted(publish.rglob('*')):
                if file.is_file():
                    archive.write(file, file.relative_to(publish).as_posix())
        digest = hashlib.sha256(package.read_bytes()).hexdigest()
        extracted = temporary / '中文 空格 portable'
        with zipfile.ZipFile(package) as archive:
            archive.extractall(extracted)
        qa = output / 'qa'
        qa.mkdir(exist_ok=True)
        shutil.copytree(ROOT / 'tests/fixtures/G4 中文 空格', qa / 'project', dirs_exist_ok=True)
        shutil.copy2(ROOT / 'tests/fixtures/scalars.elf', qa / 'scalars.elf')
        clean_env = os.environ.copy()
        clean_env['PATH'] = os.path.join(os.environ['SystemRoot'], 'System32')
        clean_env['DOTNET_ROOT'] = str(temporary / 'no-installed-runtime')
        clean_env['DOTNET_MULTILEVEL_LOOKUP'] = '0'
        for exe in ['EmbeddedHelper.Worker.exe', 'embedded-helper-cli.exe']:
            result = subprocess.run([str(extracted / exe), '--self-test'], check=True,
                                    capture_output=True, text=True, env=clean_env, timeout=15)
            if result.stdout.strip() != version:
                raise RuntimeError(f'Version mismatch: {exe}')
        startup = []
        for _ in range(2):
            started = time.perf_counter()
            subprocess.run([str(extracted / 'EmbeddedHelper.exe'), '--smoke', str(qa)],
                           check=True, env=clean_env, timeout=60, cwd=temporary)
            startup.append(round(time.perf_counter() - started, 3))
            smoke = json.loads((qa / 'smoke.json').read_text(encoding='utf-8'))
            if smoke.get('status') != 'passed' or smoke.get('exitCode') != 0 or smoke.get('version') != version:
                raise RuntimeError('WPF smoke did not pass.')
        # Hash the original ZIP again after testing the extracted copy.
        if hashlib.sha256(package.read_bytes()).hexdigest() != digest:
            raise RuntimeError('Package changed during testing.')
        manifest = dict(schemaVersion=1, productId='embedded-helper', version=version, commit=commit,
                        platform='windows', architecture='x64', deployment='self-contained-directory',
                        releaseProfile='developer-preview', packageName=package.name, packageSha256=digest,
                        packageBytes=package.stat().st_size, expandedBytes=sum(p.stat().st_size for p in publish.rglob('*') if p.is_file()),
                        sdk=json.loads((ROOT / 'global.json').read_text())['sdk']['version'],
                        runtime='10.0.12', startupAndSmokeSeconds=startup)
        write_json(output / 'release-manifest.json', manifest)
        (output / 'SHA256SUMS').write_text(f'{digest}  {package.name}\n', encoding='utf-8')
        (output / 'release-notes.md').write_text(
            f'# Embedded Helper {version}\n\n开发内测；硬件未验收。\n\n'
            '下载 ZIP，完整解压到可写目录后启动 EmbeddedHelper.exe；升级时保留 data。\n\n'
            '已实现：G4工程读取、ELF标量索引、变量/RTT采集入口、兼容远程JSON预览保存、独立串口收发/记录、设置与诊断。'
            'DWARF5需外部ARM readelf（自动识别EIDE安装），SEGGER库及设备驱动由用户安装。\n\n'
            '尚未完成：EIDE上传切换、新工具版本在线发现；远程变量、网页RTT切换及其他模板尚未完成完整P0验收。'
            '当前更新变量地址需先停止采集；绘图采用有界WPF折线。\n\n'
            '硬件链路、1Mbps双向30分钟、目标停止/恢复、无预装运行时的纯净Windows均未验收，等待用户测试。'
            '本次验证为真实EXE在构建机解压启动、五页GUI渲染、设置回读、CLI/工作进程自检及C#离线测试。'
            '收窄PATH和DOTNET_ROOT不等同纯净Windows测试。\n', encoding='utf-8')
        checks = [dict(id=identifier, status='passed', exitCode=0, method='automated', details=detail)
                  for identifier, detail in [
                      ('repository', 'scripts/verify.py --client: repository/Harness checks passed before publishing.'),
                      ('client-static', 'Locked restore, dotnet format verify-no-changes and Release build passed.'),
                      ('client-tests', 'C# tests passed; TRX produced by verify.py. Hardware was not accessed.'),
                      ('package-smoke', f'Original ZIP extracted to a Chinese/space path; WPF five pages and settings twice; CLI/worker self-tests. Elapsed including smoke: {startup}. See qa/smoke.json and page PNGs.')]]
        evidence = dict(schemaVersion=1, version=version, commit=commit, packageSha256=digest,
                        releaseProfile='developer-preview', checks=checks,
                        deferredChecks=['clean-windows', 'g4-remote-flash', 'g4-local-variables-rtt',
                                        'g4-release-debug-reconnect', 'serial-1mbps-duplex'])
        write_json(output / 'verification-evidence.json', evidence)
        run([sys.executable, 'scripts/release_gate.py', '--candidate', str(output), '--evidence',
             str(output / 'verification-evidence.json'), '--version', version, '--commit', commit])
        print(json.dumps(manifest, indent=2))
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
