"""双向有序分段记录原型；调用方只提交驱动实际接受/返回的字节。"""
import json
from pathlib import Path
import threading


class Recorder:
    def __init__(self, directory, segment_bytes=4096):
        self.directory = Path(directory)
        self.directory.mkdir(parents=True, exist_ok=False)
        self.limit = segment_bytes
        self.lock = threading.Lock()
        self.sequence = 0
        self.segment = 0
        self.used = 0
        self.file = None
        self.failed = False

    def append(self, direction, data):
        if direction not in {'TX', 'RX'} or not isinstance(data, bytes) or len(data) > 1024 * 1024:
            raise ValueError('非法方向或字节块')
        with self.lock:
            if self.failed:
                raise OSError('记录器已失败')
            payload = (json.dumps({'sequence': self.sequence, 'direction': direction,
                                   'length': len(data), 'hex': data.hex()}) + '\n').encode()
            try:
                if self.file is None or self.used + len(payload) > self.limit:
                    if self.file:
                        self.file.close()
                    self.file = (self.directory / f'{self.segment:04}.jsonl').open('xb')
                    self.segment += 1
                    self.used = 0
                self.file.write(payload)
                self.file.flush()
                self.used += len(payload)
                self.sequence += 1
            except OSError:
                self.failed = True
                raise

    def close(self):
        with self.lock:
            if self.file:
                self.file.close()
