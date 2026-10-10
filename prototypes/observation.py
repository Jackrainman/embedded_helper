"""固定地址标量索引与手动更新生命周期原型；不连接设备。"""
from pathlib import Path
import struct

from elftools.elf.elffile import ELFFile
from elftools.dwarf.dwarf_expr import DWARFExprParser


def index_elf(path):
    result = {}
    with Path(path).open('rb') as stream:
        elf = ELFFile(stream)
        if elf.elfclass != 32 or not elf.little_endian or elf['e_machine'] != 'EM_ARM':
            raise ValueError('仅支持ARM ELF32小端')
        if not elf.has_dwarf_info():
            raise ValueError('缺少DWARF类型')
        dwarf = elf.get_dwarf_info()
        for cu in dwarf.iter_CUs():
            parser = DWARFExprParser(cu.structs)
            for die in cu.iter_DIEs():
                if die.tag != 'DW_TAG_variable' or 'DW_AT_location' not in die.attributes:
                    continue
                name = die.attributes.get('DW_AT_name')
                if not name:
                    continue
                location = die.attributes['DW_AT_location']
                if location.form != 'DW_FORM_exprloc':
                    continue
                ops = parser.parse_expr(location.value)
                if len(ops) != 1 or ops[0].op_name != 'DW_OP_addr':
                    continue
                base = die.get_DIE_from_attribute('DW_AT_type')
                seen = set()
                while base.tag in {'DW_TAG_typedef', 'DW_TAG_const_type', 'DW_TAG_volatile_type'}:
                    if base.offset in seen:
                        raise ValueError('循环类型')
                    seen.add(base.offset)
                    base = base.get_DIE_from_attribute('DW_AT_type')
                if base.tag != 'DW_TAG_base_type':
                    continue
                size = base.attributes['DW_AT_byte_size'].value
                encoding = base.attributes['DW_AT_encoding'].value
                fmt = {(5, 1): 'b', (6, 1): 'b', (7, 1): 'B', (8, 1): 'B',
                       (5, 2): 'h', (7, 2): 'H', (5, 4): 'i', (7, 4): 'I', (4, 4): 'f'}.get((encoding, size))
                identity = f'{cu.cu_offset}:{die.offset}:{name.value.decode()}'
                result[identity] = {'name': name.value.decode(), 'address': ops[0].args[0],
                                    'size': size, 'format': fmt, 'supported': fmt is not None}
    return result


def decode(item, data):
    if not item['supported'] or len(data) != item['size']:
        raise ValueError('不支持类型或读取长度不符')
    return struct.unpack('<' + item['format'], data)[0]


class ReadPlan:
    def __init__(self, path):
        self.path = Path(path)
        self.generation = 0
        self.update()

    def changed(self):
        return self.path.stat().st_mtime_ns != self.mtime

    def update(self):
        stat_before = self.path.stat()
        items = index_elf(self.path)
        stat_after = self.path.stat()
        if (stat_before.st_mtime_ns, stat_before.st_size) != (stat_after.st_mtime_ns, stat_after.st_size):
            raise ValueError('ELF正在变化，保留旧计划')
        self.items, self.mtime = items, stat_after.st_mtime_ns
        self.generation += 1

    def reconnect(self):
        # 停止/再开始或端点切换只换会话代次，旧地址计划继续保留。
        self.generation += 1
        return self.items
