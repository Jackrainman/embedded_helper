"""M0可打包绘图探针：仅合成数据，不连接目标。"""
import argparse
import json
from pathlib import Path
import sys
import time

import numpy as np
from PySide6 import QtCore, QtWidgets
import pyqtgraph as pg

START = time.perf_counter()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--seconds', type=float, default=5)
    parser.add_argument('--report', type=Path, required=True)
    args = parser.parse_args()
    app = QtWidgets.QApplication([])
    widget = pg.PlotWidget(title='M0 离线绘图原型 · 16条合成曲线 · 非硬件读数')
    widget.resize(900, 500)
    curves = [widget.plot(pen=pg.intColor(i, 16)) for i in range(16)]
    values = np.zeros((16, 1000))
    frames = []
    first = []
    def tick():
        now = time.perf_counter()
        if not first:
            first.append(now - START)
        values[:, :-1] = values[:, 1:]
        values[:, -1] = np.sin(now + np.arange(16))
        for i, curve in enumerate(curves):
            curve.setData(values[i])
        frames.append(now)
        if len(frames) >= 2 and now - frames[0] >= args.seconds:
            args.report.parent.mkdir(parents=True, exist_ok=True)
            args.report.write_text(json.dumps({'mode': 'synthetic', 'frames': len(frames),
                'elapsed': now - frames[0], 'fps': (len(frames) - 1) / (now - frames[0]),
                'firstTickSeconds': first[0], 'historySamplesPerCurve': 1000,
                'frozen': bool(getattr(sys, 'frozen', False))}, indent=2), encoding='utf-8')
            app.quit()
    timer = QtCore.QTimer()
    timer.timeout.connect(tick)
    timer.start(33)
    widget.show()
    return app.exec()


if __name__ == '__main__':
    sys.exit(main())
