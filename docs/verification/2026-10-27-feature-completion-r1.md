# 功能闭合第一批：强冷能力与V35风扇新路线

2026-10-27阶段记录。本文前序条目保留其记录时的安装/发布状态；当前113安装验证状态见文末更新。

## EXT-07已闭合

- 原因：创建FieldCapabilities时强冷Readable硬编码false，固件实际读值在其后取得。
- 修复：同一快照只读一次OEM强冷bool?，同时用于Controls及Readable；0/1可读、null未知，来源oemFirmwareReadback，现有写准入不变。CPU恢复/GPU提交中的能力重算不读取无关硬件，以null显式传入；同步所有私有反射测试调用。
- 新增一个行为回归方法，覆盖false/true/null及有/无写能力。RED实际失败（预期可读而实际false），修复后21项相关回归通过。
- 管理员只读实机候选：flag0、Controls=false、Readable=true、source正确，候选exit0；服务临时停止/恢复Running，偏好/AUTO文件哈希未变，零硬件值写入。
- Service DLL SHA256：4397F38778A05B4EB7DD032B4D06C05D9E207C4EC2E2D49FDA1BBBB90D5FCAC8。
- 全Service在管理员权限下569/569、零失败/跳过。普通shell首次全套558过/11失败：10项管道被管理员ACL拒绝（GUI manifest同样要求管理员）；另1项worker测试观察client-control-session.json遇共享冲突，提升权限复跑未再发生。原始失败保留，该一次性共享冲突不宣称已修复，最终交付EXT-08仍须关注。

## V35风扇：新事实，未计功能完成

- 原失败1060映射不重复试写。第三方反编译Read_EC_EX实际写0并返回0，不能当作成功的EC getter照搬。
- 同版AML：WMI getter21返回FASP、setter21将FUN3写FASP；FASP为ECF2 SystemMemory偏移0x5F的8bit设置字段，不是转速计数器。强冷20对应独立FAAP位。
- 实机只读WMI20均0、WMI21连续4次均51；同次快照模式balanced，tachs1992/1969 RPM。未进行method21写入，也不从这个读值推导百分比/RPM。
- 参考项目mode范围22–35/35–50/50–58及显示百分比只是候选线索；当前模式下51的语义、恢复/模式副作用仍需闭合。ECRD在不可用或取锁失败时也可能返回0，零不能直接当可信配置。

## 证据与状态

证据根：C:/Users/Administrator/AppData/Local/Temp/XisuraUsableRelease-20261026/feature-completion-r1。

- strong-cooling-red.trx/log、strong-cooling-green.trx/log。
- service-regression.trx/log（原失败保留）、service-regression-elevated.trx及service-elevated-result.json。
- probe-result.json、candidate-state.json、Probe-Readback.ps1：只读探测及独立OEM原始回包。

首次测试误继承系统TEMP到D盘，只生成red.log/trx；已核验SHA复制回上述C盘目录，再删除这两份误生成文件及空叶目录。后续所有执行器显式设置TEMP/TMP到C盘。原始恢复证据未删除。

台账79项：既有25+EXT-07=26完成，53未完成，32.9%；新目标增量1/54=1.9%。SET-06原生强冷Keep已实机复现111退出失败，修复候选仍待验，新版最终构建/安装/发布仍未完成；全量ETA尚无法可靠估算。

## 111强冷Keep失败与恢复

- keep-native-run1：原生flag0→1成功；Keep退出失败，journal fan.strongCooling=submittedUnverified，session Active且普通写冻结。原因是111生产能力Readable=false，Keep要求Confirmed；失败发生在durable Keep计划提交前。
- keep-native-recovery1保留失败原证据；keep-native-recovery2通过正常认证End(false)重试恢复原flag0，Completed/fields0。原host已正常收尾离开，恢复附件无法取得进程退出码，不宣称exit0。
- keep-native-recovery-final.json：Passed=true；独立method20=0/method21=51；13组控制恢复；7份配置哈希与试前相同，无需覆盖；正式111服务Running。
- 已提交Keep计划不可用false覆盖；这次未形成计划。修复能力只覆盖新会话，不重写旧submittedUnverified记录。

## 修复候选Keep实机与112

- keep-integration-final.trx：2/2，生产能力强冷Confirmed、Keep后true及重开baseline=true；旧未确认Keep失败后无保留计划、冻结写拒绝、认证End(false)恢复false/Completed/fields0。Sol独立窄审已覆盖。
- keep-patched-run2/result.json：Passed=true/Cycles3/KeepRetentionProven=true/ExactConfigurationRestored=true/HostExit0。保留后值1及重开1成立，随后准确恢复原0，默认退出及再次重开0；method21保持51，13组控制和受保护配置一致。wrapper确认正式111服务恢复。该次Service源码bin+GUI/Diagnostics111混合验证，不冒充最终配套Release。身份/DLL SHA见mixed-identities.json。
- 112版本1.0.112，产物usable-release-1.0.112-strong-cooling；集中回归/发布输入hash冻结已启动，最终配套实机、安装和GitHub尚未完成。台账维持26/79、53剩余、32.9%。

