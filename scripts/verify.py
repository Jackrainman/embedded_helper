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

if hasattr(sys.stdout, 'reconfigure'):
    sys.stdout.reconfigure(encoding='utf-8')

ROOT = Path(__file__).resolve().parents[1]
DOCS = {'README.md', 'AGENTS.md', '01_需求规格说明书.md',
        '02_概要设计说明书.md', '03_详细设计说明书.md'}


def git(root, *args):
    return subprocess.run(['git', '-C', str(root), *args], capture_output=True, check=True).stdout


def validate(root):
    errors = []
    files = [p for p in root.rglob('*') if p.is_file() and
             not any(x in {'.git', 'tmp', '.venv', '__pycache__'} for x in p.relative_to(root).parts)]
    for p in files:
        rel = p.relative_to(root).as_posix()
        if p.suffix == '.md' and rel not in DOCS and not rel.startswith('.agents/skills/'):
            errors.append(f'文档不在单源清单: {rel}')
        if any(x in {'data', 'node_modules', '.codex', '.claude'} for x in p.relative_to(root).parts):
            errors.append(f'本机文件越界: {rel}')
    for name in DOCS:
        p = root / name
        if not p.exists():
            errors.append(f'缺少文档: {name}')
            continue
        content = p.read_text(encoding='utf-8-sig')
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
    return errors


def environment():
    print(f'ENV Python={sys.version.split()[0]} Git={git(ROOT, "--version").decode().strip()}')
    print('未验证: J-Link/目标板/远程服务/COM实物链路；离线检查不访问设备')
    for command in ['dotnet', 'arm-none-eabi-gcc']:
        print(f'ENV {command}={shutil.which(command) or "缺失，相关试验未验证"}')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--install-hooks', action='store_true')
    parser.add_argument('--staged', action='store_true')
    args = parser.parse_args()
    if args.install_hooks:
        git(ROOT, 'config', '--local', 'core.hooksPath', '.githooks')
        assert git(ROOT, 'config', '--get', 'core.hooksPath').strip() == b'.githooks'
        print('已安装 core.hooksPath=.githooks；运行完整验证检查真实提交触发')
        return 0
    environment()
    if args.staged:
        git(ROOT, 'diff', '--cached', '--check')
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
    suite = unittest.defaultTestLoader.discover(str(ROOT / 'tests'), pattern='test_*.py')
    result = unittest.TextTestRunner(verbosity=2).run(suite)
    print('PASS 离线验证' if result.wasSuccessful() else 'FAIL 测试')
    return 0 if result.wasSuccessful() else 1


if __name__ == '__main__':
    sys.exit(main())
