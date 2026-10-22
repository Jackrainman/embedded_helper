"""开发/验收账本校验及离线只读看板；不执行测试或设备操作。"""
import argparse
from datetime import datetime
import hashlib
import html
import json
from pathlib import Path, PurePosixPath, PureWindowsPath
import re
import sys

ROOT = Path(__file__).resolve().parents[1]
METHODS = {'automated': '自动程序', 'computer_use': '界面操作', 'manual': '用户参与'}
KINDS = {'software': '本机软件', 'hardware': '硬件', 'sample': '真实样本', 'clean_windows': '干净 Windows'}
STATES = {'dev': '待开发', 'ready': '现在可测', 'blocked': '缺条件', 'paused': '用户暂缓',
          'passed': '已通过', 'failed': '测试失败'}
IDENTITY = re.compile(r'[A-Z][A-Z0-9]*-\d+')


def require(condition, message):
    if not condition:
        raise ValueError(message)


def nonempty(value):
    return isinstance(value, str) and bool(value.strip())


def strings(value):
    return isinstance(value, list) and bool(value) and all(nonempty(item) for item in value)


def candidate_valid(candidate):
    return (isinstance(candidate, dict) and nonempty(candidate.get('version'))
            and isinstance(candidate.get('commit'), str)
            and re.fullmatch(r'[0-9a-f]{40}', candidate['commit']) is not None
            and all(isinstance(candidate.get(key), str) and re.fullmatch(r'[0-9a-f]{64}', candidate[key])
                    for key in ['packageSha256', 'executableSha256']))


def evidence_path(value):
    if not nonempty(value) or '\\' in value or ':' in value:
        return False
    path = PurePosixPath(value)
    return (not path.is_absolute() and not PureWindowsPath(value).is_absolute()
            and all(part not in {'..', '.'} for part in value.split('/'))
            and path.parts[0] in {'.docs', 'test-results', 'tests'}
            and path.suffix.lower() in {'.json', '.txt', '.log', '.trx', '.png', '.jpg', '.jpeg', '.md', '.csv'})


def read_ledger(root, name, schema, collection):
    value = json.loads((root / '.harness' / name).read_text(encoding='utf-8'))
    require(isinstance(value, dict) and value.get('schemaVersion') == schema, f'{name}: schemaVersion 应为 {schema}')
    rows = value.get(collection)
    require(isinstance(rows, list), f'{name}: {collection} 必须为数组')
    ids = []
    for row in rows:
        require(isinstance(row, dict), f'{name}: 条目必须为对象')
        identifier = row.get('id')
        require(isinstance(identifier, str) and IDENTITY.fullmatch(identifier), f'{name}: 编号无效')
        require(identifier not in ids, f'{name}: 编号重复 {identifier}')
        ids.append(identifier)
    return value


def common(row, requirements):
    identifier = row['id']
    require(nonempty(row.get('title')), f'{identifier}: 缺少标题')
    require(strings(row.get('refs')) and all(ref in requirements for ref in row['refs']), f'{identifier}: 需求引用无效')
    require(isinstance(row.get('reason'), str) and isinstance(row.get('resumeWhen'), str), f'{identifier}: 缺少条件说明字段')
    if row.get('status') in {'blocked', 'paused'}:
        require(nonempty(row['reason']) and nonempty(row['resumeWhen']), f'{identifier}: 受阻/暂缓须填写原因和恢复条件')


