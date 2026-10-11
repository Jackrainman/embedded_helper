"""唯一离线验证编排；提交时检查索引快照而非未暂存工作树。"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET

if hasattr(sys.stdout, 'reconfigure'):
    sys.stdout.reconfigure(encoding='utf-8')

ROOT = Path(__file__).resolve().parents[1]
DOCS = {'README.md', 'AGENTS.md', '01_需求规格说明书.md',
        '02_概要设计说明书.md', '03_详细设计说明书.md'}
GENERATED = {'.git', 'tmp', '.venv', '__pycache__', 'build', 'dist', '.ruff_cache',
             'bin', 'obj', 'TestResults', 'test-results', '.vs'}
FORBIDDEN = {'data', 'node_modules', '.codex', '.claude'} | (GENERATED - {'.git'})
SEMVER = re.compile(r'(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)'
                    r'(?:-((?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)'
                    r'(?:\.(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*))*))?')
SOLUTION = 'EmbeddedHelper.slnx'
CLIENT_PROJECTS = ('src/EmbeddedHelper.App/EmbeddedHelper.App.csproj',
                   'src/EmbeddedHelper.Core/EmbeddedHelper.Core.csproj',
                   'src/EmbeddedHelper.Worker/EmbeddedHelper.Worker.csproj',
                   'src/EmbeddedHelper.Cli/EmbeddedHelper.Cli.csproj',
                   'src/EmbeddedHelper.Portable/EmbeddedHelper.Portable.csproj',
                   'tests/EmbeddedHelper.Tests/EmbeddedHelper.Tests.csproj')
TEST_PROJECT = 'tests/EmbeddedHelper.Tests/EmbeddedHelper.Tests.csproj'


def validate_dotnet(root):
    errors = []
    try:
        version = (root / 'VERSION').read_text(encoding='utf-8').strip()
        if not SEMVER.fullmatch(version):
            raise ValueError('VERSION不是受支持的SemVer')
        sdk = json.loads((root / 'global.json').read_text(encoding='utf-8'))['sdk']
        if not re.fullmatch(r'10\.\d+\.\d+', sdk['version']):
            raise ValueError('SDK必须锁定.NET 10的具体版本')
        if sdk.get('rollForward') != 'disable' or sdk.get('allowPrerelease') is not False:
            raise ValueError('SDK禁止静默漂移或预览版')
        props = ET.parse(root / 'Directory.Build.props').getroot()
        versions = props.findall('.//Version')
        if len(versions) != 1 or versions[0].text != "$([System.IO.File]::ReadAllText('$(MSBuildThisFileDirectory)VERSION').Trim())":
            raise ValueError('构建版本必须读取唯一VERSION')
        for name in ['Nullable', 'Deterministic', 'TreatWarningsAsErrors', 'RestorePackagesWithLockFile']:
            expected = 'enable' if name == 'Nullable' else 'true'
            if props.findtext(f'.//{name}') != expected:
                raise ValueError(f'缺少构建约束: {name}')
    except (OSError, ValueError, KeyError, TypeError, ET.ParseError) as exc:
        errors.append(f'C#基线无效: {exc}')
    for project in list((root / 'src').rglob('*.csproj')) + list((root / 'tests').rglob('*.csproj')):
        if any(part in GENERATED for part in project.relative_to(root).parts):
            continue
        try:
            tree = ET.parse(project).getroot()
            if any(tree.findall(f'.//{name}') for name in ['Version', 'VersionPrefix', 'VersionSuffix']):
                raise ValueError('项目不允许另设产品版本')
            lock = json.loads((project.parent / 'packages.lock.json').read_text(encoding='utf-8'))
            if lock.get('version') not in {1, 2} or not isinstance(lock.get('dependencies'), dict):
                raise ValueError('NuGet锁文件结构不正确')
        except (OSError, ValueError, ET.ParseError) as exc:
            errors.append(f'C#项目约束无效: {project.relative_to(root)}: {exc}')
    return errors


def validate_workflows(root):
    errors = []
    for name in ['verify.yml', 'release.yml']:
        path = root / '.github/workflows' / name
        try:
            content = path.read_text(encoding='utf-8')
            for action in re.findall(r'^\s*- uses:\s*(\S+)', content, re.MULTILINE):
                if not re.fullmatch(r'actions/[a-z-]+@[0-9a-f]{40}', action):
                    errors.append(f'Action未锁定官方完整SHA: {name}: {action}')
            if 'pull_request_target' in content or 'continue-on-error: true' in content:
                errors.append(f'工作流不允许高权限PR或忽略失败: {name}')
            if name == 'verify.yml' and ('contents: write' in content or 'gh release create' in content):
                errors.append('普通验证不得发布或具有仓库写权限')
            if name == 'release.yml':
                if not re.search(r'^on:\s*\n  workflow_dispatch:', content, re.MULTILINE) or 'needs: gate' not in content:
                    errors.append('发布必须显式触发且依赖输入门')
        except OSError as exc:
            errors.append(f'缺少工作流: {exc}')
    return errors


def git(root, *args):
    return subprocess.run(['git', '-C', str(root), *args], capture_output=True, check=True).stdout


def validate(root):
    errors = []
    files = []
    for directory, dirs, names in os.walk(root):
        dirs[:] = [d for d in dirs if d not in GENERATED]
        files.extend(Path(directory) / name for name in names)
    for p in files:
        rel = p.relative_to(root).as_posix()
        if p.suffix == '.md' and rel not in DOCS and not rel.startswith('.agents/skills/'):
            errors.append(f'文档不在单源清单: {rel}')
        if any(x in FORBIDDEN for x in p.relative_to(root).parts):
            errors.append(f'本机文件越界: {rel}')
    for name in DOCS:
        p = root / name
        if not p.exists():
            errors.append(f'缺少文档: {name}')
            continue
        content = p.read_text(encoding='utf-8-sig')
        headings = re.findall(r'^## (\d+)\.', content, re.MULTILINE)
        if len(headings) != len(set(headings)):
            errors.append(f'重复章节编号: {name}')
        for target in re.findall(r'\]\(([^)]+)\)', content):
            target = target.strip('<>').split('#')[0]
            if target and not re.match(r'[a-zA-Z]+:', target) and not (p.parent / target).exists():
                errors.append(f'断链: {name}: {target}')
    try:
        ledger = json.loads((root / '.harness/todo.json').read_text(encoding='utf-8'))
        assert ledger['schemaVersion'] == 1
        tasks = ledger['tasks']
        ids = [t['id'] for t in tasks]
        assert len(ids) == len(set(ids))
        requirements = (root / '01_需求规格说明书.md').read_text(encoding='utf-8-sig')
        graph = {}
        for t in tasks:
            assert re.fullmatch(r'[A-Z0-9]+-\d+', t['id'])
            assert t['status'] in {'todo', 'in_progress', 'blocked', 'done'}
            assert isinstance(t['doneWhen'], str) and len(t['doneWhen']) > 10
            assert isinstance(t['refs'], list) and t['refs']
            assert all(re.fullmatch(r'[A-Z]+-\d+', ref) and ref in requirements for ref in t['refs'])
            assert isinstance(t['dependsOn'], list)
            assert all(d in ids and d != t['id'] for d in t['dependsOn'])
            graph[t['id']] = t['dependsOn']
        def visit(n, stack):
            assert n not in stack, '任务依赖环'
            for d in graph[n]:
                visit(d, stack | {n})
        for n in graph:
            visit(n, set())
    except (OSError, ValueError, KeyError, TypeError, AssertionError) as e:
        errors.append(f'任务账本无效: {e}')
    try:
        manifest = json.loads((root / 'tests/fixtures/manifest.json').read_text(encoding='utf-8'))
        assert re.fullmatch('[0-9a-f]{40}', manifest['upstreamCommit'])
        assert manifest['sanitization'] and manifest['expectations']
        for name, digest in manifest['sha256'].items():
            p = (root / 'tests/fixtures' / name).resolve()
            assert p.is_relative_to((root / 'tests/fixtures').resolve())
            assert hashlib.sha256(p.read_bytes()).hexdigest() == digest, name
    except (OSError, ValueError, KeyError, AssertionError) as e:
        errors.append(f'样本身份无效: {e}')
    return errors + validate_dotnet(root) + validate_workflows(root)


def environment():
    print(f'ENV Python={sys.version.split()[0]} Git={git(ROOT, "--version").decode().strip()}')
    print('未验证: J-Link/目标板/远程服务/COM实物链路；离线检查不访问设备')
    for command in ['dotnet', 'arm-none-eabi-gcc']:
        print(f'ENV {command}={shutil.which(command) or "缺失，相关试验未验证"}')
    if shutil.which('dotnet'):
        sdk_list = subprocess.run(['dotnet', '--list-sdks'], capture_output=True, text=True, check=False)
        print(f'ENV SDK列表={sdk_list.stdout.strip() or "空，C#客户端未验证"}')


def client_structure_errors(root):
    errors = [f'缺少工程文件: {name}' for name in [SOLUTION, *CLIENT_PROJECTS] if not (root / name).is_file()]
    if not (root / SOLUTION).is_file():
        return errors
    try:
        solution = ET.parse(root / SOLUTION).getroot()
        paths = [node.attrib['Path'] for node in solution.findall('.//Project')]
        resolved = [(root / name).resolve() for name in paths]
        if len(set(resolved)) != len(resolved):
            errors.append('解决方案包含重复工程')
        for required in CLIENT_PROJECTS:
            if (root / required).resolve() not in resolved:
                errors.append(f'解决方案未引用必要工程: {required}')
        for path in resolved:
            if not path.is_relative_to(root.resolve()) or not path.is_file():
                errors.append(f'解决方案引用缺失或越界: {path}')
                continue
            for node in ET.parse(path).getroot().findall('.//ProjectReference'):
                target = (path.parent / node.attrib['Include']).resolve()
                if not target.is_relative_to(root.resolve()) or not target.is_file():
                    errors.append(f'工程引用缺失或越界: {path.name}: {target}')
    except (OSError, KeyError, ET.ParseError) as exc:
        errors.append(f'解决方案或工程格式无效: {exc}')
    return errors


def client_preflight(root):
    errors = client_structure_errors(root)
    if errors:
        print('未验证：C#客户端尚未形成完整工程:', '; '.join(errors))
        return 2
    if not shutil.which('dotnet'):
        print('未验证：缺少.NET SDK')
        return 2
    sdk = json.loads((root / 'global.json').read_text(encoding='utf-8'))['sdk']['version']
    result = subprocess.run(['dotnet', '--version'], cwd=root, capture_output=True, text=True, check=False)
    if result.returncode or result.stdout.strip() != sdk:
        print(f'未验证：要求SDK {sdk}，无法使用该SDK')
        return 2
    return 0


def verify_client(root, configuration='Release'):
    result = client_preflight(root)
    if result:
        return result
    commands = [
        ['dotnet', 'restore', SOLUTION, '--locked-mode'],
        ['dotnet', 'format', SOLUTION, '--verify-no-changes', '--no-restore'],
        ['dotnet', 'build', SOLUTION, '--configuration', configuration, '--no-restore'],
        ['dotnet', 'test', TEST_PROJECT, '--configuration', configuration, '--no-build',
         '--no-restore', '--logger', 'trx', '--results-directory', str(root / 'test-results')],
    ]
    for command in commands:
        result = subprocess.run(command, cwd=root, check=False)
        if result.returncode:
            return result.returncode
    return 0


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--install-hooks', action='store_true')
    parser.add_argument('--staged', action='store_true')
    parser.add_argument('--client', action='store_true', help='显式执行C#静态检查、构建和测试')
    parser.add_argument('--client-preflight', action='store_true', help='只检查客户端工程与SDK，不构建')
    parser.add_argument('--configuration', choices=['Debug', 'Release'], default='Release', help='--client构建配置，默认Release')
    args = parser.parse_args()
    if sum([args.install_hooks, args.staged, args.client, args.client_preflight]) > 1:
        parser.error('执行模式不能组合')
    if args.configuration != 'Release' and not args.client:
        parser.error('--configuration仅用于--client')
    if args.install_hooks:
        git(ROOT, 'config', '--local', 'core.hooksPath', '.githooks')
        assert git(ROOT, 'config', '--get', 'core.hooksPath').strip() == b'.githooks'
        print('已安装 core.hooksPath=.githooks；运行完整验证检查真实提交触发')
        return 0
    environment()
    if args.staged:
        git(ROOT, 'diff', '--cached', '--check')
        for name in git(ROOT, 'ls-files', '-z').decode('utf-8').split('\0'):
            if name and any(part in FORBIDDEN for part in Path(name).parts):
                print('FAIL 暂存本机产物:', name)
                return 1
        (ROOT / 'tmp').mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(dir=ROOT / 'tmp') as temp:
            snapshot = Path(temp)
            git(ROOT, 'checkout-index', '--all', '--prefix=' + snapshot.as_posix() + '/')
            # 从暂存副本执行，避免未暂存脚本让坏提交漏检。
            return subprocess.call([sys.executable, str(snapshot / 'scripts/verify.py')], cwd=snapshot)
    errors = validate(ROOT)
    for error in errors:
        print('FAIL', error)
    if errors:
        return 1
    for directory in ['scripts', 'tests']:
        for source in (ROOT / directory).rglob('*.py'):
            compile(source.read_text(encoding='utf-8'), str(source), 'exec')
    suite = unittest.defaultTestLoader.discover(str(ROOT / 'tests'), pattern='test_*.py')
    result = unittest.TextTestRunner(verbosity=2).run(suite)
    print('PASS 离线验证' if result.wasSuccessful() else 'FAIL 测试')
    if not result.wasSuccessful():
        return 1
    print('仓库门不覆盖成品启动和实机验收；仅显式--client继续检查客户端', flush=True)
    if args.client_preflight:
        return client_preflight(ROOT)
    if args.client:
        return verify_client(ROOT, args.configuration)
    return 0


if __name__ == '__main__':
    sys.exit(main())
