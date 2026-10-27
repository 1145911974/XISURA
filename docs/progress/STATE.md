# XISURA 当前状态

更新：2026-10-27。唯一当前状态；执行[首版目标](USABLE-RELEASE-GOAL.md)，用户最新决定优先。

**最新交付决定（2026-10-27）：** 后续每个正式可用版本完成必要回归及实际运行验收后，必须安装到本机并上传既有项目GitHub仓库，记录版本、源码/产物SHA、安装验证与GitHub链接；包括必要提交/推送，持续授权不再逐版询问。禁止夹带无关既有改动、更改仓库可见性或覆盖用户配置；其他硬件、恢复和禁测边界继续有效。

**安装/上传实况（2026-10-27实际执行）：** 本机已从109升级至1.0.110.0，MSI退出0、无需重启，已装文件逐项匹配离线payload。实际GUI/Windows服务两轮及服务停止/重启通过；原配置和13组控制值不变。183份原journal完整迁移至`C:/ProgramData/JiaolongControlCenter/control-journal-110`，原Temp证据保留；服务Auto/Running并绑定持久目录。GitHub上传执行中，未验证远端资产前不记完成。[安装发布记录](../verification/2026-10-27-install-github-release.md)。

- **R0 3/3**：原33失败独立复现；真实调用/授权/退出路径核对；实际Release构建并启动。
- **R1 3/3**：EC写前持久化、管理员IPC、Keep持久化/Freeze重试、灯光读回/退出停动态及idle风扇交还假ownership修复；原会话按用户保留决定安全退役。
- **R2 3/3**：AMD/OEM按字段收敛；高级风扇/强冷、GPU锁频/任意电压D并关闭直间接写入口；GPU功率C。保留可靠常用功能及不依赖未知字段的路径。
- **R3 4/4**：1.0.110.0／Release／R3冻结构建；1358/1358、0失败0跳过；正式GUI/Service两轮默认退出重开、空闲交还与Keep三会话通过；SHA/矩阵/短用法/清单齐备，ZIP共918文件逐项哈希校验通过。
- **R4未开始**：用户反馈、高级功能、兼容性和深入优化；原71项库存未全部完成。主页18/18为Partial流程观察；旧10/18及旧阶段4的1/6只为历史。

## 产物与证据

- 当前安装包：`artifacts/install-release-1.0.110/XISURA-1.0.110-Stable.msi`，SHA-256 `76C5CDE71CA2D6C6440B171981F7DD6284C0653A693935CF599007FCD20CFB8B`；离线产物`artifacts/usable-release-1.0.110-offline`。原R3 ZIP保留为历史验收证据，其“自包含”旧说明不准确；本次显式补齐运行库且核心应用DLL SHA不变，不使用原ZIP SHA指代离线包。
- [构建与实机报告](../verification/2026-10-27-usable-release-r3.md)列版本/SHA及边界；[支持矩阵](../verification/2026-10-26-usable-release-support.md)、[用户清单](../verification/2026-10-26-usable-release-user-checklist.md)。
- 证据根：`C:/Users/Administrator/AppData/Local/Temp/XisuraUsableRelease-20261026/`。最终构建`final-r3-20261027-004158-896`；`published-lifecycle3`、`published-keep-logo1`均Passed/HostExit0。最终859产物/1413源码零差异，Completed、零owner/未决项/候选进程，旧Service Stopped。
- 用户主动重启并要求保留当前状态；`external-restart-retirement/result.json`证明13控制/配置未变、Completed。OldBaselineRestored=false，不覆盖用户重启后Turbo/灯光，不写旧65W。

## 边界与下一步

MRID6-23／7745HX／RTX4070 Laptop／V35、EC0.35。风扇仅空闲交还实机通过，不外推高级接管；CPU/GPU窄B不宣称性能实效/稳定性。Keep默认关闭；开启只保留已应用值，完全退出停止Follow/AUTO，渐变停在最后静态帧。

数值点立即恢复鲜读准确试前值并独立核验，失败/取消也一样；Keep不豁免。原journal/恢复证据保留，不建空日志绕冲突。全部输出C盘。不reset/clean；提交推送仅限已授权版本交付，不启动109旧服务。禁游戏/跑分/轨迹/MUX/睡眠/自行重启/EC盲写/驱动安装。

本机安装已完成，GitHub交付执行中；持续授权不再重复询问。R4按[后续计划](2026-10-27-post-release-roadmap.md)围绕反馈和延期功能推进。历史报告保留当时事实，不倒改旧包。自动审批曾以“blocked by policy”拦截旧生成候选及MST临时验证文件的清理，未绕过，暂留C盘证据目录，不作发布资产。
