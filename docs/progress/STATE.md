# XISURA 当前状态

更新2026-10-27。唯一当前状态；[目标](FEATURE-COMPLETION-GOAL.md)、[固定台账](FEATURE-COMPLETION-LEDGER.csv)。用户最新决定优先。

**79项：完成29，剩余50，36.7%；本目标新增4/54（EXT-07、SET-06、CPU-21、HOME-09），本轮新增完成0。** 配置保存、Partial、声明不支持不计完成。全量ETA受EC/OEM/NVIDIA协议及禁测/实机验收依赖限制，暂不能可靠估算。

**正式113已安装且GitHub[1.0.113](https://github.com/1145911974/XISURA/releases/tag/v1.0.113)已发布**，五项资产SHA/大小核验通过；冻结源码`daa4ea5b6254e8f120cafad19592319d27852a30`，MSI SHA `22C4596E13055242153FBCF95A726BF1E3186A78EC1BBCDB8DC142BC853B231A`。113冻结版本1369/1369、零跳过；Release自包含GUI/Service/Diagnostics，安装及副屏启动/托盘/退出重开/Keep验收已过。当前安装GUI/Service SHA本轮复核相同，服务Automatic/Running。见[113交付](../verification/2026-10-27-release113.md)。

113已移除整个强制微软拼音及释放豆包主动层。所有GUI验收固定`--accept-secondary`，主窗/托盘均副屏；后台助手Hidden。不主动切换输入法、鼠标或显示布局。

**本轮是AUTO-11进程GPU增量，尚无新正式构建/安装/发布。** CPU/GPU均按同句柄身份及运行时会话/revision/epoch隔离，给RequireActivity规则填独立进程输入。GPU取同归属应用在任意GPU上的最忙单引擎，不求和、不用全局值代替；首次/缺失/身份变化为unknown。完整GPU采集在后台单flight，WMI卡住也不等待、不占退出门；停用/过期/手动优先清缓存，旧任务结果失效。评审沿用[草稿PR #1](https://github.com/1145911974/XISURA/pull/1)。

GPU-only接线前实际失败（预期Turbo，实际Gaming），接线后6项定向通过；最终提升服务回归**581/581、零跳过**。原CPU轮非提升10例pipe ACL失败保留，不放宽授权。本轮同生产源码NativeProbe读取已有Explorer，约1秒节奏9个连续GPU区间可读、最大Read约10.37ms；首样本/错误归属/reset为unknown。加入0.9秒计数区间用例，避免1秒tick抖动清空活动。未运行真实硬件自动调度，AUTO-11仍Open。见[GPU补丁与限制](../verification/2026-10-27-auto11-process-gpu-patch.md)、[原CPU证据](../verification/2026-10-27-auto11-process-cpu-patch.md)。

最终权威journal=`C:/ProgramData/JiaolongControlCenter/control-journal-110`：同会话`6109fcdb-400e-4439-a89a-225c8f28ef9b` Completed、fields0/pending0/unresolved0、GUI0；偏好SHA `0AEA2036B1278756E5AC797E6FCD3E018EE03B02B6DF1EF07D0025DB49CAC74E`，自动化文件SHA `41DCF1D5682DE181CB207931CBA4BEAA06DA2EA3057DD779157E84CDA0CC028A`与试前一致。本轮无GUI启动、硬件写入或用户配置变更。

剩余共同根因：EC1060两传输当前读回失败，第三方Read_EC_EX会写0并返回0；V35 method21读51但单位/模式未知，不能外推强冷20为固定RPM/曲线。CPU可靠PPT/TDC/EDC/MP1，STAPM/SlowPPT布局及OEM复合SPL单位/当前值/逆向恢复缺失。GPU锁频缺原外部min/max getter、任意电压未实现、功率只读；[GPU-07](../verification/2026-10-27-gpu07-readonly-display.md)仍Open。

已闭合：CPU-21为B级单核配置读回/恢复，index1–7为113逐点往返，index0继承10-24同V35证据；不宣称113同轮八核或负载稳定性。HOME-09继承10-16用户实效确认并关联113源码/安装SHA，非本轮重新阻断按键。详见对应[CPU-21](../verification/2026-10-27-cpu21-per-core-closure.md)、[HOME-09](../verification/2026-10-27-home09-installed-association.md)。

SET-02仍Open：助手DPI倍率已定位，PMv2后准确保存1280×800；UIA可见窗查询仍失败，未验记忆重开/手动拖拽/跨DPI。准确原字节恢复及独立13组对账已过；不重复Resize路径。见[失败及恢复](../verification/2026-10-27-set02-window-size-attempt.md)。

边界：MRID6-23／7745HX／RTX4070 Laptop／V35 BIOS、EC0.35。每个数值点立即恢复新鲜准确试前值并独立核验，Keep不豁免；保留用户主动状态，不按旧65W盲写。禁游戏/跑分/轨迹/卡顿复现/显示或MUX切换/睡眠/自行重启/EC盲写/驱动安装；不启动109或不兼容110服务。完全退出停止Follow/AUTO，保留静态值不代表动态运行。正式可用版本已有安装本机及GitHub上传持续授权。

全部输出C盘、不reset/clean。旧113候选/重复MSI、CPU轮探针466185字节及本轮GPU探针1083957字节的清理均被自动审批blocked by policy拒绝、未执行、不重试/绕过。原恢复证据不删。证据根=`C:/Users/Administrator/AppData/Local/Temp/XisuraUsableRelease-20261026/`，本轮`feature-completion-r7-gpu-activity`、前轮`feature-completion-r6-auto-activity`；原113构建/安装/发布保留。

下一步：AUTO-11自然应用活动/启动停止/目标派发/立即准确恢复；补丁冻结及候选集中验收后再形成正式版本并安装/上传。SET-02仅用新鲜绑定HWND做只读分层诊断；协议/禁测项保留未完成，不重复已过数值点。所有应用验证副屏，不主动切换输入法。
