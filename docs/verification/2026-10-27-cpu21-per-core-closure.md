# CPU-21：逐核CO配置闭合

总79项，完成28，剩余51，35.4%；本轮新增CPU-21一项。本目标累计新增3/54。全量ETA仍受EC/OEM/NVIDIA协议和禁测依赖限制，暂不可可靠估算。

## 范围与结论

B级配置能力：真实CO整数计数、逐核写入、独立读回、准确恢复。步数不是mV；未运行游戏、负载、轨迹、睡眠或重启，不证明电压变化量、性能收益或长期稳定性。设备MRID6-23／7745HX／RTX4070 Laptop／V35 BIOS、EC0.35。

- 已安装113：index1至7逐个独立会话，原值均为新鲜−50；仅目标核改为−49，其余七核不变；每点立即从同一UI恢复−50并独立读取八核，随后才退出。未使用默认退出恢复兜底。
- index0：沿用2026-10-24同V35原始PointA，实际−50→−49→−50，完整八核独立读回；PointB=false不计通过。跨版本沿用，不宣称113同轮八核实测。
- 模式选择仅改变编辑方式；每点恢复原“全核统一”编辑方式。所有测试窗口在副屏，无输入法选择/释放操作。
- 本轮无产品源码修改、构建或新版本；继续使用已安装并发布的1.0.113.0。

## 本轮实测

证据根：`C:/Users/Administrator/AppData/Local/Temp/XisuraUsableRelease-20261026/feature-completion-r2-cpu21/`。对应目录`coreN-installed113-run1/`。

| index | 独立目标读回 | 独立准确恢复 | 结果 |
|---|---|---|---|
| 1 | 18:00:04 | 18:00:14 | Passed |
| 2 | 18:01:32 | 18:01:42 | Passed |
| 3 | 18:02:18 | 18:02:27 | Passed |
| 4 | 18:03:03 | 18:03:12 | Passed |
| 5 | 18:03:48 | 18:03:57 | Passed |
| 6 | 18:04:33 | 18:04:42 | Passed |
| 7 | 18:05:19 | 18:05:28 | Passed |

写前检查新会话完整八核baseline等于鲜读值；每个CPU batch只有所选核，其他数值和nativeMode均为空。写后要求同session、Confirmed、完整BaselineDetails/LastWrittenDetails、pending/unresolved为空、编辑器重新可用，再独立读取SMU。读取器显式禁写；不以成功回包、草稿或输入框回显作硬件证据。

恢复前核对本会话归属及新鲜八核表等于本点target，外变拒绝覆盖；恢复在try/finally内立即执行。UI新写的“读→提交”检查不是原子CAS，不宣称原子保护。异常仅可由已证明同基线的认证默认退出兜底，本轮未触发。实际恢复均先于Completed。

每点`result.json`的Passed/PointRestored/ConfigurationUnchanged均true；`target/restored-{journal,independent}.json`、`comparison.json`、`point-completed.json`保留。13组受控配置一致，用户preferences、automation、last-applied及四页preset原SHA保持；GPU原始快照有非受控观察差异，不能说完整原始GPU快照逐字相同。`root-evidence-verification.json`逐点核验；`serial-result.json`Passed。最终`final-authority.json`确认Completed、fields0/pending0/unresolved0、GUI0、113服务Auto/Running。

前置只读与选择预演分别为`inspect-installed113-run1/`、`core1-preview-run1/`，均Passed且无数值写入。所有脚本、原始读回及恢复证据留C盘供复核；无新增构建中间目录。

## index0证据继承

原件：`C:/Users/Administrator/AppData/Local/Temp/XisuraCoPerCoreGui-20261024/`的`independent-before.json`、`session-active.json`、`point-a-{target,restored}-{independent,journal}.json`、`root-verification.json`及输入/产品manifest。身份V35/7745HX；同session稀疏`{0:-49}`后`{0:-50}`、Confirmed，独立目标02:58:35/恢复02:58:39，其余七核−50，完整后验通过。

历史输入manifest与113的source-lock/冻结source-inputs中以下SHA256一致：

| 文件 | SHA256 |
|---|---|
| PawnIoCurveOptimizerTransport.cs | C048411487AF5E09EDD7EB82CF2C7D48E235EFF387217639CCA9DCECDEB20C4D |
| WindowsHomeHardwareProvider.CpuPerCore.cs | BF75112EBE5ADDE61A91F228EF91B10CA33E5D71DFDE7C0F28021E3C7DE705CE |
| AdvancedCpuTuningWorkspaceV2.xaml | 54A4D1F29C88C8FB8736D04C56FEA87498B96086FAEE441E6A195154E14534A8 |

因此继承getter `0xD5/core<<20`、单核写`0x06`、完整核表验证和回滚契约；UI cs、command factory及CpuRestore历史SHA不同，不作整体同二进制声明。Sol独立核验index0原件/冻结SHA与本轮index1、7，未发现must-fix；主会核验七点全部结果及最终权威状态。

113冻结源码仍为`daa4ea5b6254e8f120cafad19592319d27852a30`；后续验收文档提交不改变该源码归属。[正式发布](https://github.com/1145911974/XISURA/releases/tag/v1.0.113)。
