# AUTO-11：进程GPU输入补丁

本地补丁；AUTO-11仍Open。79项完成29、剩余50（36.7%），目标新增4/54，本轮新增0。安装/正式发布仍113，无新MSI，无硬件写入、GUI启动或输入法切换。全量ETA尚不可可靠估算。

## 实现与支持边界

- `AdaptiveProcessGpuReader`复用CPU reader的同句柄映像、Token SID/session及创建/退出时间检查。后台采集前后进程PID＋birth全集一致，消费时再核当前身份；缓存绑定运行时会话/revision/epoch、scope及generation。
- 查询已有`System.Management`的`Win32_PerfRawData_GPUPerformanceCounters_GPUEngine`，WQL只请求归属PID。严格解析实例名，拒绝未知格式、重复实例、外部PID、过旧/未来时间、倒退及异常数值。实例命名没有已找到的官方稳定契约，系统/驱动改变格式可能回退unknown。
- 使用`UtilizationPercentage`原始累计值及`Timestamp_Sys100NS`：`100 × ΔUtilizationPercentage / ΔTimestamp_Sys100NS`，核Frequency=10,000,000；没有把`RunningTime`或成功回包当占用。两个原始区间及单调区间均0.5–5秒，当前计数年龄≤3秒。最初严格1秒下限会受1秒tick抖动影响，故留0.5秒边界并补0.9秒用例。
- 结果是同归属应用所有稳定可读实例中、**任意GPU上最忙的单个引擎占用**；包括核显/视频引擎，不是RTX独占统计，也不是跨引擎或多进程占用相加。某归属PID没有GPU实例不伪填0；全部缺失为unknown。
- 默认完整采集在最多一个后台Task内；Read不等待WMI，完成时消费一次并立即启动下一次。Reset使旧generation失效但保留未完成任务，避免停用/切换累积线程。WMI请求2秒Timeout不等于硬截止；查询卡住时本功能保持unknown，不占自动调度/Freeze退出通道，也不派发硬件写入。
- Worker为新鲜匹配的RequireActivity规则填写ProcessGpuPercent；与CPU一起在停用、手动优先、上下文失效、异常及设备变化时Reset。未降低活动阈值，未改IPC授权、恢复合同或其他功能入口。

## 验证

1. Reader空实现最初4组中2失败/2通过；Worker接线前真实GPU-only行为失败（预期Turbo，实际Gaming），CPU分支另证GPU采样次数为0。初次编译的EnumerationOptions歧义和过时DataTestMethod分析错误另留原日志，不算行为复现。
2. 接线后6项定向通过：4组reader及CPU/GPU两条Worker路径。覆盖真实零与差分/max、身份/出生/PID集合、scope/reset/时间/失败、实例增减/重复/倒退/超量；阻塞假源下不等待、20次Reset不叠任务、旧结果丢弃和后续连续差分；Worker低全局负载触发及Disable/过期/Closing不采样、模拟退出恢复。
3. 最终提升runner重建并执行服务回归：**581/581通过，零跳过**。包括最终0.9秒用例；保留管道ACL，不重跑已知非提升权限失败，不冒称全解决方案回归。
4. NativeProbe编译链接同一生产CPU/GPU reader，读取已有Explorer三个归属PID，未启动目标应用/制造负载。先1.1秒节奏5个可读区间；最终约1秒节奏12次读取，前三次unknown、后9次连续可读，约0.018–0.026%。最大Read耗时约10.37ms；错误SID/session与Reset后均unknown。没有与异时Task Manager数值作精度等同，也没有达到GPU活动阈值或触发实机模式。
5. 最终权威：原会话`6109fcdb-400e-4439-a89a-225c8f28ef9b` Completed，fields0/pending=false/unresolved0、GUI0，113服务Automatic/Running；偏好、自动化文件及安装GUI/Service SHA均与试前一致。

## 尚未完成

生产Worker自然应用匹配/活动、目标应用启动停止、实机派发及逐点准确恢复仍待；慢/不可用GPU计数会保持unknown。补丁尚未形成正式候选；冻结及集中实机验收后按既有授权安装并上传正式版，不把草稿PR称作已安装功能。

证据：`C:/Users/Administrator/AppData/Local/Temp/XisuraUsableRelease-20261026/feature-completion-r7-gpu-activity/`。

- `before-gpu-reader.{log,trx}`、`before-worker-gpu*.{log,trx}`、`after-worker-gpu{,-final}.{log,trx}`。
- `service-regression-elevated.{log,trx}`、`service-regression-elevated-result.json`、`final-source-sha.csv`、`root-final-authority.json`。
- `native-probe{,-worker-cadence}.log`、`native-probe-result.json`、`native-probe/{Program.cs,NativeProbe.csproj}`；`explorer-identities.json`、`raw-engine-first.json`为前期原始只读记录。

本轮精确检查后尝试清理新探针生成bin/obj共1,083,957字节，自动审批以`blocked by policy`拒绝；未执行、未重试或绕过，暂留C盘。旧轮拒绝记录、恢复原证据均保留。
