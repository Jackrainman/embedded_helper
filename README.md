# Embedded Helper 设计文档

文档版本0.18，2026-10-19。已建立C#、WPF和.NET 10客户端，提供工程读取、ELF标量及固定地址结构体成员索引、变量/RTT采集入口、远程JSON维护、独立串口、设置与诊断。Windows x64自包含单EXE按开发内测交付；硬件验收由用户后续执行，不宣称完整P0通过。

## 阅读顺序

1. [需求规格说明书](.docs/01_需求规格说明书.md)：为什么做、第一版做什么、使用场景、约束与验收。
2. [概要设计说明书](.docs/02_概要设计说明书.md)：模块职责、仓库依赖、配置归属、连接与采集架构、实施顺序。
3. [详细设计说明书](.docs/03_详细设计说明书.md)：数据与接口草案、文件写入恢复、工具版本、SDK工作进程、串口流程与测试矩阵。

## 当前范围

LAN_Debug/client维护客户端脚本，LAN_Debug/server维护Linux服务；客户端脚本包统一从LAN_Debug Release下载。旧Template EXE Release已撤下，不再下载；已有旧任务在显式重新导入时迁移。GUI本身无需Python；执行工程内脚本需要Python 3.10+和SEGGER软件。

Windows本地小助手配合VS Code/EIDE使用。第一版围绕现有模板工程的可选远程接入、本地/远程变量可视化、本地原生RTT及独立串口助手。远程变量与既有网页RTT二选一、手动切换；NRF按本地COM，TCP串口暂不做；串口记录由上位机保证顺序，时间戳可选。

功能优先级以需求第4节为准，发布通过条件以第8节AC验收项为准。开发待验证问题统一见需求第9节，不要求用户逐项决策后才开工。

## 维护基线

需求与验收统一维护在需求说明书；概要设计维护模块职责和架构决策；详细设计维护数据模型、算法和测试步骤。下游文档引用需求编号，不重复制定功能范围或发布门槛。用例、实现方案和验收步骤是不同层次的描述，不因涉及同一功能就删除。

配置归属见概要第6节，旧工具兼容见REM-07及详细设计第6节，网页快捷入口规则见详细设计第11节，产品版本纪律见REL-10及概要10.4。首批范围见需求4.1；开发内测按AC-14，完整验收按AC-13，不代表全部P0已完成。

## 后续更新规则

需求变更先更新需求编号和验收条件，再同步模块职责、详细接口及样本。新增GUI可改字段时，检查读取、校验、变更计划、写入、撤销、CLI消费和旧版本迁移，避免只新增界面控件。

新增模板/硬件区分已移植、实机已验证和实验功能，不凭相似目录或驱动接受参数宣布实测通过。接口冻结后记录版本和兼容规则；重大决策在概要设计的ADR表保留依据。

G4一键VS Code接入/共享移除及LAN_Debug Release脚本包的下载、离线导入和版本锁定已可用。H7/F4/F1已通过配置样本移植；可选EIDE上传切换、官方工具清单刷新与网页切换前停止采集已实现。硬件与完整P0验收仍待用户执行。文档中的“拟新增”“候选”“待验证”均不能当作已完成能力。

开发采用关键风险原型验证、小批次迭代交付、稳定版本补丁维护的节奏；集中功能变更留到下一轮更新，详见概要14.1。

## 开发与验证

标准库Python Harness仅用于开发与打包，不进入客户端运行依赖；产品及其测试采用C#。提交、验证及设备操作边界见根AGENTS；推送、打标签与发布按具体任务授权执行。

| 准备项 | 必要内容 |
| --- | --- |
| `.gitignore` | 排除个人工具状态、依赖、构建产物、缓存、日志和本机data；保留AGENTS、项目技能、任务账本、共享配置及必要样本。不要整体忽略`.vscode`、`.eide`、`.embedded_helper`或全部ELF文件。 |
| `.gitattributes` | 明确文本换行与二进制样本处理；新文件用UTF-8，不顺带批量改写既有文件。 |
| 根`AGENTS.md` | 中文回复、文档入口、源码归属、修改边界、验证方式与完成定义；只引用真实存在的入口，不复述需求全文。 |
| `.harness/todo.json` | 活动开发任务；包含ID、状态、需求/AC引用、开发依赖、关联验收和完成条件。完成后移除，过程进入Git。 |
| `.harness/test.json` | 验收安排与最近结果；明确执行方式、所需环境、步骤、预期、阻塞/暂缓原因和恢复条件。通过结果绑定候选包、时间、实际环境和证据。 |
| 验证与环境检查 | 用一个入口编排文档链接、任务结构、文件边界和环境检查；失败返回非零，缺工具/硬件报告未验证；用正例、反例确认检查有效，再接提交钩子并验证真实触发。 |
| M0隔离样本 | 配置试验用工程副本；记录上游SHA、脱敏范围和已知预期，覆盖中文空格路径、未知字段、ELF及串口字节；结果记录代码基线、工具版本、输入/硬件、退出结果和未覆盖项。 |

