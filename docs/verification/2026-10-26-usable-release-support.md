# 首版支持矩阵与已知限制：1.0.110.0 Release R3

**状态：正式构建已实际安装并验收；GitHub状态见[安装发布记录](2026-10-27-install-github-release.md)。** [R3集中验收报告](2026-10-27-usable-release-r3.md)记录应用功能及实机生命周期；原R3依赖系统.NET10，本次安装补齐离线运行库，应用核心DLL SHA一致。设备限定MRID6-23／7745HX／RTX4070 Laptop／BIOS V35、EC0.35。下表分级仅覆盖明确范围；[71项库存](../progress/CURRENT-ACCEPTANCE.csv)中的Partial及延期项继续保留。

## 分级

- **A 已验证**：有独立实机证据确认实际效果，并记录准确恢复。
- **B 配置可用**：限定语义、单位、字段/单点的真实写入、可靠读回和精确恢复已证；缺少独立实际效果遥测，不外推至未测范围。
- **C 只读**：只显示观测值，不开放写入。
- **D 本版不支持**：隐藏或禁用直接、预设和 AUTO 写入口。D 表示产品支持决定，不表示测试被禁止。
- 库存中的“待确认”表示原完整功能仍有缺口；首版按以下A/B/C/D窄范围使用。

## A/B 证据范围

