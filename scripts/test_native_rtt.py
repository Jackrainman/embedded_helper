"""Opt-in real Worker/native-DLL regression. Requires Windows x64 and GCC; no hardware."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import threading
import time

ROOT = Path(__file__).resolve().parents[1]


def trial(worker, dll, output, mode, rtt=True):
    label = mode if rtt else 'variables-only'
    log = output / (label + '.native.log')
    log.write_text('')
    env = dict(os.environ, EH_NATIVE_MODE=mode, EH_NATIVE_LOG=str(log))
    process = subprocess.Popen([str(worker), '--observe'], stdin=subprocess.PIPE,
                               stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                               text=True, encoding='utf-8', env=env,
                               creationflags=subprocess.CREATE_NO_WINDOW)
    events = []
    def read_events():
        for line in process.stdout:
            events.append(json.loads(line))
    reader = threading.Thread(target=read_events)
    reader.start()
    request = dict(Dll=str(dll), ProbeSerial=1, Host='', Port=0, Device='fixture',
                   Interface='SWD', SpeedKHz=1000, SampleRate=100, Rtt=rtt,
                   Variables=[] if rtt else [dict(Identity='value', Name='value', Address=0x20000000,
                                                  Type='uint32', Size=4, Status='可读取')],
                   SessionId='1' * 32, Generation=1)
    killed = False
    try:
        process.stdin.write(json.dumps(request) + '\n')
        process.stdin.flush()
        deadline = time.monotonic() + 3
        while process.poll() is None and time.monotonic() < deadline:
            time.sleep(.02)
        alive = process.poll() is None
        process.stdin.close()
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            killed = True
            process.kill()
            process.wait(timeout=5)
        reader.join(timeout=3)
        error = process.stderr.read()
        calls = log.read_text().splitlines()
        fault = rtt and mode != 'idle'
        passed = (not killed and process.returncode == (1 if fault else 0)
                  and alive == (not fault) and 'close' in calls
                  and (not rtt or 'rtt-stop' in calls)
                  and any(e['Kind'] == 'sample' for e in events)
                  and (not fault or ('USB' in error and ('断开' in error or '枚举失败' in error))))
        return dict(label=label, passed=passed, exitCode=process.returncode, aliveBeforeStop=alive,
                    killed=killed, stderr=error, calls=calls, samples=sum(e['Kind'] == 'sample' for e in events))
    finally:
        if process.poll() is None:
            process.kill()
            process.wait(timeout=5)
        reader.join(timeout=3)
        process.stdout.close()
        process.stderr.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--worker', type=Path, required=True)
    parser.add_argument('--compiler', default='gcc')
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    worker = args.worker.resolve(strict=True)
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    dll = output / 'native-rtt-fixture.dll'
    subprocess.run([args.compiler, '-shared', '-O2', '-o', str(dll),
                    str(ROOT / 'tests/native_rtt_fixture.c')], check=True)
    cases = [trial(worker, dll, output, mode) for mode in
             ['idle', 'disconnected', 'replaced', 'enumeration-error', 'idle']]
    cases.append(trial(worker, dll, output, 'disconnected', rtt=False))
    report = dict(scope='real Worker + controlled native DLL; no hardware',
                  worker=str(worker), workerSha256=hashlib.sha256(worker.read_bytes()).hexdigest(), cases=cases)
    (output / 'result.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
    for case in cases:
        print(('PASS' if case['passed'] else 'FAIL') + ' ' + case['label'])
    return 0 if all(c['passed'] for c in cases) else 1


if __name__ == '__main__':
    raise SystemExit(main())
