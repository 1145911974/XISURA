# AUTO-11：113只读遥测与应用活动输入缺口

结论：AUTO-11仍Open；无新产品构建、硬件测试写入或自动化启用。79项完成29、剩余50，36.7%；本轮新增0。证据保存与读回不计功能完成，全量ETA暂不可可靠估算。

## 已安装113实际观察

- 2026-10-27 20:30:25–32（+08:00），GUI PID35224、创建FILETIME134375778186627030；已安装路径启动，`--accept-secondary`。副屏DISPLAY1、Primary=false，工作区(2560,261,1920,1032)，主窗(2725,281,1589,991)，全部位于副屏。
- 8次生产`getDeviceState`只读请求，CPU/GPU各6个不同原始采样时间；每次传感器年龄均在−1至3秒有效范围，重复缓存不算新样本。CPU约30.38–92.33%、GPU7–47%。这是可读性/时间戳证据，不是独立精度校准，也不是低负载条件或规则派发通过。
- AUTO始终disabled；未添加规则、未启用调度。此前run3低负载准入拒绝不能据此归因为CPU读取器永久不可用；本轮也不抹去旧失败。
- 正常退出，同会话`6109fcdb-400e-4439-a89a-225c8f28ef9b` Completed；fields0、pending=false、unresolved0、GUI0，113服务Automatic/Running。
- 助手逐次对比偏好/自动化文件SHA，前后一致；主会额外独立捕获13组受控配置，全部相同，偏好仍为既有SHA `0AEA2036B1278756E5AC797E6FCD3E018EE03B02B6DF1EF07D0025DB49CAC74E`。原始GPU全快照相等=false，不宣称全部动态GPU字段保持。
- 未调用输入法切换API或按键注入。未制造负载、启动应用/游戏、切换性能模式或显示布局。

## 已定位的实现缺口

1. `AutomationWorkspaceV2.Service.cs:160`发送前景进程名、运行进程白名单及空闲上下文；`AdaptiveAutomationClientContext`没有进程占用字段。
2. `AdaptiveAutomationWorker.cs:146`匹配应用，再建立`AdaptiveTrialInput`，填写全局CPU/GPU占用，**未填写**`ProcessCpuPercent`、`ProcessGpuPercent`。
3. 两属性为nullable、无默认有效值（`AdaptiveAdvancedSettings.cs:80`）；`AdaptiveFilteredSession.cs:157`仅用进程CPU≥5%或GPU≥10%累计活动。`RequireActivity=true`的活动确认条件因此得不到生产输入；过滤器的孤立模拟输入不能证明生产已接通。
4. 这不表示所有应用规则完全无效：`RequireActivity=false`有独立匹配路径；缺活动的规则仍可能限制上限并导致降档，不能概括为“一律不执行”。两条路径的实际派发/恢复仍待验。
5. 所有Follow=false仍会通过Worker请求预设原生模式；同当前模式也不保证零写。不能将启用真实规则伪装成只读观察。

上述五个生产文件与113最终构建`source-inputs.csv`逐项SHA一致，详见原始`source-association.json`。当前安装版缺口与冻结源码有关联；未用陈旧索引代替实际文件。

## 下一实现边界

优先补齐服务侧匹配应用的CPU差分输入；GPU进程占用未实现前保持unknown。范围取已经验证的活动客户端SID和Windows SessionId，不直接信任进程名归属。按规则白名单、同一打开句柄核身份/创建时间/CPU累计时间，按PID+创建时间隔离，用单调时间与逻辑核数归一化。首样本、无法读取、退出或超长间隔返回unknown；停用、上下文过期及归属变化清缓存，不拿全局占用或0冒充进程占用。

这是待实现方案，不是已修复。先验证输入/身份/缓存边界，再验自然进程规则；每次模式或数值测试必须以新鲜试前值立即准确恢复并独立核验。原生模式副作用未具备精确恢复合同前，不启用规则派发。

## 原始证据

根：`C:/Users/Administrator/AppData/Local/Temp/XisuraUsableRelease-20261026/feature-completion-r5-auto-context/`。

- `installed113-readonly-run1/{result,telemetry-summary,source-association,root-control-comparison,root-final-authority}.json`及8个`telemetry-*-state.json`。
- `installed113-readonly-run1/runtime/{automation,normal-open}.json`，同目录`formal-exit.json`及原始进程/会话记录。
- `Inspect-AutoTelemetry113.ps1`、`Verify-AutoReadonly113.ps1`；最初生成循环时变量提前展开的语法错误已被AST检查拦截，修正后才启动，未以失败运行冒充成功。

原始采样、恢复及构建关联留存C盘，供复核；本轮未产生新安装包，无须替换已经交付的113。