def validate_ledgers(root):
    """只校验声明及关联，不将本机证据缺失或缺硬件误判为软件门失败。"""
    root = Path(root)
    errors = []
    todo = acceptance = None
    try:
        requirements = set(re.findall(r'\|\s*([A-Z]+-\d+)\s*\|',
                                     (root / '.docs/01_需求规格说明书.md').read_text(encoding='utf-8-sig')))
    except OSError as error:
        return [f'账本需求来源不可读: {error}']
    try:
        todo = read_ledger(root, 'todo.json', 2, 'tasks')
        ids = {task['id'] for task in todo['tasks']}
        graph = {}
        for task in todo['tasks']:
            common(task, requirements)
            identifier = task['id']
            require(task.get('status') in {'todo', 'in_progress', 'blocked'}, f'{identifier}: 仅保留活动开发任务')
            require(task.get('priority') in {'P0', 'P1', 'P2'}, f'{identifier}: 优先级无效')
            require(nonempty(task.get('component')) and nonempty(task.get('doneWhen')), f'{identifier}: 缺少组件或完成条件')
            dependencies = task.get('dependsOn')
            require(isinstance(dependencies, list) and all(isinstance(dep, str) and dep in ids and dep != identifier for dep in dependencies), f'{identifier}: 依赖不存在或引用自身')
            require(isinstance(task.get('testIds'), list) and all(isinstance(item, str) for item in task['testIds']), f'{identifier}: testIds 必须为编号数组')
            graph[identifier] = dependencies
        visited = set()

        def visit(identifier, stack):
            require(identifier not in stack, '任务依赖环')
            if identifier in visited:
                return
            for dependency in graph[identifier]:
                visit(dependency, stack | {identifier})
            visited.add(identifier)

        for identifier in graph:
            visit(identifier, set())
    except (OSError, ValueError, TypeError, KeyError, RecursionError) as error:
        errors.append(f'任务账本无效: {error}')
        todo = None
    try:
        acceptance = read_ledger(root, 'test.json', 1, 'checks')
        require(candidate_valid(acceptance.get('candidate')), '当前候选包身份无效')
        for check in acceptance['checks']:
            common(check, requirements)
            identifier = check['id']
            require(isinstance(check.get('origin'), str) and IDENTITY.fullmatch(check['origin']), f'{identifier}: 历史任务编号无效')
            require(check.get('status') in {'pending', 'running', 'blocked', 'paused', 'complete'}, f'{identifier}: 安排状态无效')
            require(check.get('method') in METHODS and check.get('kind') in KINDS, f'{identifier}: 执行方式或环境类别无效')
            require(strings(check.get('environment')) and strings(check.get('steps')) and nonempty(check.get('expected')), f'{identifier}: 缺少环境、步骤或预期结果')
            require('lastResult' in check, f'{identifier}: 缺少 lastResult（未测填 null）')
            result = check['lastResult']
            if check['status'] == 'complete':
                require(isinstance(result, dict) and result.get('outcome') == 'passed', f'{identifier}: 完成须有通过结果')
            if result is None:
                continue
            require(isinstance(result, dict) and result.get('outcome') in {'passed', 'failed'}, f'{identifier}: 测试结果无效')
            require(result.get('method') == check['method'] and result.get('kind') == check['kind'], f'{identifier}: 结果执行方式/环境类型不能替代验收要求')
            require(candidate_valid(result.get('candidate')), f'{identifier}: 结果缺少版本、源码及包摘要')
            require(nonempty(result.get('environment')) and nonempty(result.get('details')), f'{identifier}: 结果缺少实际环境或说明')
            require(nonempty(result.get('recordedAt')) and datetime.fromisoformat(result['recordedAt']).utcoffset() is not None, f'{identifier}: 记录时间必须含时区')
            evidence = result.get('evidence')
            require(isinstance(evidence, list) and bool(evidence), f'{identifier}: 缺少证据引用')
            for item in evidence:
                require(isinstance(item, dict) and nonempty(item.get('label')) and evidence_path(item.get('path')), f'{identifier}: 证据路径无效或越界')
                require((root / item['path']).resolve().is_relative_to(root.resolve()), f'{identifier}: 证据联接越界')
    except (OSError, ValueError, TypeError, KeyError) as error:
        errors.append(f'验收账本无效: {error}')
        acceptance = None
    if todo is not None and acceptance is not None:
        ids = {check['id'] for check in acceptance['checks']}
        for task in todo['tasks']:
            if any(identifier not in ids for identifier in task['testIds']):
                errors.append(f'任务账本无效: {task["id"]} 引用了不存在的验收项')
    return errors


def check_state(check, candidate):
    result = check['lastResult']
    if check['status'] in {'blocked', 'paused'}:
        return check['status'], STATES[check['status']]
    if result and result['candidate'] != candidate:
        return 'ready', '待复验 · 上次结果属于其他包'
    if result and result['outcome'] == 'failed':
        return 'failed', '测试失败'
    if check['status'] == 'complete':
        return 'passed', '已通过 · 当前候选包'
    return 'ready', '执行中' if check['status'] == 'running' else '待测试'


def escaped(value):
    return html.escape(str(value), quote=True)


