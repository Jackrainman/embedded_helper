"""只更新唯一产品VERSION，不创建tag或发布。"""
import argparse
from pathlib import Path

from verify import SEMVER
from version_guard import next_version


def bump(path, version):
    if not SEMVER.fullmatch(version):
        raise ValueError('请输入合法SemVer，不接受构建元数据')
    previous = path.read_text(encoding='utf-8').strip()
    if not SEMVER.fullmatch(previous):
        raise ValueError('原VERSION无效')
    if version == previous:
        raise ValueError('新版本必须与原版本不同')
    path.write_text(version + '\n', encoding='utf-8', newline='\n')
    return previous


def main():
    parser = argparse.ArgumentParser()
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument('--to')
    group.add_argument('--level', choices=['patch', 'minor', 'major'])
    args = parser.parse_args()
    try:
        path = Path(__file__).resolve().parents[1] / 'VERSION'
        args.to = args.to or next_version(path.read_text(encoding='utf-8').strip(), args.level)
        previous = bump(path, args.to)
    except (OSError, ValueError) as exc:
        print(f'FAIL 版本更新: {exc}')
        return 1
    print(f'产品版本: {previous} -> {args.to}；未创建tag或Release')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
