"""检查已生成包与同包验收证据；不构建、不运行exe、不访问设备、不发布。"""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import zipfile

from verify import SEMVER

AUTOMATED = {'repository', 'client-static', 'client-tests', 'package-smoke'}
HARDWARE = {'g4-remote-flash', 'g4-local-variables-rtt', 'g4-release-debug-reconnect',
            'serial-1mbps-duplex'}
REQUIRED = AUTOMATED | HARDWARE | {'clean-windows'}
ENTRYPOINTS = {'EmbeddedHelper.exe', 'EmbeddedHelper.Worker.exe', 'embedded-helper-cli.exe'}


def read_json(path):
    return json.loads(path.read_text(encoding='utf-8'))


def validate_release(candidate, evidence_path, version, commit):
    errors = []
    try:
        if not SEMVER.fullmatch(version) or '-' not in version:
            raise ValueError('内测发布要求带预发布后缀的SemVer')
        if not re.fullmatch(r'[0-9a-f]{40}', commit):
            raise ValueError('必须指定完整提交SHA')
        manifest = read_json(candidate / 'release-manifest.json')
        if (manifest.get('schemaVersion'), manifest.get('productId'), manifest.get('version'),
                manifest.get('commit'), manifest.get('platform'), manifest.get('architecture'),
                manifest.get('deployment')) != (1, 'embedded-helper', version, commit, 'windows',
                                               'x64', 'self-contained-directory'):
            raise ValueError('发布清单身份、平台或部署方式不匹配')
        name = f'EmbeddedHelper-{version}-win-x64.zip'
        if manifest.get('packageName') != name:
            raise ValueError('包名不匹配')
        package = candidate / name
        digest = hashlib.sha256(package.read_bytes()).hexdigest()
        if manifest.get('packageSha256') != digest or manifest.get('packageBytes') != package.stat().st_size:
            raise ValueError('包摘要或大小不匹配')
        checksum = (candidate / 'SHA256SUMS').read_text(encoding='utf-8').strip()
        if checksum != f'{digest}  {name}':
            raise ValueError('SHA256SUMS与包不一致')
        with zipfile.ZipFile(package) as archive:
            members = archive.namelist()
            normalized = set()
            for member in members:
                path = PurePosixPath(member)
                if (not member or '\\' in member or ':' in member or path.is_absolute()
                        or '..' in path.parts or member.casefold() in normalized):
                    raise ValueError('ZIP条目不安全或重复')
                normalized.add(member.casefold())
            if not ENTRYPOINTS.issubset(members):
                raise ValueError('ZIP缺少GUI、工作进程或稳定CLI入口')
            if archive.testzip():
                raise ValueError('ZIP内容损坏')
        evidence = read_json(evidence_path)
        if (evidence.get('schemaVersion'), evidence.get('version'), evidence.get('commit'),
                evidence.get('packageSha256')) != (1, version, commit, digest):
            raise ValueError('验收证据未绑定同一版本、提交和包')
        checks = evidence.get('checks')
        if not isinstance(checks, list):
            raise ValueError('验收checks必须为列表')
        by_id = {}
        for check in checks:
            if not isinstance(check, dict) or not isinstance(check.get('id'), str) or check['id'] in by_id:
                raise ValueError('验收项无效或重复')
            by_id[check['id']] = check
        for identifier in sorted(REQUIRED):
            check = by_id.get(identifier, {})
            if check.get('status') != 'passed' or type(check.get('exitCode')) is not int or check['exitCode'] != 0:
                errors.append(f'缺少真实通过结果: {identifier}')
                continue
            expected = 'hardware' if identifier in HARDWARE else 'clean-windows' if identifier == 'clean-windows' else 'automated'
            if check.get('method') != expected or not isinstance(check.get('details'), str) or not check['details'].strip():
                errors.append(f'验证方法或记录缺失: {identifier}')
            if identifier in HARDWARE and (not check.get('equipment') or not check.get('toolVersions')):
                errors.append(f'设备和工具版本缺失: {identifier}')
            if identifier in {'g4-local-variables-rtt', 'serial-1mbps-duplex'}:
                duration = check.get('durationSeconds')
                if type(duration) not in {int, float} or duration < 1800:
                    errors.append(f'持续时间不足30分钟: {identifier}')
        if '远程变量、网页RTT切换及其他模板尚未完成完整P0验收' not in (candidate / 'release-notes.md').read_text(encoding='utf-8'):
            errors.append('内测说明缺少剩余P0边界')
    except (OSError, ValueError, KeyError, TypeError, zipfile.BadZipFile, RuntimeError) as exc:
        errors.append(f'发布输入无效: {exc}')
    return errors


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--candidate', type=Path, required=True)
    parser.add_argument('--evidence', type=Path, required=True)
    parser.add_argument('--version', required=True)
    parser.add_argument('--commit', required=True)
    args = parser.parse_args()
    errors = validate_release(args.candidate, args.evidence, args.version, args.commit)
    for error in errors:
        print('FAIL', error)
    if not errors:
        print('PASS 发布输入门；仅核对证据格式和一致性，不替代实测，也未发布')
    return 1 if errors else 0


if __name__ == '__main__':
    raise SystemExit(main())