SDK 10.0.402锁定在[global.json](global.json)，运行时10.0.12；根[VERSION](VERSION)通过[scripts/bump_version.py](scripts/bump_version.py)更新。C#工程已有NuGet锁文件：LibObjectFile 2.3.1、YamlDotNet 18.1.0、System.IO.Ports 10.0.12；测试使用xUnit。SDK与库更新须显式修改并重新验证。

`.agents/skills`用于稳定、可复用的项目流程；已有EIDE/J-Link/LAN技能按任务复用，缺失时明确说明，不复制个人技能形成双源，也不预建空技能目录。真实工程写入、烧录/复位/停核和服务部署按具体任务授权执行，文档研究不扩大设备操作范围。

已按用户要求安装UI UX Pro Max和Impeccable到`.agents/skills/`；各自`source.json`记录上游仓库与固定提交，保留原始许可及依赖文件。它们只用于开发审查，不进入客户端运行包。WPF界面评估见概要7.5，安装和验证边界见详细18节。

必须学习本地`D:/01_Workspace/04_MyProjects/TeamHub`的AGENTS工作流与验证门、`.gitignore`、D-091文档单源、提交钩子安装及验证脚本；定向阅读ARC-DEC-004、ARC-MILE-006、ARC-INC-007，吸取钩子未接通、技能副本漂移和文档反弹的教训。采用其单源与真实验证原则，具体技术栈、CI平台、版本脚本和Git授权依据本项目实施。

仓库长期文档保留本入口与三份规格/设计；研究结论写回对应权威位置，普通审查过程、完成日志和原始测试输出不新增永久文档，历史通过Git恢复。

统一入口：`python scripts/verify.py`，只运行仓库检查与Harness测试，无pip依赖、不构建、不访问设备。首次克隆运行 `python scripts/verify.py --install-hooks`；提交门仍验证索引快照。开发需要Python 3.12+、Git；客户端开发另需global.json指定的SDK。

`python scripts/verify.py --client-preflight`检查客户端工程和精确SDK，不构建；缺少时返回2。`--client`执行锁定恢复、格式检查、Release构建及C#测试；加`--configuration Debug`验证Debug布局，Worker由测试构建自动准备，不依赖Release目录。`python scripts/package_client.py`从干净已提交基线构建ZIP、解压后运行真实GUI/CLI自检并生成同包证据，产物在dist/candidate。旧Qt入口已退出。待修改见[开发账本](.harness/todo.json)，手动测试、环境缺口及验收结果见[验收账本](.harness/test.json)，历史证据见详细设计18节。

运行 `python scripts/harness_ledger.py` 生成 `test-results/harness/index.html`，双击即可离线查看；支持状态、来源任务和全文筛选。看板不修改账本、不执行测试或设备操作。更新JSON后须重新生成，HTML不入Git；换候选包会把旧包通过显示为待复验，单纯文档提交不会改变所验包。缺少本机原始证据会明确标注，不凭链接存在宣称重新测试；此看板不替代发布门。

源码构建后的离线GUI验证使用 `python -X utf8 scripts/gui_smoke.py`；支持 `--configuration Debug`、`--app-dir <已有WPF构建目录>`、`--timeout <秒>`，不会自动构建。入口按脚本位置定位仓库，可从其他工作目录启动。样本复制至tmp隔离副本，不复制既有data；结果、日志和24张截图归test-results/gui-smoke，每配置自动保留最近一次成功证据，失败证据保留待排查。成功删除运行副本，失败保留现场；`--keep-workspace`可显式保留成功副本。

本地产物保留和清理规则见根AGENTS。已保留的历史完整候选集中在`dist/candidates/版本-提交短SHA/`，同版本不同字节以用途后缀区分；旧临时验证材料集中在test-results/legacy，本次维护摘要见详细设计18节。既有候选保持原源码/SDK绑定，本轮源码验证不替代重新打包验收。

Python产品原型、对应测试、requirements.lock及Qt打包探针归档于Git基线 `a7f30e11a86b8a60f15f853202da19bfba99a4c4`，当前工作树移除，不建归档文档或第二套产品实现。需要复现时在tmp隔离副本恢复该提交；[样本](tests/fixtures/manifest.json)保留供C#验证，历史通过不自动成为C#通过。

## 启动、产物与发布

直接交付 `EmbeddedHelper.exe`，另提供同字节EXE的可选ZIP、清单及摘要。将EXE放在可写目录并双击，无需Python或预装.NET；J-Link软件和设备驱动由用户安装。DWARF4内置解析，DWARF5调用已有ARM readelf（自动定位EIDE工具链，亦可在设置中填写）。软件在自身目录创建data；升级保留data，工程变量设置保存到.embedded_helper/watch.json，共享连接保存到.embedded_helper/connection.json。

