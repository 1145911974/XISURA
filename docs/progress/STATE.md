# XISURA 当前状态

更新2026-10-28。唯一当前状态；[目标](FEATURE-COMPLETION-GOAL.md)、[固定台账](FEATURE-COMPLETION-LEDGER.csv)。用户最新决定优先。

**79项：完成31，剩余48，39.2%；本目标新增6/54（EXT-07、SET-06、CPU-21、HOME-09、HOME-06、HOME-07），本轮新增完成0。** 配置保存、Partial、声明不支持不计完成。全量ETA受EC/OEM/NVIDIA协议及禁测/实机验收依赖限制，暂不能可靠估算。

**本轮修复SET-02铺满后手动缩放误存旧尺寸，仍Open。** 原生ENTER/SIZING/EXIT捕获并比较实际逻辑尺寸，真实改变才清铺满标志，取消/纯移动保留缓存；新增4方法，Release及定向8/8零跳过、Sol窄审通过。未启动候选/操作硬件/安装/发布。见[补丁、TRX及边界](../verification/2026-10-28-set02-fit-manual-sizing.md)。

上轮HOME-06/07闭合：用户确认正式113触控板开关和Fn Lock“两项都实际用过，均正常”；结合既有CIM读回、立即原值恢复、反向GUI及113安装生命周期证据，限本机基础开关，不外推所有Fn组合键或机型。见[用户验收](../verification/2026-10-28-home06-home07-user-acceptance.md)。

候选运行障碍仍在：GUI验收助手生成/启动被自动审批拒绝；历史三轮审计见`feature-completion-r10-settings/blocker-audit-20261028.json`。不重试被拒动作、不重复构建/同源调查。SET-02继续依赖候选运行审批环境变化，EC/OEM/NVIDIA支线需新的设备协议/恢复证据；用户本次反馈未解除这些障碍。

