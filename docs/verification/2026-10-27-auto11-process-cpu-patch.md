# AUTO-11：进程CPU输入补丁及验证

状态：本地补丁已接线并验证；**AUTO-11仍Open**。79项完成29、剩余50、36.7%，本轮新增完成0；全量ETA仍受协议/禁测/实机验收依赖限制。当前安装和正式发布仍为113，未生成新MSI、未替换本机服务或GUI。

## 实现

- 新增`AdaptiveProcessCpuReader`，按匹配规则进程名枚举；同一打开句柄校验映像名、Token SID/Windows Session、创建/退出时间及kernel+user累计CPU。被采样应用无需管理员身份；句柄逐次释放。
- 差分按PID＋创建时间及完整实例集合匹配，用单调时间、逻辑处理器数归一化。实例增减/重启、倒退、超过5秒、首次、不可读均unknown，不累加部分实例冒充完整占用；真实连续零增量才返回0。
- `HomeServiceRuntime.GetClientAutomationProcessScope`从已验证活动会话取SID、Windows Session、clientSessionId，核Active、Closing和设备epoch；不从客户端进程名推断归属。
- Worker只为新鲜上下文下匹配且`RequireActivity=true`的规则读取进程CPU。scope包含会话/revision/epoch；统一Reset同时清过滤器与差分缓存，覆盖停用、手动优先、上下文过期、异常及设备变化。`ProcessGpuPercent`仍unknown，不以全局GPU替代。
- 独立窄审发现GetProcessTimes退出FILETIME原先被忽略，已改成退出非零返回unknown，避免末次退出CPU当作活动。没有修改阈值、冷却、停留时间、IPC授权或原有恢复合同。

## 证据

1. 接线前：新增Worker测试实际失败，预期Turbo预设、实际Gaming预设；全局负载低于所有升档阈值，缺口不靠降低断言掩盖。最初测试编译类型错误另行保留，不计作行为复现。
2. 接线后：6项定向测试通过、零跳过。覆盖多实例/归属/差分边界、活动会话Scope、实际Worker假输入触发、Disable/上下文过期/Closing不继续采样及模拟退出恢复。此处都是隔离Provider，不是实机模式效果证明。
3. 最终服务回归：**576/576通过、零跳过**。首次非提升运行566通过/10失败，全部10例实际错误为命名管道ACL拒绝访问；提升runner使用同一已构建程序集、保留授权限制后复核通过。原始失败TRX/日志保留，未Ignore、删除或削弱断言。没有将本轮服务回归冒称新的1369项全套回归。
4. NativeProbe直接编译链接同一生产reader源文件，真实Windows API读取本探针进程，未制造负载；4个连续区间可读（约0.2944%、0、0、0），16逻辑处理器。首次、错误SID、错误session、切回原scope都unknown；另记录Process.TotalProcessorTime累计时间，未宣称异时百分比精确相等或精度校准。仅证明本进程只读Native链路，尚不是生产Worker实机规则派发。
5. 最终权威：仍同一既有Completed会话`6109fcdb-400e-4439-a89a-225c8f28ef9b`，fields0/pending=false/unresolved0、GUI0，113服务Automatic/Running；偏好、自动化文件及安装GUI/Service SHA与试前一致。未启动GUI、切输入法、写硬件或改变用户配置。

## 仍缺的功能与后续

- 进程GPU占用仍未接通；GPU活动而CPU低于阈值的应用不能靠本补丁证明触发。
- 自然应用匹配/活动、应用启动停止组合、目标派发及立即准确恢复尚待实机验收；当前配置未启用自动化，原native模式副作用恢复门仍有效。
- 本补丁未形成正式新版本。后续候选必须冻结源码、必要回归、实机启动/托盘/退出重开和新分支验收，再按持续授权安装本机并上传GitHub；不得把113原SHA指作新补丁产物。

证据根：`C:/Users/Administrator/AppData/Local/Temp/XisuraUsableRelease-20261026/feature-completion-r6-auto-activity/`。

- `before-integration*.log`、`before-integration-run2.trx`、`after-integration.trx`。
- `service-regression{,-elevated}.{log,trx}`、`service-regression-elevated-result.json`。
- `native-probe-result.json`、`native-probe.log`、`native-probe/{Program.cs,NativeProbe.csproj}`。
- `changed-source-sha.csv`、`root-final-authority.json`、原文件`.before`及验证脚本。

探针生成`native-probe/bin`和`obj`共466185字节，精确路径/生成属性已检查；删除调用被自动审批以`blocked by policy`拒绝，未执行、未绕过。暂留C盘；原始日志、源码和验证记录用于复核也保留。
