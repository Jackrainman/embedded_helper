"""自动升级提交的产品版本；打包/CI 只读校验，失败即阻断。"""
import argparse
import os
from pathlib import Path
import re
import subprocess
import sys

if hasattr(sys.stdout, 'reconfigure'):
    sys.stdout.reconfigure(encoding='utf-8')

VERSION = re.compile(r'(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([a-zA-Z][a-zA-Z0-9-]*)\.(0|[1-9]\d*))?')
ROOT = Path(__file__).resolve().parents[1]
BASELINE = '121e075f1cd083159b197f04f838f5e84cd21046'


def git(root, *args):
    return subprocess.check_output(['git', *args], cwd=root).decode('utf-8').strip()


def product_changed(paths):
    return any(p.startswith(('src/', 'packaging/')) or p in {
        'Directory.Build.props', 'global.json', 'EmbeddedHelper.slnx',
        'scripts/package_client.py'} for p in paths)


def level_for(message):
    title = message.splitlines()[0] if message else ''
    if re.match(r'^\w+(?:\([^)]*\))?!:', title) or re.search(r'^BREAKING[ -]CHANGE:', message, re.M):
        return 'major'
    return 'minor' if re.match(r'^feat(?:\([^)]*\))?:', title) else 'patch'


def parse(value):
    match = VERSION.fullmatch(value)
    if not match:
        raise ValueError(f'不支持的版本格式：{value}')
    return tuple(map(int, match.groups()[:3])), match[4], match[5]


def next_version(value, level):
    numbers, channel, _ = parse(value)
    numbers = list(numbers)
    index = {'major': 0, 'minor': 1, 'patch': 2}[level]
    numbers[index] += 1
    for i in range(index + 1, 3):
        numbers[i] = 0
    return '.'.join(map(str, numbers)) + (f'-{channel}.1' if channel else '')


def validate_change(before, after, message):
    required = next_version(before, level_for(message))
    if parse(after)[0] < parse(required)[0]:
        raise ValueError(f'产品变化必须升版：{before} -> 至少 {required}，实际 {after}')


def prepare(root, message):
    if subprocess.run(['git', 'rev-parse', '--verify', 'HEAD'], cwd=root,
                      capture_output=True).returncode:
        return
    before = git(root, 'show', 'HEAD:VERSION')
    paths = git(root, 'diff', '--cached', '--name-only', '--no-renames').splitlines()
    if 'VERSION' in paths and parse(git(root, 'show', ':VERSION'))[0] < parse(before)[0]:
        raise ValueError('VERSION 不允许倒退')
    if not product_changed(paths):
        return
    staged = git(root, 'show', ':VERSION')
    file = root / 'VERSION'
    if file.read_text(encoding='utf-8').strip() != staged:
        raise ValueError('VERSION 存在未暂存修改；请先明确并暂存版本，避免覆盖。')
    if staged == before:
        staged = next_version(before, level_for(message))
        file.write_text(staged + '\n', encoding='utf-8')
        git(root, 'add', '--', 'VERSION')
        print(f'自动升版：{before} -> {staged}')
    validate_change(before, staged, message)


def check(root, revision):
    parents = git(root, 'rev-list', '--parents', '-n', '1', revision).split()
    if len(parents) < 2:
        return
    message = git(root, 'show', '-s', '--format=%B', revision)
    current = parse(git(root, 'show', f'{revision}:VERSION'))[0]
    if any(current < parse(git(root, 'show', f'{p}:VERSION'))[0] for p in parents[1:]):
        raise ValueError('VERSION 不允许倒退')
    # 合并提交由其各个源提交负责升版；检查合并结果不得低于任一父提交。
    if len(parents) > 2:
        return
    paths = git(root, 'diff', '--name-only', '--no-renames', parents[1], revision).splitlines()
    if product_changed(paths):
        validate_change(git(root, 'show', f'{parents[1]}:VERSION'),
                        git(root, 'show', f'{revision}:VERSION'), message)


def validate_index(root, message):
    if subprocess.run(['git', 'rev-parse', '--verify', 'HEAD'], cwd=root,
                      capture_output=True).returncode:
        return
    paths = git(root, 'diff', '--cached', '--name-only', '--no-renames').splitlines()
    if product_changed(paths):
        validate_change(git(root, 'show', 'HEAD:VERSION'), git(root, 'show', ':VERSION'), message)


def main():
    parser = argparse.ArgumentParser()
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument('--commit-msg', type=Path)
    group.add_argument('--prepare', action='store_true')
    group.add_argument('--check', nargs='?', const='HEAD')
    group.add_argument('--range')
    args = parser.parse_args()
    try:
        if args.commit_msg:
            validate_index(ROOT, args.commit_msg.read_text(encoding='utf-8'))
        elif args.prepare:
            level = os.environ.get('VERSION_BUMP_LEVEL', 'patch')
            messages = {'patch': 'fix: automatic', 'minor': 'feat: automatic', 'major': 'feat!: automatic'}
            if level not in messages:
                raise ValueError('VERSION_BUMP_LEVEL 必须为 patch/minor/major')
            prepare(ROOT, messages[level])
        elif args.range:
            for revision in git(ROOT, 'rev-list', '--reverse', args.range).splitlines():
                check(ROOT, revision)
        else:
            for revision in git(ROOT, 'rev-list', '--reverse', f'{BASELINE}..{args.check}').splitlines():
                check(ROOT, revision)
    except (OSError, ValueError, subprocess.CalledProcessError) as exc:
        print(f'FAIL 版本门：{exc}')
        return 1
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
