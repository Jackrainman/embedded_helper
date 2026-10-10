"""验证离线版本缓存，不包含或复制 Debug_LAN 实现。"""
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import shutil
import stat
import tempfile
import zipfile


def safe_name(name):
    parts = PurePosixPath(name).parts
    if not parts or name.startswith('/') or '\\' in name or ':' in name:
        raise ValueError('非法包路径')
    if any(p in {'.', '..'} or p.endswith(('.', ' ')) or
           re.fullmatch(r'(?i)(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\..*)?', p) for p in parts):
        raise ValueError('非法包路径')
    return parts


def install(archive, digest, cache):
    archive, cache = Path(archive), Path(cache).resolve()
    if archive.stat().st_size > 64 * 1024 * 1024:
        raise ValueError('包过大')
    if hashlib.sha256(archive.read_bytes()).hexdigest() != digest:
        raise ValueError('包摘要不符')
    with zipfile.ZipFile(archive) as z:
        infos = z.infolist()
        if len(infos) > 1024 or sum(i.file_size for i in infos) > 128 * 1024 * 1024:
            raise ValueError('解压预算超限')
        seen = set()
        for info in infos:
            safe_name(info.filename)
            key = info.filename.casefold().rstrip('/')
            if key in seen or stat.S_ISLNK(info.external_attr >> 16):
                raise ValueError('重复或链接条目')
            seen.add(key)
        manifest = json.loads(z.read('manifest.json'))
        for key, expected in [('manifestSchema', 1), ('toolId', 'debug-lan'),
                              ('platform', 'windows'), ('architecture', 'x64'), ('cliApiMajor', 1)]:
            if manifest.get(key) != expected:
                raise ValueError('不兼容清单: ' + key)
        version = manifest['version']
        if not re.fullmatch(r'\d+\.\d+\.\d+', version):
            raise ValueError('版本无效')
        if not re.fullmatch('[0-9a-f]{40}', manifest['upstreamCommit']):
            raise ValueError('来源SHA无效')
        if manifest.get('configSchemaRange') != {'min': 1, 'max': 1}:
            raise ValueError('配置版本不兼容')
        safe_name(manifest['entrypoint'])
        if manifest['entrypoint'] not in z.namelist() or z.getinfo(manifest['entrypoint']).is_dir():
            raise ValueError('入口缺失')
        cache.mkdir(parents=True, exist_ok=True)
        target = cache / version
        if target.exists():
            raise ValueError('版本不可覆盖')
        staging = Path(tempfile.mkdtemp(prefix='.install-', dir=cache))
        try:
            z.extractall(staging)
            (staging / '.ready.json').write_text(json.dumps({'sha256': digest}), encoding='utf-8')
            staging.rename(target)
        finally:
            if staging.exists():
                shutil.rmtree(staging)
    return target


def resolve(project, portable):
    lock = json.loads((Path(project) / '.embedded_helper/tools.lock.json').read_text(encoding='utf-8'))
    tool = lock['tools']['debug-lan']
    version = tool['version']
    if lock.get('schemaVersion') != 1 or tool.get('cliApiMajor') != 1 or not re.fullmatch(r'\d+\.\d+\.\d+', version):
        raise ValueError('锁文件不兼容')
    cache = (Path(portable) / 'data/tools').resolve()
    directory = cache / version
    if not directory.is_dir() or directory.resolve().parent != cache:
        raise ValueError('锁定版本未安装')
    ready = json.loads((directory / '.ready.json').read_text(encoding='utf-8'))
    if ready['sha256'] != tool['sha256']:
        raise ValueError('锁定摘要不匹配')
    manifest = json.loads((directory / 'manifest.json').read_text(encoding='utf-8'))
    safe_name(manifest['entrypoint'])
    entry = (directory / manifest['entrypoint']).resolve()
    if not entry.is_relative_to(directory.resolve()) or not entry.is_file():
        raise ValueError('入口越界或缺失')
    return entry