**正式113已安装且GitHub[1.0.113](https://github.com/1145911974/XISURA/releases/tag/v1.0.113)已发布**，五项资产SHA/大小核验通过；冻结源码`daa4ea5b6254e8f120cafad19592319d27852a30`，MSI SHA `22C4596E13055242153FBCF95A726BF1E3186A78EC1BBCDB8DC142BC853B231A`。113冻结版本1369/1369、零跳过；Release自包含GUI/Service/Diagnostics，安装及副屏启动/托盘/退出重开/Keep验收已过。当前安装GUI/Service SHA本轮复核相同，服务Automatic/Running。见[113交付](../verification/2026-10-27-release113.md)。

113已移除整个强制微软拼音及释放豆包主动层。所有GUI验收固定`--accept-secondary`，主窗/托盘均副屏；后台助手Hidden。不主动切换输入法、鼠标或显示布局。

**既有SET-02新鲜HWND诊断及铺满尺寸DPI缓存的单文件修复，无新正式安装/发布。** 已安装113副屏显示→关闭隐藏→托盘重开保持同HWND/1573×983/DPI96，原生与UIA可见性一致。修复铺满缓存旧DPI像素导致跨DPI误存尺寸：改存DIP、保存直接使用缓存、还原先移动再按目标DPI换算。Release构建成功，定向4/4、GUI回归583/583零跳过；候选运行未执行，SET-02仍Open。见[窗口证据与限制](../verification/2026-10-28-set02-hwnd-fit-dpi.md)。

AUTO-11沿用r8服务585/585及R9自然后台Explorer规则局部证据：Gaming→条件5.997秒→Office2提交，最终SubmittedUnverified/CurrentTarget=null；原脚本恢复后ZIP解析错误Accepted=false保留，独立只读核验Passed。RequireActivity=true、应用启停、跨native及硬件页派发恢复仍未验，AUTO-11仍Open。见[自然后台证据](../verification/2026-10-28-auto11-natural-background.md)。过程采样及尺寸补丁使用同一[草稿PR #1](https://github.com/1145911974/XISURA/pull/1)评审；尺寸增量`cc32607aa5c9c1f605731f60ce1e3ce822a713df`已同步，6文件blob/SHA、parent、draft及main/113标签不变均经独立GET核验；未形成正式版本。

最近已登记硬件边界（非本轮实时捕获）：权威journal=`C:/ProgramData/JiaolongControlCenter/control-journal-110`，会话`aa57a973-7406-4a30-aad9-7ec898136940` Completed、5恢复步骤Verified、fields0/pending0/unresolved0、GUI0，113服务Running。2026-10-28 00:24:46 +08捕获Balanced、13组/6OEM与当轮试前一致；偏好SHA `0AEA2036B1278756E5AC797E6FCD3E018EE03B02B6DF1EF07D0025DB49CAC74E`，自动化SHA `41DCF1D5682DE181CB207931CBA4BEAA06DA2EA3057DD779157E84CDA0CC028A`；AUTO/Follow/Keep全off。不沿用该快照写回；原Temp证据现缺失，下一硬件会话须先核权威journal及新鲜基线。

剩余共同根因：EC1060两传输当前读回失败，第三方Read_EC_EX会写0并返回0；V35 method21读51但单位/模式未知，不能外推强冷20为固定RPM/曲线。CPU可靠PPT/TDC/EDC/MP1，STAPM/SlowPPT布局及OEM复合SPL单位/当前值/逆向恢复缺失。GPU锁频缺原外部min/max getter、任意电压未实现、功率只读；[GPU-07](../verification/2026-10-27-gpu07-readonly-display.md)仍Open。

已闭合：CPU-21为B级单核配置读回/恢复，index1–7为113逐点往返，index0继承10-24同V35证据；不宣称113同轮八核或负载稳定性。HOME-09继承10-16用户实效确认并关联113源码/安装SHA，非本轮重新阻断按键。详见对应[CPU-21](../verification/2026-10-27-cpu21-per-core-closure.md)、[HOME-09](../verification/2026-10-27-home09-installed-association.md)。

SET-02仍Open：普通HWND/托盘生命周期已有证据；旧Transform缩放后UIA缺失未定因，不重复Resize。铺满DPI缓存及本轮手动缩放状态均已修；最新候选GUI DLL SHA `2705E9BA0996DA49A3BDF6451E9C5BDF44B03642877D56A9801B2533A1238C3B`，FileVersion仍113但非正式安装二进制，未运行/安装。Remember保存重开、关闭开关、手动拖拽及跨DPI仍待；[旧失败及恢复报告](../verification/2026-10-27-set02-window-size-attempt.md)保留，不宣称其Temp原件仍可读。

边界：MRID6-23／7745HX／RTX4070 Laptop／V35 BIOS、EC0.35。每个数值点立即恢复新鲜准确试前值并独立核验，Keep不豁免；保留用户主动状态，不按旧65W盲写。禁游戏/跑分/轨迹/卡顿复现/显示或MUX切换/睡眠/自行重启/EC盲写/驱动安装；不启动109或不兼容110服务。完全退出停止Follow/AUTO，保留静态值不代表动态运行。正式可用版本已有安装本机及GitHub上传持续授权。

并行清理：用户确认另一个工具仍在清理，要求本对话同步检查/清理、不等待。原Temp证据根及测试obj已缺失；未删除或伪造恢复记录。exit0无TRX空跑已标失败，C盘还原后实际8/8原件留存`docs/verification/retained/2026-10-28-fit-sizing/`。已审查22个可重建输出/npm缓存目录约1.76GiB；删除命令在启动前被自动审批整体以`blocked by policy`拒绝，删除0/释放0，未绕过。完整磁盘盘点仅保留本地维护报告，不上传用户磁盘索引。不把其他工具释放空间记为本轮成果，保留源码、候选、正式交付及恢复证据。

下一步按可用权限推进，不以另一个工具清理为等待条件；被拒删除和候选启动不重试绕过。新硬件会话先核源码/证据/journal完整性及新鲜基线，再补齐SET-02实际记忆重开及AUTO-11活动/应用启停/跨native恢复。不重复已证明的HWND或同模式规则；必要运行验收后再冻结正式版安装/上传。所有应用验证固定副屏，不主动切换输入法。
