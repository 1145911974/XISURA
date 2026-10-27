# XISURA 当前状态

更新2026-10-27。唯一当前状态；[目标](FEATURE-COMPLETION-GOAL.md)、[固定台账](FEATURE-COMPLETION-LEDGER.csv)。用户最新决定优先。

**79项：完成28，剩余51，35.4%；本目标新增3/54（EXT-07、SET-06、CPU-21），本轮新增0。** 配置保存、Partial、声明不支持不计完成。EC/OEM/NVIDIA协议及禁测依赖未闭合，全量ETA暂不可可靠估算。

**本机113已安装验收；GitHub正式[1.0.113](https://github.com/1145911974/XISURA/releases/tag/v1.0.113)已发布，五项资产大小/SHA经服务端核验；冻结源码`daa4ea5b6254e8f120cafad19592319d27852a30`，MSI SHA256 `22C4596E13055242153FBCF95A726BF1E3186A78EC1BBCDB8DC142BC853B231A`。旧113候选及重复MSI清理被自动审批以`blocked by policy`拒绝，暂留C盘；原恢复证据保留。** Release自包含GUI/Service/Diagnostics，1369/1369软件测试通过、零跳过、构建源SHA前后一致。MSI退出0、无需重启、用户文件及13组控制保持；安装后两轮副屏启动/托盘/正常退出重开、服务停止/重启通过，当前Automatic/Running。MSI SHA256：22C4596E13055242153FBCF95A726BF1E3186A78EC1BBCDB8DC142BC853B231A。

113移除启动/激活强制微软拼音和释放豆包的整个主动层。后续测试GUI固定`--accept-secondary`，主窗/托盘菜单使用副屏；后台助手Hidden，不改输入法、显示布局或鼠标。不要启动旧112GUI触发输入法。

托盘故障：run4/延迟Hide候选的dump/SOS定位ShowNearCursor定位调用，c000027b/80070578。主窗SW_HIDE及延迟Hide均撤回。当前菜单重开先Show(false)恢复可见再定位，首次先定位副屏；窄验两轮通过。run5可见主窗观察失败并准确恢复；助手保留原断言，增加3秒有界观察。正式配套run6三轮Keep退出/重开/立即准确恢复通过，method20为0→1→保留重开1→恢复0，method21始终51；配置/13控制恢复、Completed/fields0、候选exit0、原服务恢复。原始失败与恢复证据保留。

剩余共同根因：
- EC：1060两传输当前读回失败；第三方Read_EC_EX实际写0并返回0。V35 method21读51真实，单位/模式契约未知，本轮未写21；不把强冷开关外推为固定RPM/曲线效果。
- CPU：可靠PPT/TDC/EDC/MP1；STAPM/SlowPPT布局、OEM复合SPL当前值/单位/恢复契约缺失。
- GPU：锁频缺原外部min/max getter，当前频率/范围/私有Lock表不能代替；任意电压未实现，功率只读。

边界：MRID6-23／7745HX／RTX4070 Laptop／V35、EC0.35。每个数值点立即准确恢复并独立核验，Keep不豁免；保留用户主动状态。禁游戏/跑分/轨迹/卡顿复现/显示或MUX切换/睡眠/重启/EC盲写/驱动安装；不启动109或不兼容新证据的110服务。所有输出C盘；不reset/clean。正式可用版本已有安装本机及上传GitHub持续授权。完全退出停止Follow/AUTO；保留静态值不代表动态控制继续运行。

证据根：C:/Users/Administrator/AppData/Local/Temp/XisuraUsableRelease-20261026/；最终构建completion113/final-20261027-170255-196、feature-completion-r1/keep-release113-run6、install-publish-113。详见[113说明](../verification/2026-10-27-release113.md)、[本轮证据](../verification/2026-10-27-feature-completion-r1.md)。权威journal= C:/ProgramData/JiaolongControlCenter/control-journal-110。

CPU-21已闭合为B级配置能力：已安装113的index1至7分别−50→−49→立即恢复−50，每点生产Confirmed、稀疏单核计划、独立八核读回，恢复先于退出；13组受控配置及用户配置哈希一致。index0沿用2026-10-24同V35原件；PawnIO、CpuPerCore及高级CPU XAML与113冻结输入同SHA，不把不同的UI/恢复源码说成完全一致，不宣称113同轮八核实测或负载稳定性。最终Completed、fields0/pending0/unresolved0、GUI0、113服务Auto/Running。见[CPU-21证据](../verification/2026-10-27-cpu21-per-core-closure.md)；本轮仅验收，无新构建/安装包。

本轮GPU-07：113副屏UI显示当前111/最高140W，独立SMI109.87/140W、服务异时109.18/140W；动态执行值不冒称同步精确相等。完整查询可用，未加字段回退。正常退出及13组受控配置对账通过，原始GPU全快照逐字比较false不外推为全部相同；详见[只读复核](../verification/2026-10-27-gpu07-readonly-display.md)。固定请求功率上限及恢复仍缺，GPU-07保持Open。

本轮SET-02：首轮助手路径比较失败，配置未动且主会正常退出；第二轮UIA缩放后失去唯一可见主窗，目标1280×800而退出保存848×531。只读证实helper系统DPI144、副屏96，改初始化前PMv2的run3退出保存准确1280×800，但可见UIA查询仍失败，未完成记忆重开。两轮主会均按同一Completed会话/GUI0/非目标字段一致/独占SHA检查，恢复偏好原字节（SHA256 0AEA2036B1278756E5AC797E6FCD3E018EE03B02B6DF1EF07D0025DB49CAC74E），独立13组受控配置及最终权威检查Passed；详见[尝试与恢复](../verification/2026-10-27-set02-window-size-attempt.md)。SET-02保持Open，停止重复此Transform路径；手动拖拽、跨DPI未验。原始证据feature-completion-r3-gpu07、feature-completion-r3-settings保留在C盘；本轮无产品源码改动及新构建。

下一步：SET-02尺寸倍率已定位助手DPI，剩UIA可见性/枚举，先静态查明再有界修正验收路径；并推进EC风扇、AMD/OEM、NVIDIA最小实现缺口。缺协议和禁测部分保留未完成，不重复已过数值点。
