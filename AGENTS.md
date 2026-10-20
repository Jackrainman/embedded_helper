# Embedded Helper 工作约定

始终中文回复。开工读取 [README](README.md)、[任务账本](.harness/todo.json) 和 Git 状态，再读取任务涉及的设计章节。

规则仅在本文件；需求及 AC 在 [需求](.docs/01_需求规格说明书.md)，架构决策在 [概要](.docs/02_概要设计说明书.md)，接口及验证证据在 [详细设计](.docs/03_详细设计说明书.md)。活动任务仅维护 `.harness/todo.json`，完成过程留在 Git；不得新增计划、状态、研究或决策副本。项目技能仅在有稳定可复用流程时增加，不复制个人技能。

本仓库拥有C#客户端和Harness；Python产品原型已归档于Git，恢复只在`tmp/`隔离副本。Debug_LAN后续源码归LAN_Debug/client，Linux服务端归LAN_Debug/server；现有工具下载仍兼容Template Release，不与旧位置并行开发。配置试验只写 `tmp/` 内隔离副本；`tests/fixtures/` 是带来源和摘要的只读输入，不运行其中的工程钩子。不得为观察擅自烧录、复位、停核或部署服务；设备操作遵循具体任务授权。

产品采用C#、WPF、.NET 10 LTS，SDK与依赖锁定见README。Python 3.12+仅用于标准库Harness，无pip依赖。安装钩子：`python scripts/verify.py --install-hooks`。统一仓库验证：`python scripts/verify.py`，不构建；C#静态检查、构建和测试须显式用`--client`，只检查准备状态用`--client-preflight`。钩子验证暂存快照；缺工具/硬件输出未验证，不得替代实测结论。用户禁止构建的任务不得调用`--client`或其他构建/打包入口。

本地产物分工：`tmp/`只放隔离试验和待排查现场；正式复用脚本归`scripts/`并纳入Git。GUI验证成功默认删除运行副本，每配置只保留最新成功证据；失败现场排查结束后清理。`test-results/`存放GUI截图、日志、TRX及维护证据，TRX保留当前Debug/Release结果，失败或验收所需证据按用途保留，不复制成永久状态文档。新打包输出默认`dist/candidate/`，需保留的完整同包候选归`dist/candidates/版本-提交短SHA/`；同版本不同字节以用途后缀区分，完整候选按回退/验收需要保留，不按版本名自动去重。清理前核实绝对路径、占用及可再生性，保留非运行时data和独有证据；开发bin/obj及便携data/runtime可再生，settings、工程事务和串口记录不得混同缓存清理。

每批从账本取任务，实施、验证、更新权威文档后独立提交；不默认推送、打标签或发布。完成条件须有可执行验证和真实退出结果，模拟、离线、实机分别说明。版本纪律见概要10.4；成品发布按需求AC-13及详细设计12.4检查同包证据，普通仓库检查通过不等于客户端可发布。

代码修改默认执行[修改、构建、桌面交付和测试 workflow](.agents/workflows/change-test-deliver.md)。未指定测试范围时仅测本机软件及 Computer Use UI；结束关闭本次自己打开的窗口，保留用户原有窗口。仅用户明确要求不编译时跳过构建交付。版本按真实变更类型由提交钩子自动升级，打包及 CI 强制校验。
