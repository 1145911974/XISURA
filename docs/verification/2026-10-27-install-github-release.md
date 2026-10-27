# 1.0.110：本机安装与GitHub交付

2026-10-27，用户明确授权安装当前可用版本并上传GitHub。实际安装与验收成功后发布正式资产；此前本机及GitHub均为1.0.109。

## 构建纠正

原R3已在本机运行验收，但其`runtimeconfig.json`实际引用系统.NET10，缺少`coreclr.dll`；旧“自包含”文字不准确。安装器离线完整性检查正确拒绝该payload。

本次对同一冻结源码显式`--self-contained true -t:Rebuild,Publish`，补齐.NET10离线运行库及Windows App SDK。核心应用程序集SHA与R3逐项一致；运行时配置与依赖清单改变，未改应用行为。原1358/1358软件回归有效；离线产物另通过两轮实际GUI/Service生命周期，安装器15/15通过。安装器有既有WIX1149配置警告，不冒称零警告；实际服务停止/重启另做验证。

## 本机升级

- 正式MSI：`artifacts/install-release-1.0.110/XISURA-1.0.110-Stable.msi`，SHA-256 `76C5CDE71CA2D6C6440B171981F7DD6284C0653A693935CF599007FCD20CFB8B`。
- 本机安装使用局部MST：服务设为手动、清除安装启动位，并绑定原journal，安装阶段不启动109或110服务。原MSI字节未改；公开安装包保留标准安装行为。
- 已备份用户配置、ProgramData、完整原journal及可回退109安装源。MSI退出0，无重启；注册项、Program Files GUI/Service均为1.0.110.0；已安装文件逐项匹配离线payload。
- 原配置与恢复记录安装阶段字节不变；前后13组独立控制比较一致。
- 安装目录实际GUI连接Windows服务，CPU/GPU/风扇页、托盘、完全退出和重开两轮通过；Windows服务停止/重启通过，配置及13组控制不变。
- 将原恢复日志183个文件完整复制到`C:/ProgramData/JiaolongControlCenter/control-journal-110`，逐文件SHA及数量一致，原Temp目录保留。服务参数已绑定新目录；迁移没有建立空日志绕过冲突，13组控制不变。新目录成为本机后续会话的权威journal。
- 服务最终设为自动启动并运行；不再启动109旧服务，不安装驱动、不重启电脑、不跑负载测试。

## GitHub交付

仓库：`https://github.com/1145911974/XISURA`；正式Release目标：[v1.0.110](https://github.com/1145911974/XISURA/releases/tag/v1.0.110)。上传MSI、完整离线便携包、SHA清单、源码冻结清单与可复现源码归档；发布前逐项核对GitHub资产大小与SHA，安装和上传的最终状态以[STATE](../progress/STATE.md)及发布元数据为准。

发布源码在独立`publish110`工作树整理，只纳入R3冻结的1413份相关输入和本版交付文档；原release108大量既有改动/索引保留。完整源码树SHA由GitHub tag及`source-lock.json`记录；运行核心DLL SHA仍为[R3报告](2026-10-27-usable-release-r3.md)所列值，不能把原R3 ZIP SHA当本次离线包SHA。

正式发布已完成：源码提交及v1.0.110标签为`46e1b50ea2e946d1b0c5e495c346d98ba6df3ada`；5项发布资产均逐项核对GitHub大小及SHA-256通过，匿名latest确认v1.0.110且非草稿。正式下载：[v1.0.110](https://github.com/1145911974/XISURA/releases/tag/v1.0.110)。main随后仅同步最终交付文档；标签、源码冻结清单和发布包不改动。

## 证据与限制

证据根`C:/Users/Administrator/AppData/Local/Temp/XisuraUsableRelease-20261026/`：

- `offline-lifecycle110/result.json`：离线包两轮运行通过。
- `install-publish-110/build-result.json`、`installer-tests.trx`：MSI及15项回归。
- `installation-result.json`、`msi-install.log`：真实升级、配置/文件/控制验证。
- `installed-lifecycle/result.json`：实际Windows服务与GUI两轮、停止/重启验证。
- `journal-migration-result.json`及`journal-migration-hashes.json`：183文件完整迁移和状态保持。
- `installed-durable-lifecycle/result.json`：新持久日志目录下的GUI及服务生命周期。
- `github-release-result.json`、`release-asset-hashes.json`：最终发布与远端资产核验。

除`offline-lifecycle110`外，上述文件均位于证据根下`install-publish-110/`。原R3及恢复证据保留，不倒改历史包。候选MST临时验证文件清理被自动审批以`blocked by policy`拒绝，留在C盘证据目录，不作为发布资产。

功能缺口见[支持矩阵](2026-10-26-usable-release-support.md)，后续顺序见[优化计划](../progress/2026-10-27-post-release-roadmap.md)。高级风扇/强冷、GPU锁频/任意电压仍D；GPU功率C；CPU未知字段拒写。本次安装/发布不把原71项、18/18 Partial或未测自动化组合变成完成项。