[Windows工作流](.github/workflows/verify.yml)运行仓库及客户端门。[内测发布工作流](.github/workflows/release.yml)核对候选运行来源和同包证据后上传原包；发布输入由[scripts/release_gate.py](scripts/release_gate.py)检查。用户授权先发布离线开发内测：developer-preview只接受软件门真实通过，并强制列明硬件与纯净Windows未验证；完整验收仍保留tested-prerelease门，契约见详细12.4。仓库管理员权限已核实，远端运行结果随批次记录，分支保护未配置。

已发布的开发内测版本：[1.0.24-alpha.1](https://github.com/Jackrainman/embedded_helper/releases/tag/v1.0.24-alpha.1)，[直接下载单EXE](https://github.com/Jackrainman/embedded_helper/releases/download/v1.0.24-alpha.1/EmbeddedHelper.exe)（约98MiB）。本次包含Audit复核后的RTT、变量身份、配置事务及工具校验修复，使用成功CI的原始候选包；自动门通过，硬件与纯净Windows未验收。发布身份及证据见验收账本和详细设计18.34。

当前开发版本以 VERSION 为准，包含 MCU Viewer 坐标轴、完整记录 Excel 导出及 LAN_Debug 工程接入。最终同包远程实测由验收账本跟踪，保留 C-07 来源；构建成功不代表硬件验收或发布通过。

远程接入：打开工程，在远程配置页填写服务器地址和端口，点击“一键导入 LAN_Debug”，自动准备已核验工具并写入工程任务，默认切换 EIDE 到 Custom CLI 并写入烧录命令；勾选“不替换Custom CLI烧录选项”则保留当前烧录配置。导入只写配置，不执行烧录。离线可直接选择官方 ZIP。生成任务通过 Python 使用工程内 LAN_Debug/jlink_flash.py 的相对路径，无需绑定本机环境变量或重启 VS Code。新配置使用 LAN_Debug/remote-jlink.json；已有 Debug_LAN 或工程根配置继续兼容。重新导入迁移由助手管理的旧入口，保留用户本地任务；冲突时提示并保留原文件。移除只恢复受管配置，保留远程 JSON、工具锁和缓存供复用。客户端脚本可随工程提交 Git，另一台电脑只需准备 Python 3.10+ 和 SEGGER。

单EXE首次启动自动将内部运行组件准备到旁边data/runtime，设置仍在data/settings；搬迁时携带data保留配置，更新只替换主EXE。内部SDK工作进程和CLI保持隔离，用户无需管理DLL。运行时可能另用Windows临时缓存。

串口打开/关闭合并为单按钮，绿灯表示驱动连接正常，红灯表示未连接或故障，附带文字；切换期间禁用重复操作。程序图标和页首logo使用用户提供的原图。

主导航为工程、MCU Viewer、RTT监视、串口助手、设置与诊断。工程内含工程概览与远程配置；共享连接配置从右上角独立窗口进入。MCU Viewer负责搜索添加变量、采样率及曲线；RTT监视负责本地Up 0日志和远程网页入口，两者共用连接且一次仅持有一个采集会话。共享连接显式保存，旧watch.json连接字段兼容读取；RTT无需ELF，变量采集不隐式启用RTT。本机DLL与readelf路径在设置与诊断维护。远程网页停止仍需手动操作。

顶部统一显示工程/目标、配置状态及本地/远程选择。每次打开工程默认本地，绿色只代表配置有效，实际采集状态单独显示。远程未配置时提示并保留原选择；采集中禁止切换。连接方式选择不自动连接，也不改变量配置。

固定地址结构体、嵌套成员及定长数组可按完整路径搜索观察；位域、匿名成员、联合体、指针跟随、64位数值暂不支持。MCU Viewer左侧搜索/已观察变量独立滚动，右侧图表占满内容区高度；缺少本机DLL仍可查看和编辑已解析变量，但不能开始采集。

仅需本地封装且明确不运行冒烟时使用 python scripts/package_client.py --skip-smoke --output <新目录>。该模式执行软件逻辑门后封装，标记unverified-local，不启动GUI/成品冒烟，不运行发布门；产物须后续验收，不能当已验证发布包。

未 release 的本地测试包构建后统一移动至 Windows 桌面 `EmbeddedHelper_test/EmbeddedHelper-版本-源码短提交-用途/`，供手动测试。保留清单和验证证据，不覆盖旧包及 data；软件逻辑通过不代表 GUI 或硬件验收通过。

采集与解析环境支持从工程/VS Code 明确工具设置、EIDE 默认目录、SEGGER 安装目录及 PATH 查找候选；显示来源、版本及静态校验结果。多候选需选择，已有路径不自动替换；J-Link 文件校验不等于加载或设备连接验证。

工程概览仅保留“打开工程”按钮：点击后选择目录，确认即加载工程；也可输入工程目录或 `.code-workspace` 路径后按 Enter 打开。取消目录选择保留当前工程。
