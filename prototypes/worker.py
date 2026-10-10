"""M0进程隔离探针；仅提供假后端验证超时清理，不触碰J-Link。"""
import multiprocessing
import time


def simulated_worker(pipe, block):
    pipe.send('connected')
    if block:
        time.sleep(3600)
    else:
        while pipe.recv() != 'stop':
            pass
        pipe.send('closed')
    pipe.close()


class Session:
    def __init__(self):
        self.process = None
        self.pipe = None
        self.state = 'disconnected'
        self.generation = 0

    def start(self, block=False):
        if self.process is not None:
            raise RuntimeError('必须先清理旧工作进程')
        context = multiprocessing.get_context('spawn')
        self.pipe, child = context.Pipe()
        self.process = context.Process(target=simulated_worker, args=(child, block))
        self.process.start()
        child.close()
        if not self.pipe.poll(5):
            self.stop()
            raise TimeoutError('连接超时')
        self.state = self.pipe.recv()
        self.generation += 1

    def stop(self, timeout=0.5):
        if self.process is None:
            return
        try:
            self.pipe.send('stop')
        except (BrokenPipeError, EOFError, OSError):
            pass
        self.process.join(timeout)
        if self.process.is_alive():
            self.process.terminate()
            self.process.join(5)
        if self.process.is_alive():
            self.state = 'fault'
            raise RuntimeError('旧进程未退出，禁止重试')
        self.process.close()
        self.pipe.close()
        self.process = self.pipe = None
        self.state = 'disconnected'