| ID / 项目 | 等级与可声明范围 | 证据 |
|---|---|---|
| HOME-01 主模式 | A：已记录实际模式切换和恢复后验；主页/Logo 整页范围仍待。 | [当前候选生命周期](2026-10-21-current-candidate-live-lifecycle.md) |
| HOME-06 触控板 | B：UI 开关、CIM 独立读回及原值恢复；实体触控效果未验。 | [快捷控制只读与实测](2026-10-24-home-quick-readonly.md) |
| HOME-07 Fn Lock | B：UI 开关、CIM 独立读回及原值恢复；实体组合键效果未验。 | [快捷控制只读与实测](2026-10-24-home-quick-readonly.md) |
| HOME-08 Logo 快捷开关 | A：实物开关、候选读回及恢复。 | [基础快捷控件](2026-10-16-basic-quick-controls.md) |
| HOME-09 Win 键 | A：用户确认开关行为及恢复。 | [能力闭合台账](2026-10-12-capability-closure/field-acceptance-ledger.md) |
| CPU-04 AC 频率上限 | B：4700→4600→4700，独立 powercfg 后验；不含负载性能结论。 | [CPU AC 单字段往返](2026-10-20-cpu-partial-live-edit.md) |
| CPU-05 DC 频率上限 | B：4700→4600→4700，独立 powercfg 后验；不含电池续航结论。 | [CPU DC 单字段往返](2026-10-20-cpu-dc-live-edit.md) |
| CPU-06 Boost | B：UI 开关、独立 powercfg 及恢复；不含负载/续航结论。 | [Boost 与停泊往返](2026-10-20-cpu-boost-parking-live-edit.md) |
| CPU-08 全核/逐核 CO | B：本机已验证值和精确恢复；范围限证据记录的核心/数值，不含稳定性测试。 | [CO 往返与退出默认](2026-10-20-live-co-roundtrip-and-exit-default.md) |
| CPU-12 PPT | B：独立写入、读回和恢复的窄范围；负载效果禁作声明。 | [V35 PPT 准入](2026-10-23-v35-ppt-admission.md) |
| CPU-14 TDC | B：记录中的 TDC 单字段操作、回读/恢复及隔离合同；UI单位mA，不外推VRM。 | [V35 PM 前缀](2026-10-22-v35-pm-prefix.md) |
| CPU-15 EDC | B：记录中的 EDC 单字段操作、回读/恢复及隔离合同；不扩展到 VRM。 | [V35 PM 前缀](2026-10-22-v35-pm-prefix.md) |
| CPU-16 MP1 温限 | B：已记录单点读回与恢复；不代表 RSMU 或温控负载效果。 | [V35 MP1 往返](2026-10-22-v35-mp1-roundtrip.md) |
| CPU-18 AC 核心停泊 | B：UI 单点及独立 powercfg 恢复；不含负载/续航。 | [Boost 与停泊往返](2026-10-20-cpu-boost-parking-live-edit.md) |
| CPU-19 DC 核心停泊 | B：UI 单点及独立 powercfg 恢复；不含负载/续航。 | [Boost 与停泊往返](2026-10-20-cpu-boost-parking-live-edit.md) |
| CPU-20 PBO Scalar | B：单点设置、独立读核、退出恢复。 | [PBO 只读准入记录](2026-10-21-pbo-advanced-readonly.md) |
| CPU-21 逐核 CO 编辑 | B：仅已验证首核单点、独立八核确认和恢复；其余核心不外推。 | [逐核 CO 单点](2026-10-20-cpu-per-core-live-edit.md) |
| GPU-03 核心偏移 | B：0→+1 MHz→0、127 点 VF 保持和独立读回。 | [GPU 核心偏移往返](2026-10-16-candidate-gpu-core-roundtrip.md) |
| GPU-04 显存偏移 | B：0→+1 MHz→0、独立读回和核心/VF 保持。 | [GPU 显存偏移往返](2026-10-16-candidate-gpu-memory-roundtrip.md) |
| GPU-05 V/F 曲线 | B：记录中的单点曲线应用、独立读回和完整恢复；不含负载稳定性。 | [GPU V/F 往返](2026-10-16-candidate-gpu-vf-roundtrip.md) |
| GPU-06 V/F 重置 | B：重置后独立 127 点读回及恢复后验。 | [GPU V/F UI 退出](2026-10-20-gpu-vf-ui-exit.md) |
| GPU-09 GPU 预设/跟随 | B：仅已记录 Gaming:1 显存 −1 MHz 跟随及恢复；不代表全槽/全模式。 | [GPU 预设 UI](2026-10-16-gpu-preset-ui.md) |
| LIGHT-01 常亮/渐变 | A：用户确认实物效果和恢复，仅支持常亮/渐变；循环已取消。 | [手动灯光验收](2026-10-16-manual-lighting-acceptance.md) |
| LIGHT-02 亮度 | A：单点、读回及恢复。 | [灯光 UI 退出](2026-10-20-lighting-ui-exit.md) |
| LIGHT-03 颜色/HEX | A：用户确认颜色往返、读回与恢复。 | [灯光输入验收](2026-10-16-lighting-input-acceptance.md) |
| LIGHT-04 速度 | A：用户确认渐变速度单点与恢复；不含帧率/性能轨迹。 | [渐变 UI 退出](2026-10-20-gradient-ui-exit.md) |
| LIGHT-05 Logo 灯 | A：实物开关、读回和其余字段保持。 | [灯光输入验收](2026-10-16-lighting-input-acceptance.md) |
| LIGHT-06 灯光预设跟随 | B：部分跟随退出恢复已证；完整物理键盘模式同步/全部停止边界未证。 | [后台灯光跟随](2026-10-16-background-lighting-follow.md) |
| AUTO-01 自动启停 | B：配置启停和退出恢复已记录；真实触发效果/全部 Follow 组合未证。 | [模式漂移隔离](2026-10-23-mode-windows-drift-containment.md) |
| AUTO-05 AC 办公映射 | B：限定 AC/低负载办公场景有记录；不是其他上下文、温控或负载保证。 | [自动化映射与准入](2026-10-23-mode-windows-drift-containment.md) |
| AUTO-10 候选条件 | B：仅已记录的 AC idle/Office 单点；其他条件待验。 | [自动化映射与准入](2026-10-23-mode-windows-drift-containment.md) |
| AUTO-12 空闲计时 | B：有限 AC idle 观察及退出恢复；worker 行为证据部分来自 fake 硬件。 | [自动化映射与准入](2026-10-23-mode-windows-drift-containment.md) |
| SET-01 开机自启 | B：候选任务 UI 配置、XML 读回和恢复；真实登录启动/Release 安装未验。 | [自启注册往返](2026-10-23-startup-registration-roundtrip.md) |
| SET-02 窗口大小 | A：同 DPI 保存/重开及偏好恢复；跨 DPI 未验。 | [设置偏好 UI](2026-10-12-capability-closure/settings-preferences-ui.md) |
| SET-03 Logo 样式 | A：已记录预览、读回和还原的模式范围；Office/DPI 未验。 | [设置偏好 UI](2026-10-12-capability-closure/settings-preferences-ui.md) |
| SET-04 快捷菜单编辑 | B：保存/取消顺序及配置恢复；真实拖拽排序未验。 | [快捷菜单保存反馈](2026-10-20-set04-save-feedback.md) |
| SET-05 诊断包导出 | A：离线导出、包校验及服务退出后留存读取。 | [服务诊断留存](2026-10-21-service-diagnostic-retention.md) |
| FAN-01 EC自动／固件交还 | B，限空闲交还：正式GUI交还按钮、零额外控制污染、退出重开已证；未在本版开启自定义EC接管，不能宣称真实高级目标写入/恢复或RPM实效通过。 | [R3集中验收](2026-10-27-usable-release-r3.md) |
| SET-06 退出后保留配置修改 | B：默认关闭；开启后Logo真实写入/双getter读回，退出保留及重开已证，再精确恢复原值和配置字节。渐变完全退出仅保留最后静态帧，软件动态调度停止。 | [R3三会话验收](2026-10-27-usable-release-r3.md) |