## 112配套正式候选验收

- completion112/final-20261027-160518-374：10组1370/1370，零跳过，三产物自包含Release成功；源码输入前后SHA一致。首轮final-20261027-160251-942失败保留；WorkerHandoff观察器仅对Windows文件暂缺/共享32/锁33重试，保留8秒期限/Completed解析及全部恢复断言，针对性1/1、独立Sol窄审通过。
- keep-release112-run3：配套112 GUI/Service/Diagnostics三轮Passed、Cycles3、KeepRetentionProven、ExactConfigurationRestored、HostExit0；独立method20原0→1、Keep退出与重开保持1、立即恢复原0、默认退出/再开0；method21保持51，13组控制/配置核验通过。wrapper确认旧111服务恢复；后续安装独立记录。SET-06完成。
- 当前27/79完成、52未完成、34.2%；新目标增量2/54=3.7%。全部收束仍受协议/禁测条件限制，ETA不可可靠估算。

## 用户输入法/副屏纠正与113阻断

- 用户明确后续窗口副屏、禁止改变输入法。根因三入口主动UseMicrosoftPinyin并ReleaseInputProcessor(Doubao)；已移除3调用及整个主动层。旧2项重试策略测试因行为取消而移除，新增不接管用户输入法回归；总数113为1369，全部通过。输入法首轮red构建因主会过早删除文件导致CS2001，不算行为RED；原log保留。随后相关2/2与最终1369/1369有效通过。
- --accept-secondary首次显示前定位副屏，正式托盘菜单也沿用副屏区域；不修改显示布局或鼠标。
- keep-release113-run4：首窗口独立副屏区域已证；CloseWindow至托盘时GUI真实崩溃，Application1000 code c000027b，WER80070578，不能归类UIA助手失败或通过。尚未SetKeep及硬件数值写入。现有dump= C:/Users/Administrator/AppData/Local/CrashDumps/Jiaolong.ControlCenter.exe.13392.dmp，Sol离线分析。
- keep-release113-run4-recovery.json：Passed，ConfigHashesUnchanged，13控制相同，Completed/fields0、GUI不存在、候选正常停止、已装112服务恢复Running。截至run4恢复时，112未GitHub发布、113尚未安装；该时点记录不代表后续状态。当前台账仍27/79/52剩余/34.2%。

## 113托盘崩溃定位与窄验证（10-27）

- SOS对dump13392/31836的定位为`TrayContextMenuWindow.ShowNearCursor`首次`Move`，异常`c000027b`/`80070578`。主窗`SW_HIDE`改动未经实机验证，已撤回。
- `window113-tray-defer-run1`延迟失活Hide候选在第2轮仍崩溃，候选已撤回且恢复`Passed`。`window113-tray-trace-run1`助手路径错误、`run2`启动时进程Path检查过早，均非产品通过且服务已恢复；保留原记录。`window113-tray-trace-run3`原行为两轮通过。
- 当前托盘重开顺序为`hasBeenShown`后先`Show(false)`再`Move`，首次显示仍先定位副屏。`window113-tray-show-first-run1`两轮通过：同一HWND隐藏/重开及正常认证退出；13项控制、配置哈希不变，session `Completed`、fields=0、候选exit0、112服务恢复`Running`。
- `completion113/final-20261027-170255-196`成功：1369/1369、零跳过、源码输入SHA前后一致，GUI/Service/Diagnostics均为自包含产物。GUI DLL SHA256 `415D5CD7AAC7BEBF06D4920D01E5024EF6100689DCC33623167BDF5BE40CD88A`；Service DLL SHA256 `69953DE2D78B4B400BC15113F4770601FF3F836AD13A31A94E5ADECD68C136F6`。
## 113配套Keep及交付状态更新

- `keep-release113-run5`未通过：缺少可见主窗观察，无新崩溃；恢复准确原值、配置和服务。助手增加同一可见断言的3秒有界观察，保留断言本身。
- `keep-release113-run6`配套GUI/Service/Diagnostics三轮通过：Passed、KeepRetentionProven、ExactConfigurationRestored、HostExit0；wrapper `ServiceRestored=true`，method21仍为51。
- 截至run6后，MSI构建/安装链仍在运行，尚未记录113安装；该时点状态由下方安装结果更新。台账仍27/79、52剩余、34.2%。
## 113安装与已安装生命周期（后续更新）

- 113安装链完整Passed：MSI exit0、无需重启、UserFilesUnchanged、Controls13Unchanged、InstalledVersion=1.0.113.0；已安装GUI两轮副屏主窗/托盘隐藏重开/正常退出，ServiceStop/Restart及ProtectedConfig均Passed；服务Automatic/Running。
- MSI SHA256 `22C4596E13055242153FBCF95A726BF1E3186A78EC1BBCDB8DC142BC853B231A`，bytes=182996503。证据：`install-publish-113/installation-result.json`、`install-chain-result.json`、`installed-lifecycle/result.json`。
- GitHub发布仍在准备，尚未公开。台账27/79、52剩余、34.2%不变。