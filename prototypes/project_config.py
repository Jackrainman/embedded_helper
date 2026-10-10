"""G4只读配置原型：冲突显式报告，不创建远程文件。"""
import json
from pathlib import Path
from ruamel.yaml import YAML


def read_local(root, target='Debug'):
    root = Path(root)
    eide = YAML().load((root / '.eide/eide.yml').read_text(encoding='utf-8'))
    selected = eide['targets'][target]
    jlink = selected.get('uploadConfigMap', {}).get('JLink', {})
    launch = json.loads((root / '.vscode/launch.json').read_text(encoding='utf-8'))
    candidates = [c for c in launch['configurations'] if c.get('servertype') == 'jlink']
    if len(candidates) != 1:
        raise ValueError('本地调试配置歧义，需选择')
    debug = candidates[0]
    chip = jlink.get('cpuInfo', {}).get('cpuName')
    if chip and debug.get('device') and chip != debug['device']:
        raise ValueError('连接芯片冲突：EIDE与launch')
    interface = debug.get('interface', '').upper()
    # EIDE 3.27.2 JLinkProtocolType: JTAG=0, SWD=1；其他协议未开放。
    eide_interface = {0: 'JTAG', 1: 'SWD'}.get(jlink.get('proType'))
    if eide_interface is None or (interface and interface != eide_interface):
        raise ValueError('接口不支持或EIDE与launch冲突')
    if interface not in {'SWD', 'JTAG'}:
        raise ValueError('接口缺失或不支持')
    if not isinstance(jlink.get('speed'), int) or jlink['speed'] <= 0:
        raise ValueError('连接速度缺失或无效')
    return {'device': chip or debug.get('device'), 'interface': interface,
            'speedKHz': jlink.get('speed'), 'mcu': eide['deviceName'],
            'sources': {'device': '.eide/eide.yml + .vscode/launch.json',
                        'interface': '.vscode/launch.json', 'speedKHz': '.eide/eide.yml'},
            'elf': debug['executable'], 'writeEnabled': False}
