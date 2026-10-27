# AUTO-11 进程活动采样计划

**目标：** 为 `RequireActivity` 提供有身份和会话范围的进程 CPU/GPU 差分。

**设计：** 服务侧 reader 使用同一进程句柄校验映像名、SID、Windows Session、创建/退出时间及 CPU 累计时间；按 PID＋创建时间比较两个区间，生成有限的 CPU 百分比供 `AdaptiveAutomationWorker` 使用。采样和缓存按运行时活动会话的 SID、Windows Session、scope、revision、epoch 隔离；首次采样、失败或进程/服务重启均为 unknown。绝不以全局 CPU/GPU 值填充进程字段。

## 约束

- GPU采用Windows原始GPU Engine计数，语义为同归属应用在任意GPU上的最忙引擎；首次、缺失或不可信时unknown，不用全局GPU补值。
- 停用自动化、上下文过期或手动优先时清除对应缓存。
- AUTO-11 保持 Open，直到自然规则派发、目标应用启动/停止组合及准确恢复完成验收。
- 验证不触发硬件自动化；保持 1.0.113 安装不变。需要 UI 时仅用副屏，不切输入法。

## 执行与验收

- [x] 在 `AdaptiveAutomationWorker` 接入服务侧进程 CPU reader；归属由 `HomeServiceRuntime.GetClientAutomationProcessScope` 提供，读取实际活动会话的 SID/Windows Session/clientSessionId，再组合 revision/epoch，不引入全局替代值。
- [x] 增加6项隔离测试：首样本/失败/重启为 unknown、差分与归属边界、实际Worker输入链、停用/过期/Closing不再采样；手动优先沿用统一Reset路径，尚无本轮单独实机验收。GPU保持unknown。修复前Worker断言实际失败，接线后6项通过。
- [x] 同一生产reader源码的独立NativeProbe完成本进程4次真实Windows只读差分；首次/错误SID/错误session/切回原scope均unknown。不运行自动规则、不写硬件。AUTO-11仍Open；不把此证据当自然应用派发、效果或恢复验收。
- [x] GPU完整身份夹验在后台单flight中执行；不等待WMI、不叠任务，旧scope/generation结果丢弃。原始计数及单调间隔0.5–5秒，避免1秒tick轻微抖动打断活动；新鲜度≤3秒。4组reader与2条CPU/GPU Worker分支定向通过，服务回归581/581、零跳过；已有Explorer在约1秒节奏下9个连续区间可读。未启用真实硬件调度，详见[GPU补丁](../verification/2026-10-27-auto11-process-gpu-patch.md)。
- [x] 空硬件页的Adaptive同模式请求使用两次新鲜原始读数保留当前设置，无OEM重写、不新增ownership或伪造效果确认。修复因此暴露的CPU/GPU升级围栏回归，保留真正未知写保护。6项定向及最终提升服务585/585通过；当前候选Service配对113 GUI完成两轮副屏只读生命周期，13组/OEM/配置对账及正式服务恢复通过。见[同模式证据](../verification/2026-10-27-auto11-same-mode-retention.md)。
- [ ] 自然规则匹配、活动、应用启动停止及实机派发；AUTO-11继续Open。跨模式OEM准确恢复仍缺证据，同模式只读生命周期不能替代。