def build_report(root, output):
    root, output = Path(root).resolve(), Path(output).resolve()
    require(output.suffix.lower() == '.html' and
            any(output.is_relative_to(root / directory) for directory in ['test-results', 'tmp']),
            '输出必须是仓库 test-results/ 或 tmp/ 内的 HTML，不能覆盖账本或源码')
    errors = validate_ledgers(root)
    if errors:
        raise ValueError('\n'.join(errors))
    tasks = json.loads((root / '.harness/todo.json').read_text(encoding='utf-8'))['tasks']
    ledger = json.loads((root / '.harness/test.json').read_text(encoding='utf-8'))
    candidate = ledger['candidate']
    counts = dict.fromkeys(STATES, 0)
    cards = []

    def card(identifier, origin, title, state, label, subtitle, details):
        counts[state] += 1
        cards.append(f'<article class="card" data-state="{state}" data-origin="{escaped(origin)}">'
                     f'<div class="card-top"><span class="eyebrow">{escaped(identifier)} · {escaped(origin)}</span>'
                     f'<span class="badge {state}">{escaped(label)}</span></div>'
                     f'<h2>{escaped(title)}</h2><p class="subtitle">{escaped(subtitle)}</p>'
                     f'<details><summary>查看步骤与证据</summary>{details}</details></article>')

    for task in tasks:
        details = f'<h3>完成条件</h3><p>{escaped(task["doneWhen"])}</p>'
        details += f'<p>需求：{escaped("、".join(task["refs"]))} · 依赖：{escaped("、".join(task["dependsOn"]) or "无")}</p>'
        details += '<p>关联验收：' + (' '.join(f'<a href="#check-{escaped(item)}">{escaped(item)}</a>' for item in task['testIds']) or '无') + '</p>'
        if task['reason']:
            details += f'<h3>阻塞原因</h3><p>{escaped(task["reason"])}</p><p>恢复条件：{escaped(task["resumeWhen"])}</p>'
        label = {'todo': '待开发', 'in_progress': '开发中', 'blocked': '开发受阻'}[task['status']]
        card(task['id'], task['id'], task['title'], 'dev', label, task['priority'] + ' · ' + task['component'], details)
    for check in ledger['checks']:
        state, label = check_state(check, candidate)
        details = f'<div id="check-{escaped(check["id"])}"><h3>需要的环境</h3><p>{escaped("、".join(check["environment"]))}</p>'
        if check['reason']:
            details += f'<h3>当前原因</h3><p>{escaped(check["reason"])}</p><p>恢复条件：{escaped(check["resumeWhen"])}</p>'
        details += '<h3>测试步骤</h3><ol>' + ''.join(f'<li>{escaped(step)}</li>' for step in check['steps']) + '</ol>'
        details += f'<h3>通过标准</h3><p>{escaped(check["expected"])}</p><p>需求：{escaped("、".join(check["refs"]))}</p>'
        related = [task['id'] for task in tasks if check['id'] in task['testIds']]
        if related:
            details += f'<p>相关开发：{escaped("、".join(related))}；条件齐备后再执行。</p>'
        result = check['lastResult']
        if result:
            result_label = '通过' if result['outcome'] == 'passed' else '失败'
            details += f'<h3>上次结果：{result_label}</h3><p>{escaped(result["recordedAt"])} · {escaped(METHODS[result["method"]])}</p>'
            details += f'<p>{escaped(result["environment"])}</p><p>{escaped(result["details"])}</p>'
            details += f'<p class="digest">源码：{escaped(result["candidate"]["commit"])}<br>包 SHA-256：{escaped(result["candidate"]["packageSha256"])}</p><ul>'
            for item in result['evidence']:
                path = root / item['path']
                text = escaped(item['label'])
                details += (f'<li><a href="{escaped(path.as_uri())}" target="_blank" rel="noopener">{text}</a></li>'
                            if path.is_file() else f'<li>{text} <span class="muted">（证据未随当前副本提供：{escaped(item["path"])}）</span></li>')
            details += '</ul>'
        else:
            details += '<p class="muted">尚无本项最终候选包的结果；既有局部通过记录保留在详细设计中，不自动迁称同包通过。</p>'
        details += '</div>'
        card(check['id'], check['origin'], check['title'], state, label,
             METHODS[check['method']] + ' · ' + KINDS[check['kind']], details)
    buttons = '<button class="stat active" data-filter="all" aria-pressed="true"><strong>' + str(len(cards)) + '</strong><span>全部事项</span></button>'
    buttons += ''.join(f'<button class="stat" data-filter="{key}" aria-pressed="false"><strong>{count}</strong><span>{STATES[key]}</span></button>' for key, count in counts.items())
    options = '<option value="">全部来源任务</option>' + ''.join(f'<option>{escaped(origin)}</option>' for origin in sorted({item['origin'] for item in ledger['checks']} | {task['id'] for task in tasks}))
    digest = hashlib.sha256((root / '.harness/todo.json').read_bytes() + (root / '.harness/test.json').read_bytes()).hexdigest()
    values = {'CARDS': '\n'.join(cards), 'BUTTONS': buttons, 'OPTIONS': options,
              'GENERATED': escaped(datetime.now().astimezone().isoformat(timespec='seconds')),
              'CANDIDATE': escaped(candidate['version'] + ' · 源码 ' + candidate['commit'][:7]),
              'DIGEST': escaped(digest), 'PACKAGE': escaped(candidate['packageSha256'])}
    template = Path(__file__).with_name('harness_report.html').read_text(encoding='utf-8')
    document = re.sub(r'\{\{([A-Z]+)\}\}', lambda match: values[match[1]], template)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(document, encoding='utf-8')
    return output


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, default=ROOT / 'test-results/harness/index.html')
    args = parser.parse_args()
    try:
        print(build_report(ROOT, args.output))
        return 0
    except (OSError, ValueError) as error:
        print(f'FAIL 看板生成：{error}', file=sys.stderr)
        return 1


if __name__ == '__main__':
    sys.exit(main())
