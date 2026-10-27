# XISURA 当前状态

更新2026-10-27。唯一当前状态；[目标](FEATURE-COMPLETION-GOAL.md)、[固定台账](FEATURE-COMPLETION-LEDGER.csv)。用户最新决定优先。

**79项：完成27，剩余52，34.2%；本目标新增2/54（EXT-07、SET-06）。** 配置保存、Partial、声明不支持不计完成。EC/OEM/NVIDIA协议及禁测依赖未闭合，全量ETA暂不可可靠估算。

**本机113已安装验收；GitHub113交付进行中，当前公开仍111。** Release自包含GUI/Service/Diagnostics，1369/1369软件测试通过、零跳过、构建源SHA前后一致。MSI退出0、无需重启、用户文件及13组控制保持；安装后两轮副屏启动/托盘/正常退出重开、服务停止/重启通过，当前Automatic/Running。MSI SHA256：22C4596E13055242153FBCF95A726BF1E3186A78EC1BBCDB8DC142BC853B231A。

113移除启动/激活强制微软拼音和释放豆包的整个主动层。后续测试GUI固定`--accept-secondary`，主窗/托盘菜单使用副屏；后台助手Hidden，不改输入法、显示布局或鼠标。不要启动旧112GUI触发输入法。

托盘故障：run4/延迟Hide候选的dump/SOS定位ShowNearCursor定位调用，c000027b/80070578。主窗SW_HIDE及延迟Hide均撤回。当前菜单重开先Show(false)恢复可见再定位，首次先定位副屏；窄验两轮通过。run5可见主窗观察失败并准确恢复；助手保留原断言，增加3秒有界观察。正式配套run6三轮Keep退出/重开/立即准确恢复通过，method20为0→1→保留重开1→恢复0，method21始终51；配置/13控制恢复、Completed/fields0、候选exit0、原服务恢复。原始失败与恢复证据保留。

剩余共同根因：
- EC：1060两传输当前读回失败；第三方Read_EC_EX实际写0并返回0。V35 method21读51真实，单位/模式契约未知，本轮未写21；不把强冷开关外推为固定RPM/曲线效果。
- CPU：可靠PPT/TDC/EDC/MP1；STAPM/SlowPPT布局、OEM复合SPL当前值/单位/恢复契约缺失。
- GPU：锁频缺原外部min/max getter，当前频率/范围/私有Lock表不能代替；任意电压未实现，功率只读。

边界：MRID6-23／7745HX／RTX4070 Laptop／V35、EC0.35。每个数值点立即准确恢复并独立核验，Keep不豁免；保留用户主动状态。禁游戏/跑分/轨迹/卡顿复现/显示或MUX切换/睡眠/重启/EC盲写/驱动安装；不启动109或不兼容新证据的110服务。所有输出C盘；不reset/clean。正式可用版本已有安装本机及上传GitHub持续授权。完全退出停止Follow/AUTO；保留静态值不代表动态控制继续运行。

证据根：C:/Users/Administrator/AppData/Local/Temp/XisuraUsableRelease-20261026/；最终构建completion113/final-20261027-170255-196、feature-completion-r1/keep-release113-run6、install-publish-113。详见[113说明](../verification/2026-10-27-release113.md)、[本轮证据](../verification/2026-10-27-feature-completion-r1.md)。权威journal= C:/ProgramData/JiaolongControlCenter/control-journal-110。

下一步：冻结源码并核验五项GitHub资产，交付113；随后按EC风扇、AMD/OEM、NVIDIA的最小缺口逐项闭合，不重复已有调查。