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
    parser.add_argument('--tool-zip', type=Path, help='Optional original Debug_LAN ZIP for real GUI import/integration smoke')
    parser.add_argument('--upgrade-from', type=Path, help='Optional previous portable EXE for real replacement/old-runtime-removal checks')
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
        (publish / '.portable-runtime').write_text('embedded-helper-portable-v1', encoding='utf-8')
        bundle = temporary / 'bundle'
        bundle.mkdir()
        with zipfile.ZipFile(bundle / 'payload.zip', 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
            for file in sorted(publish.rglob('*')):
                if file.is_file():
                    archive.write(file, file.relative_to(publish).as_posix())
        write_json(bundle / 'payload.json', dict(Version=version,
                   Sha256=hashlib.sha256((bundle / 'payload.zip').read_bytes()).hexdigest(),
                   Files={f.relative_to(publish).as_posix(): hashlib.sha256(f.read_bytes()).hexdigest()
                          for f in publish.rglob('*') if f.is_file()}))
        launcher = temporary / 'launcher'
        run(['dotnet', 'publish', 'src/EmbeddedHelper.Portable/EmbeddedHelper.Portable.csproj',
             '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '--no-restore',
             '-p:PublishTrimmed=false', '-p:PortablePayload=' + str(bundle), '-o', str(launcher)])
        executable = output / 'EmbeddedHelper.exe'
        shutil.copy2(launcher / 'EmbeddedHelper.Portable.exe', executable)
        exe_digest = hashlib.sha256(executable.read_bytes()).hexdigest()
        delivery = temporary / 'delivery'
        delivery.mkdir()
        shutil.copy2(executable, delivery)
        shutil.copy2(ROOT / 'packaging/使用说明.txt', delivery)
        shutil.copy2(ROOT / 'packaging/THIRD-PARTY-NOTICES.txt', delivery)
        with zipfile.ZipFile(package, 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
            for file in sorted(delivery.rglob('*')):
                if file.is_file():
                    archive.write(file, file.relative_to(delivery).as_posix())
        digest = hashlib.sha256(package.read_bytes()).hexdigest()
        extracted = temporary / '中文 空格 portable'
        with zipfile.ZipFile(package) as archive:
            archive.extractall(extracted)
        qa = output / 'qa'
        qa.mkdir(exist_ok=True)
        shutil.copytree(ROOT / 'tests/fixtures/G4 中文 空格', qa / 'project', dirs_exist_ok=True)
        shutil.copy2(ROOT / 'tests/fixtures/scalars.elf', qa / 'scalars.elf')
        if args.tool_zip:
            shutil.copy2(args.tool_zip, qa / 'debug-lan.zip')
        clean_env = os.environ.copy()
        clean_env['PATH'] = os.path.join(os.environ['SystemRoot'], 'System32')
        clean_env['DOTNET_ROOT'] = str(temporary / 'no-installed-runtime')
        clean_env['DOTNET_MULTILEVEL_LOOKUP'] = '0'
        startup = []
        for _ in range(2):
            started = time.perf_counter()
            subprocess.run([str(extracted / 'EmbeddedHelper.exe'), '--smoke', str(qa)],
                           check=True, env=clean_env, timeout=60, cwd=temporary)
            startup.append(round(time.perf_counter() - started, 3))
            smoke = json.loads((qa / 'smoke.json').read_text(encoding='utf-8'))
            if smoke.get('status') != 'passed' or smoke.get('exitCode') != 0 or smoke.get('version') != version:
                raise RuntimeError('WPF smoke did not pass.')
        installed = next((extracted / 'data/runtime').glob(version + '-*'))
        external = subprocess.run([str(extracted / 'EmbeddedHelper.exe'), '--cli', '--version'],
                                  check=True, capture_output=True, text=True, encoding='utf-8', env=clean_env, timeout=30)
        if external.stdout.strip() != version:
            raise RuntimeError('Portable CLI stdout/version mismatch.')
        invalid = subprocess.run([str(extracted / 'EmbeddedHelper.exe'), '--cli', 'invalid-command'],
                                 capture_output=True, text=True, encoding='utf-8', env=clean_env, timeout=30)
        if invalid.returncode != 2 or 'embedded-helper-cli' not in invalid.stderr:
            raise RuntimeError('Portable CLI stderr/exit code mismatch.')
        for exe in ['EmbeddedHelper.Worker.exe', 'embedded-helper-cli.exe']:
            result = subprocess.run([str(installed / exe), '--self-test'], check=True,
                                    capture_output=True, text=True, encoding='utf-8', env=clean_env, timeout=15)
            if result.stdout.strip() != version:
                raise RuntimeError(f'Version mismatch: {exe}')
        if not (extracted / 'data/settings/window.json').is_file():
            raise RuntimeError('Settings did not stay beside the single EXE.')
        # Launch after moving the complete portable directory; no old absolute runtime dependency.
        moved = temporary / '移动后 单 EXE'
        for attempt in range(11):
            try:
                extracted.rename(moved)
                break
            except PermissionError:
                if attempt == 10:
                    raise
                time.sleep(0.5)
        subprocess.run([str(moved / 'EmbeddedHelper.exe'), '--smoke', str(qa)], check=True,
                       env=clean_env, timeout=60, cwd=temporary)
        external = subprocess.run([str(moved / 'EmbeddedHelper.exe'), '--cli', '--version'],
                                  check=True, capture_output=True, text=True, encoding='utf-8', env=clean_env, timeout=30)
        if external.stdout.strip() != version:
            raise RuntimeError('Moved portable CLI did not resolve current components.')
        installed = next((moved / 'data/runtime').glob(version + '-*'))
        damaged = installed / 'EmbeddedHelper.Worker.exe'
        original = damaged.read_bytes()
        damaged.write_bytes(b'corrupted component')
        refused = subprocess.run([str(moved / 'EmbeddedHelper.exe'), '--smoke', str(qa)],
                                 env=clean_env, timeout=60, capture_output=True)
        if refused.returncode != 1:
            raise RuntimeError('Modified component was not rejected.')
        damaged.write_bytes(original)
        upgrade_from = None
        if args.upgrade_from:
            upgrade_home = temporary / '升级 中文 空格'
            upgrade_home.mkdir()
            entry = upgrade_home / 'EmbeddedHelper.exe'
            shutil.copy2(args.upgrade_from, entry)
            old = subprocess.run([str(entry), '--cli', '--version'], check=True,
                                 capture_output=True, text=True, encoding='utf-8', env=clean_env, timeout=60)
            upgrade_from = old.stdout.strip()
            if not upgrade_from or upgrade_from == version:
                raise RuntimeError('Upgrade input must report a different product version.')
            old_runtimes = list((upgrade_home / 'data/runtime').glob(upgrade_from + '-*'))
            if len(old_runtimes) != 1:
                raise RuntimeError('Previous portable runtime was not prepared.')
            saved = upgrade_home / 'data/settings/upgrade-probe.json'
            saved.parent.mkdir(parents=True, exist_ok=True)
            saved.write_bytes(b'{"keep":"previous-settings"}')
            settings_digest = hashlib.sha256(saved.read_bytes()).hexdigest()
            shutil.copy2(executable, entry)
            upgrade_env = clean_env | {'EMBEDDED_HELPER_ENTRY': str(entry),
                                       'EMBEDDED_HELPER_HOME': str(old_runtimes[0])}
            for remove_old in [False, True]:
                if remove_old:
                    old_runtime = old_runtimes[0].resolve()
                    retired = (temporary / 'retired-runtime').resolve()
                    if not old_runtime.is_relative_to(temporary.resolve()) or not retired.is_relative_to(temporary.resolve()):
                        raise RuntimeError('Upgrade trial paths escaped the isolated workspace.')
                    old_runtime.rename(retired)
                current = subprocess.run([upgrade_env['EMBEDDED_HELPER_ENTRY'], '--cli', '--version'],
                                         check=True, capture_output=True, text=True, encoding='utf-8', env=upgrade_env, timeout=60)
                if current.stdout.strip() != version or hashlib.sha256(saved.read_bytes()).hexdigest() != settings_digest:
                    raise RuntimeError('Replacement did not select current CLI or preserve settings.')
        # Hash the original ZIP again after testing the extracted copy.
        if hashlib.sha256(package.read_bytes()).hexdigest() != digest:
            raise RuntimeError('Package changed during testing.')
        manifest = dict(schemaVersion=1, productId='embedded-helper', version=version, commit=commit,
                        platform='windows', architecture='x64', deployment='self-contained-single-exe',
                        executableName='EmbeddedHelper.exe', executableSha256=exe_digest, executableBytes=executable.stat().st_size,
                        releaseProfile='developer-preview', packageName=package.name, packageSha256=digest,
                        packageBytes=package.stat().st_size, expandedBytes=sum(p.stat().st_size for p in delivery.rglob('*') if p.is_file()),
                        runtimeExpandedBytes=sum(p.stat().st_size for p in publish.rglob('*') if p.is_file()),
                        sdk=json.loads((ROOT / 'global.json').read_text())['sdk']['version'],
                        runtime='10.0.12', startupAndSmokeSeconds=startup, upgradeFrom=upgrade_from)
        write_json(output / 'release-manifest.json', manifest)
        (output / 'SHA256SUMS').write_text(f'{digest}  {package.name}\n{exe_digest}  EmbeddedHelper.exe\n', encoding='utf-8')
        (output / 'release-notes.md').write_text(
            f'# Embedded Helper {version}\n\n开发内测；硬件未验收。\n\n'
            '直接下载 EmbeddedHelper.exe，放在可写目录双击运行；首次启动自动准备内部组件，升级时保留旁边的 data。ZIP是同一EXE及说明的可选归档。\n\n'
            '已实现：G4/H7/F4/F1工程配置适配、EIDE上传切换与恢复、官方工具更新发现、版本锁定及回退、工程读取、ELF标量索引、变量/RTT采集入口、兼容远程JSON预览保存、独立串口收发/记录、设置与诊断。'
            'DWARF5需外部ARM readelf（自动识别EIDE安装），SEGGER库及设备驱动由用户安装。\n\n'
            '远程变量、网页RTT切换及其他模板尚未完成完整P0验收。'
            '支持采集时手动更新读计划；绘图采用有界WPF折线。\n\n'
            '硬件链路、1Mbps双向30分钟、目标停止/恢复、无预装运行时的纯净Windows均未验收，等待用户测试。'
            '本次验证为真实EXE在构建机解压启动、五页GUI渲染、设置回读、CLI/工作进程自检及C#离线测试。'
            '收窄PATH和DOTNET_ROOT不等同纯净Windows测试。\n', encoding='utf-8')
        checks = [dict(id=identifier, status='passed', exitCode=0, method='automated', details=detail)
                  for identifier, detail in [
                      ('repository', 'scripts/verify.py --client: repository/Harness checks passed before publishing.'),
                      ('client-static', 'Locked restore, dotnet format verify-no-changes and Release build passed.'),
                      ('client-tests', 'C# tests passed; TRX produced by verify.py. Hardware was not accessed.'),
                      ('package-smoke', f'Single EXE from original ZIP in Chinese/space path; first/repeat/moved WPF launch, five pages and settings; external CLI stdout/stderr/exit codes and internal CLI/worker self-tests; corrupted component refused. Real replacement and old-runtime removal from: {upgrade_from or "not tested"}. Elapsed including smoke: {startup}. See qa/smoke.json and page PNGs.')]]
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
