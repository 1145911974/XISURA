# XISURA 当前状态

更新2026-10-28。唯一当前状态；[目标](FEATURE-COMPLETION-GOAL.md)、[固定台账](FEATURE-COMPLETION-LEDGER.csv)。用户最新决定优先。

**79项：完成29，剩余50，36.7%；本目标新增4/54（EXT-07、SET-06、CPU-21、HOME-09），本轮新增完成0。** 配置保存、Partial、声明不支持不计完成。全量ETA受EC/OEM/NVIDIA协议及禁测/实机验收依赖限制，暂不能可靠估算。

**正式113已安装且GitHub[1.0.113](https://github.com/1145911974/XISURA/releases/tag/v1.0.113)已发布**，五项资产SHA/大小核验通过；冻结源码`daa4ea5b6254e8f120cafad19592319d27852a30`，MSI SHA `22C4596E13055242153FBCF95A726BF1E3186A78EC1BBCDB8DC142BC853B231A`。113冻结版本1369/1369、零跳过；Release自包含GUI/Service/Diagnostics，安装及副屏启动/托盘/退出重开/Keep验收已过。当前安装GUI/Service SHA本轮复核相同，服务Automatic/Running。见[113交付](../verification/2026-10-27-release113.md)。

113已移除整个强制微软拼音及释放豆包主动层。所有GUI验收固定`--accept-secondary`，主窗/托盘均副屏；后台助手Hidden。不主动切换输入法、鼠标或显示布局。

**本轮完成SET-02新鲜HWND诊断及铺满尺寸DPI缓存的单文件修复，无新正式安装/发布。** 已安装113副屏显示→关闭隐藏→托盘重开保持同HWND/1573×983/DPI96，原生与UIA可见性一致。修复铺满缓存旧DPI像素导致跨DPI误存尺寸：改存DIP、保存直接使用缓存、还原先移动再按目标DPI换算。Release构建成功，定向4/4、GUI回归583/583零跳过；候选运行未执行，SET-02仍Open。见[窗口证据与限制](../verification/2026-10-28-set02-hwnd-fit-dpi.md)。

AUTO-11沿用r8服务585/585及R9自然后台Explorer规则局部证据：Gaming→条件5.997秒→Office2提交，最终SubmittedUnverified/CurrentTarget=null；原脚本恢复后ZIP解析错误Accepted=false保留，独立只读核验Passed。RequireActivity=true、应用启停、跨native及硬件页派发恢复仍未验，AUTO-11仍Open。见[自然后台证据](../verification/2026-10-28-auto11-natural-background.md)。过程采样及尺寸补丁使用同一[草稿PR #1](https://github.com/1145911974/XISURA/pull/1)评审；尺寸增量的远端SHA/同步结果见本轮独立GitHub核验，仍未形成正式版本。

最终权威journal=`C:/ProgramData/JiaolongControlCenter/control-journal-110`：会话`aa57a973-7406-4a30-aad9-7ec898136940` Completed、5恢复步骤Verified、fields0/pending0/unresolved0、GUI0；113服务Running。2026-10-28 00:24:46 +08独立捕获Balanced，与本轮新鲜试前13组/6OEM一致，无Resize/偏好修改，不沿用旧快照写回。偏好SHA `0AEA2036B1278756E5AC797E6FCD3E018EE03B02B6DF1EF07D0025DB49CAC74E`，自动化SHA `41DCF1D5682DE181CB207931CBA4BEAA06DA2EA3057DD779157E84CDA0CC028A`与试前一致；AUTO/Follow/Keep全off。已安装GUI/Service SHA复核仍为正式113。

剩余共同根因：EC1060两传输当前读回失败，第三方Read_EC_EX会写0并返回0；V35 method21读51但单位/模式未知，不能外推强冷20为固定RPM/曲线。CPU可靠PPT/TDC/EDC/MP1，STAPM/SlowPPT布局及OEM复合SPL单位/当前值/逆向恢复缺失。GPU锁频缺原外部min/max getter、任意电压未实现、功率只读；[GPU-07](../verification/2026-10-27-gpu07-readonly-display.md)仍Open。

已闭合：CPU-21为B级单核配置读回/恢复，index1–7为113逐点往返，index0继承10-24同V35证据；不宣称113同轮八核或负载稳定性。HOME-09继承10-16用户实效确认并关联113源码/安装SHA，非本轮重新阻断按键。详见对应[CPU-21](../verification/2026-10-27-cpu21-per-core-closure.md)、[HOME-09](../verification/2026-10-27-home09-installed-association.md)。

SET-02仍Open：普通HWND/托盘生命周期已证明，旧Transform缩放后UIA缺失仍未定因；不重复Resize。铺满DPI缓存源码已修，候选GUI DLL SHA `54DA9EBC0596B43D13C2023FD0E202319D0FAEE87E59D076434B4A6A10D816B0`，未运行/安装。Remember保存重开、关闭开关、手动拖拽及跨DPI仍待；保留[旧失败及恢复](../verification/2026-10-27-set02-window-size-attempt.md)。

边界：MRID6-23／7745HX／RTX4070 Laptop／V35 BIOS、EC0.35。每个数值点立即恢复新鲜准确试前值并独立核验，Keep不豁免；保留用户主动状态，不按旧65W盲写。禁游戏/跑分/轨迹/卡顿复现/显示或MUX切换/睡眠/自行重启/EC盲写/驱动安装；不启动109或不兼容110服务。完全退出停止Follow/AUTO，保留静态值不代表动态运行。正式可用版本已有安装本机及GitHub上传持续授权。

全部输出C盘、不reset/clean。自动审批本轮以blocked by policy拒绝测试bin/Release清理及生成/启动候选GUI助手，两条命令整体未执行、未绕过；测试输出269MiB暂保留，C盘约535MiB余量。旧113候选/重复MSI、CPU/GPU探针清理拒绝继续有效。证据根=`C:/Users/Administrator/AppData/Local/Temp/XisuraUsableRelease-20261026/`，本轮`feature-completion-r10-settings`；原始失败/恢复证据不删，未复制完整发布树。

下一步：补齐SET-02候选实际运行/记忆重开及AUTO-11自然活动/应用启停/跨native恢复；被自动审批拒绝的动作不重试绕过。可继续独立软件缺陷与协议证据工作，不重复已证明的HWND生命周期或后台同模式规则。必要运行验收后再冻结正式版安装/上传。所有应用验证副屏，不主动切换输入法。
