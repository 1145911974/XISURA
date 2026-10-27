# XISURA 当前状态

更新2026-10-27。唯一当前状态；[目标](FEATURE-COMPLETION-GOAL.md)、[固定台账](FEATURE-COMPLETION-LEDGER.csv)。用户最新决定优先。

**79项：完成29，剩余50，36.7%；本目标新增4/54（EXT-07、SET-06、CPU-21、HOME-09），本轮新增完成0。** 配置保存、Partial、声明不支持不计完成。全量ETA受EC/OEM/NVIDIA协议及禁测/实机验收依赖限制，暂不能可靠估算。

**正式113已安装且GitHub[1.0.113](https://github.com/1145911974/XISURA/releases/tag/v1.0.113)已发布**，五项资产SHA/大小核验通过；冻结源码`daa4ea5b6254e8f120cafad19592319d27852a30`，MSI SHA `22C4596E13055242153FBCF95A726BF1E3186A78EC1BBCDB8DC142BC853B231A`。113冻结版本1369/1369、零跳过；Release自包含GUI/Service/Diagnostics，安装及副屏启动/托盘/退出重开/Keep验收已过。当前安装GUI/Service SHA本轮复核相同，服务Automatic/Running。见[113交付](../verification/2026-10-27-release113.md)。

113已移除整个强制微软拼音及释放豆包主动层。所有GUI验收固定`--accept-secondary`，主窗/托盘均副屏；后台助手Hidden。不主动切换输入法、鼠标或显示布局。

**本轮为AUTO-11同模式无写入及Worker回归修复，无新正式安装/发布。** 空硬件页同模式请求使用两次≤1秒且raw一致的真实读数，不再重写OEM模式，不新增ownership，保持SubmittedUnverified。Worker不再因此拦住随后CPU/GPU活动升档，同时保留此前真正未知写围栏。CPU/GPU进程输入、身份/scope隔离及GPU后台单flight沿用前轮补丁。评审沿用[草稿PR #1](https://github.com/1145911974/XISURA/pull/1)。

本轮完整回归曾584例中2项活动Worker失败（预期Turbo、实际Gaming）；按根因修复后6项定向及最终提升服务**585/585、零跳过**。真实候选Service为框架依赖构建，与113 GUI的Contracts SHA一致；副屏只读生命周期**2/2**通过，在线/退出/最终13组控制与6个OEM字段一致，14份保护配置SHA不变，候选退出0。首次路径比较脚本失败及正常收尾证据保留；未通过的run1不计验收。未启用真实自动调度，AUTO-11仍Open。见[同模式证据与限制](../verification/2026-10-27-auto11-same-mode-retention.md)、[GPU补丁](../verification/2026-10-27-auto11-process-gpu-patch.md)。

最终权威journal=`C:/ProgramData/JiaolongControlCenter/control-journal-110`：会话`d85246bc-4279-4860-ad28-0a21c9383c40` Completed、fields0/pending0/unresolved0、GUI0；113服务恢复Running。21:59:21 +08独立捕获Balanced/PPT65W，保留当前状态，不沿用旧Turbo120。偏好SHA `0AEA2036B1278756E5AC797E6FCD3E018EE03B02B6DF1EF07D0025DB49CAC74E`，自动化文件SHA `41DCF1D5682DE181CB207931CBA4BEAA06DA2EA3057DD779157E84CDA0CC028A`与试前一致。本轮只读GUI生命周期，无控制数值测试写入或配置修改。

剩余共同根因：EC1060两传输当前读回失败，第三方Read_EC_EX会写0并返回0；V35 method21读51但单位/模式未知，不能外推强冷20为固定RPM/曲线。CPU可靠PPT/TDC/EDC/MP1，STAPM/SlowPPT布局及OEM复合SPL单位/当前值/逆向恢复缺失。GPU锁频缺原外部min/max getter、任意电压未实现、功率只读；[GPU-07](../verification/2026-10-27-gpu07-readonly-display.md)仍Open。

已闭合：CPU-21为B级单核配置读回/恢复，index1–7为113逐点往返，index0继承10-24同V35证据；不宣称113同轮八核或负载稳定性。HOME-09继承10-16用户实效确认并关联113源码/安装SHA，非本轮重新阻断按键。详见对应[CPU-21](../verification/2026-10-27-cpu21-per-core-closure.md)、[HOME-09](../verification/2026-10-27-home09-installed-association.md)。

SET-02仍Open：助手DPI倍率已定位，PMv2后准确保存1280×800；UIA可见窗查询仍失败，未验记忆重开/手动拖拽/跨DPI。准确原字节恢复及独立13组对账已过；不重复Resize路径。见[失败及恢复](../verification/2026-10-27-set02-window-size-attempt.md)。

边界：MRID6-23／7745HX／RTX4070 Laptop／V35 BIOS、EC0.35。每个数值点立即恢复新鲜准确试前值并独立核验，Keep不豁免；保留用户主动状态，不按旧65W盲写。禁游戏/跑分/轨迹/卡顿复现/显示或MUX切换/睡眠/自行重启/EC盲写/驱动安装；不启动109或不兼容110服务。完全退出停止Follow/AUTO，保留静态值不代表动态运行。正式可用版本已有安装本机及GitHub上传持续授权。

全部输出C盘、不reset/clean。旧113候选/重复MSI、CPU/GPU探针清理曾被自动审批blocked by policy拒绝、未执行、不重试/绕过。原恢复证据不删。证据根=`C:/Users/Administrator/AppData/Local/Temp/XisuraUsableRelease-20261026/`，本轮`feature-completion-r8-auto-same-mode`及`feature-completion-r8-candidate-readonly-run1/run2`；原113构建/安装/发布保留。C盘约1GB余量，未复制另一份完整发布树。

下一步：AUTO-11自然应用活动/启动停止/目标派发/立即准确恢复；补丁冻结及候选集中验收后再形成正式版本并安装/上传。SET-02仅用新鲜绑定HWND做只读分层诊断；协议/禁测项保留未完成，不重复已过数值点。所有应用验证副屏，不主动切换输入法。