## 其余范围的四级支持

| 范围 | 首版等级与边界 |
|---|---|
| HOME-02/03、CPU-22 子档/预设/槽位 | B仅指配置保存、可靠已知字段的应用/读回/恢复；18/18槽有Partial流程观察，不能当18/18实效通过。Turbo子档实效、所有跨模式跟随组合仍属延期库存。灯光预设只写灯光。 |
| HOME-04/05 Wi-Fi/蓝牙 | C：状态/设备枚举；无线开关未实测验收，本版不作为写功能交付。 |
| CPU-01 OEM温度墙 | C：本机读回；OEM写入D。MP1温限不是该字段的写入证据。 |
| CPU-02/03 SPL/SPPT | D：本机有效值/逆操作合同不足，未知值不预填，直接及组合写入拒绝。 |
| CPU-07 电源计划 | C：本机仅已有Balanced计划的读取；其他scheme切换未验，不创建新计划。AC/DC频率等已证参数仍按上表B。 |
| CPU-09/10/11/13/17 STAPM/Fast PPT/Slow PPT/VRM/RSMU温限 | C仅覆盖实际可读诊断值；未知继续未知，写入D。PPT/TDC/MP1不能代证这些字段。 |
| GPU-01 显卡模式 | C：Hybrid/当前态显示；MUX/路由切换D，本轮不切换。 |
| AUTO-02/03/04/06/07/08/09/11 | B仅指已证规则/映射配置编辑、保存与恢复；运行支持限已证可靠目标字段，AC低负载Office路径见上表。未证的负载、游戏、电池、温控、自然前台触发效果不作承诺；本版D字段不能通过AUTO写入。 |

上述窄范围不关闭原功能的 `Remaining`。主页18槽旧10/18描述已过时，完整证据及差异仍按问题保留。

## 明确限制

- **Fan D：** FAN-02–07（固定 RPM、风扇曲线、双通道联动、最大目标、强冷及预设/跟随）本版不支持；关闭直接、预设和AUTO写入口。EC Auto/固件交还只声明上表实测的空闲交还范围，固件自行决定转速。
- **GPU D：** GPU-02 核心频率锁定、GPU-08 任意电压；隐藏写入口。已读到支持范围或原样回传的包不等于当前值读回。
- **GPU C：** GPU-07 功率只读；禁止把动态观测当固定恢复目标或写入合同。
- **CPU 高级字段：** 逐字段展示能力与真实单位；TDC/EDC为mA，PPT为W，温度为°C。未支持字段拒写，不阻断不依赖它的已知字段/AUTO。CO历史−50是试前值，不代表UI所有值均开放。
- 禁止盲写 EC 地址、MUX/显示切换、睡眠/重启、驱动安装、游戏/跑分/压力与热稳定性测试。此类限制本身不把功能标为 D。

## 集中验收用法

1. 使用[R3报告](2026-10-27-usable-release-r3.md)对应产物与SHA，不混用旧包/旧Service。
2. 启动前采集新鲜、可归属的试前状态。PPT120W、CO−50只是历史记录，不能当新会话恢复目标；不盲写旧65W。D项不写，C项只读。
3. 复用已通过的 A 范围，不重复制造验收点。只对明确允许且安全的 B 缺口逐项操作：鲜读 → 单点 → 独立同字段读回 → 立即精确恢复 → 独立恢复读回；任何未知、漂移或归属不明即停止。
4. R3已完成默认退出两轮、Keep三会话和空闲EC交还；用户集中验收只核实需要实物判断的体验，见[清单](2026-10-26-usable-release-user-checklist.md)。
5. 结果按 71 项 ID 记录证据路径、候选 SHA 和结论。Partial 保持 Partial；延期项不是完成项，禁测情景不转交为普通用户测试。

## 集成填写

| 字段 | 当前值 |
|---|---|
| 版本 | 1.0.110.0／Release／R3 |
| GUI / Service / Diagnostics | `artifacts/usable-release-1.0.110-offline/{GUI,Service,Diagnostics}`；当前安装`C:/Program Files/Jiaolong Control Center` |
| 全部组件SHA-256 | [R3报告](2026-10-27-usable-release-r3.md)，包内`SHA256SUMS.txt` |
| 集成生命周期 | 原R3默认两轮+Keep三会话；离线版两轮；实际安装版两轮及Windows服务停止/重启通过 |

本分级限定本机及已证范围。R4继续原71项库存与兼容性/优化；本版交付不等于原71项完成。
