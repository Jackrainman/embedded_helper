"""Opt-in AT-45/46 trials: instrument private source copies at named boundaries, never user data."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import time

ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--tool-zip', type=Path, required=True)
    parser.add_argument('--evidence', type=Path, required=True)
    args = parser.parse_args()
    evidence = args.evidence.resolve()
    evidence.mkdir(parents=True, exist_ok=True)
    workspace = Path(tempfile.mkdtemp(prefix='offline-fault-', dir=ROOT / 'tmp')).resolve()
    assert workspace.parent == (ROOT / 'tmp').resolve()
    results = {'scope': 'instrumented private build; original product source is unchanged; no network/SDK/device', 'trials': [], 'instrumentation': []}

    def write(path, text):
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding='utf-8')

    def instrument(relative, old, new):
        path = workspace / relative
        source = path.read_text(encoding='utf-8')
        assert source.count(old) == 1, relative
        results['instrumentation'].append({'file': relative, 'sourceSha256': hashlib.sha256(source.encode()).hexdigest(), 'replaced': old, 'with': new})
        write(path, source.replace(old, new))

    def run(command, name, env=None):
        with (evidence / (name + '.log')).open('wb') as log:
            return subprocess.run(command, cwd=workspace, env=env, stdout=log, stderr=subprocess.STDOUT, timeout=120).returncode

    def start(command, name, env):
        log = (evidence / (name + '.log')).open('wb')
        startup = subprocess.STARTUPINFO()
        startup.dwFlags |= subprocess.STARTF_USESHOWWINDOW
        startup.wShowWindow = 0
        process = subprocess.Popen(command, cwd=workspace, env=env, stdout=log, stderr=subprocess.STDOUT, startupinfo=startup)
        return process, log

    def wait_marker(process, marker):
        deadline = time.monotonic() + 60
        while not marker.exists() and process.poll() is None and time.monotonic() < deadline:
            time.sleep(.02)
        assert marker.exists() and process.poll() is None, 'instrumented boundary not reached'

    try:
        for name in ['Directory.Build.props', 'global.json', 'VERSION']:
            shutil.copy2(ROOT / name, workspace / name)
        for name in ['EmbeddedHelper.Core', 'EmbeddedHelper.App']:
            shutil.copytree(ROOT / 'src' / name, workspace / 'src' / name, ignore=shutil.ignore_patterns('bin', 'obj', 'data'))
        shutil.copytree(ROOT / 'packaging', workspace / 'packaging')
        instrument('src/EmbeddedHelper.Core/Tools/ToolCache.cs', 'Directory.CreateDirectory(staging);',
                   'Directory.CreateDirectory(staging); if (Environment.GetEnvironmentVariable("AUDIT_STAGE") == "install") { File.WriteAllText(Environment.GetEnvironmentVariable("AUDIT_MARKER")!, staging); Thread.Sleep(Timeout.Infinite); }')
        instrument('src/EmbeddedHelper.App/Diagnostics/MainWindow.LocalToolsSmoke.cs', 'Directory.CreateDirectory(settingsPath);',
                   'Directory.CreateDirectory(settingsPath); if (Environment.GetEnvironmentVariable("AUDIT_STAGE") == "settings-kill") { File.WriteAllText(Environment.GetEnvironmentVariable("AUDIT_MARKER")!, backup); Thread.Sleep(Timeout.Infinite); }')
        instrument('src/EmbeddedHelper.App/Diagnostics/MainWindow.LocalToolsSmoke.cs', 'SaveToolPath(LocalToolKind.ReadElf, "");\n                if (settings.ReadElf',
                   'if (Environment.GetEnvironmentVariable("AUDIT_STAGE") == "settings-error") throw new IOException("controlled settings failure");\n                SaveToolPath(LocalToolKind.ReadElf, "");\n                if (settings.ReadElf')
        service = (ROOT / 'src/EmbeddedHelper.App/Services/ToolPreparationService.cs').read_text(encoding='utf-8')
        results['instrumentation'].append({'file': 'ToolPreparationService.cs', 'sourceSha256': hashlib.sha256(service.encode()).hexdigest(), 'replacement': 'new HttpClient -> new HttpClient(new OfflineHandler()); all URLs intercepted locally'})
        write(workspace / 'host/ToolPreparationService.cs', service.replace('new HttpClient {', 'new HttpClient(new OfflineHandler()) {'))
        write(workspace / 'host/host.csproj', '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include="../src/EmbeddedHelper.Core/EmbeddedHelper.Core.csproj" /></ItemGroup></Project>')
        write(workspace / 'host/Program.cs', '''using EmbeddedHelper.Core;
using EmbeddedHelper.App;
using System.Net;
Console.WriteLine(await ToolPreparationService.PrepareAsync(args[0], ToolRelease.Known[0], args[1] == "download" ? null : args[1]));
sealed class OfflineHandler : HttpMessageHandler {
 protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new HeldStream()) });
}
sealed class HeldStream : MemoryStream {
 private bool first = true;
 public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) {
  if (first) { first = false; buffer.Span.Clear(); return buffer.Length; }
  if (Environment.GetEnvironmentVariable("AUDIT_STAGE") == "download-error") throw new IOException("controlled download failure");
  File.WriteAllText(Environment.GetEnvironmentVariable("AUDIT_MARKER")!, "download-body-written");
  await Task.Delay(Timeout.Infinite, token); return 0;
 }
}''')
        for project in ['src/EmbeddedHelper.App/EmbeddedHelper.App.csproj', 'host/host.csproj']:
            assert run(['dotnet', 'build', project, '-c', 'Release', '--ignore-failed-sources'], 'build-' + Path(project).stem) == 0
        host = workspace / 'host/bin/Release/net10.0/host.exe'
        for phase in ['download-error', 'download-kill', 'install']:
            data = workspace / phase
            marker = workspace / (phase + '.marker')
            env = dict(os.environ, AUDIT_STAGE=phase, AUDIT_MARKER=str(marker))
            command = [str(host), str(data), 'download' if phase.startswith('download') else str(args.tool_zip.resolve())]
            if phase == 'download-error':
                code = run(command, phase, env)
                assert code != 0 and not list((data / 'downloads').glob('*.zip'))
                results['trials'].append({'phase': phase, 'exitCode': code, 'leftoverFiles': 0})
                continue
            process, log = start(command, phase, env)
            try:
                wait_marker(process, marker)
                if phase == 'install':
                    assert run([str(host), str(data), str(args.tool_zip.resolve())], 'install-concurrent') != 0
                process.kill(); code = process.wait(timeout=10)
            finally:
                if process.poll() is None:
                    process.kill(); process.wait(timeout=10)
                log.close()
            residue = [{'path': str(p.relative_to(data)), 'bytes': p.stat().st_size} for p in data.rglob('*') if p.is_file()]
            stages = [str(p.relative_to(data)) for p in data.rglob('.install-*')]
            assert residue if phase.startswith('download') else stages
            assert run([str(host), str(data), str(args.tool_zip.resolve())], phase + '-retry') == 0
            results['trials'].append({'phase': phase, 'pid': process.pid, 'exitCode': code, 'exited': process.poll() is not None, 'residue': residue, 'staging': stages, 'retryExitCode': 0, 'recovery': 'retry succeeds; private crash residue is not auto-pruned'})
        for phase in ['settings-error', 'settings-kill']:
            trial = workspace / phase
            app = trial / 'app'
            shutil.copytree(workspace / 'src/EmbeddedHelper.App/bin/Release/net10.0-windows', app, ignore=shutil.ignore_patterns('data'))
            qa = trial / 'qa'
            shutil.copytree(ROOT / 'tests/fixtures/G4 中文 空格', qa / 'project')
            shutil.copy2(ROOT / 'tests/fixtures/scalars.elf', qa / 'scalars.elf')
            marker = trial / 'marker'
            env = dict(os.environ, AUDIT_STAGE=phase, AUDIT_MARKER=str(marker))
            env.pop('EMBEDDED_HELPER_SMOKE_PERF', None)
            command = [str(app / 'EmbeddedHelper.exe'), '--smoke', str(qa)]
            if phase == 'settings-error':
                code = run(command, phase, env)
                assert code == 1
                backups = list(app.rglob('*.r33-smoke-backup'))
                assert not backups
                settings, = app.rglob('window.json')
                assert settings.is_file() and isinstance(json.loads(settings.read_text(encoding='utf-8-sig')), dict)
                results['trials'].append({'phase': phase, 'exitCode': code, 'finallyRestored': True, 'backupLeft': False})
            else:
                process, log = start(command, phase, env)
                try:
                    wait_marker(process, marker)
                    process.kill(); code = process.wait(timeout=10)
                finally:
                    if process.poll() is None:
                        process.kill(); process.wait(timeout=10)
                    log.close()
                backup = Path(marker.read_text())
                assert backup.is_relative_to(trial)
                settings = backup.with_name('window.json')
                assert settings.is_dir() and backup.is_file()
                digest = hashlib.sha256(backup.read_bytes()).hexdigest()
                shutil.copy2(backup, evidence / 'settings-crash-backup.json')
                settings.rmdir(); backup.rename(settings)
                assert hashlib.sha256(settings.read_bytes()).hexdigest() == digest
                json.loads(settings.read_text(encoding='utf-8-sig'))
                results['trials'].append({'phase': phase, 'pid': process.pid, 'exitCode': code, 'exited': True, 'backupSha256': digest, 'recovery': 'after confirming process exit, remove empty window.json directory and rename window.json.r33-smoke-backup to window.json; bytes preserved'})
        results['status'] = 'passed'
    finally:
        results['workspace'] = str(workspace)
        write(evidence / 'results.json', json.dumps(results, ensure_ascii=False, indent=2))
        if results.get('status') == 'passed':
            assert workspace.parent == (ROOT / 'tmp').resolve()
            shutil.rmtree(workspace)
            results['workspaceRemoved'] = True
            write(evidence / 'results.json', json.dumps(results, ensure_ascii=False, indent=2))
    print(json.dumps(results, ensure_ascii=False))


if __name__ == '__main__':
    main()
