# AUTO-11：同模式无写入及调度回归修复

79项完成29、剩余50（36.7%），目标新增4/54，本轮新增0。AUTO-11仍Open；正式安装/发布仍为113。全部收束ETA无法可靠估算。

## 改动与边界

- Adaptive请求无硬件页且目标等于当前原生模式时，不再重复提交OEM模式。两次真实模式读数须新鲜≤1秒、raw一致；同时复核会话代次、生命周期、目录及自动配置版本。证据无效即拒绝，不退回模式写入。
- 保留返回`NoMutation`、`SubmittedUnverified`，不伪造有效模式确认，不新增native ownership；手动、有硬件页及不同目标保留原路径。
- Worker区分“沿用当前模式但效果未独立确认”和“实际写入后未确认”。前者不新增阻升档围栏，且不能清除已有围栏；状态、CurrentTarget及stage override仍仅依据独立确认。

## 软件验证

证据根：`C:/Users/Administrator/AppData/Local/Temp/XisuraUsableRelease-20261026/feature-completion-r8-auto-same-mode/`。

- 新同模式测试首次有效RED：2失败/1通过，原实现实际重复写入，且首读过旧仍应用。首个错误filter匹配0例，不算RED证据。
- 新分支后完整服务回归584例，原CPU/GPU Worker两项实际失败：预期Turbo:1，实际Gaming:1。根因为无写入返回未确认，Worker误设升档围栏；没有Ignore、删原Turbo断言或降低活动门槛。
- 修复后6项定向通过：同模式无写入/不冒充确认/ownership保留、无效证据及版本拒绝、不同目标仍写、Worker旧围栏false/true保留、CPU/GPU两条实际Worker链。两条活动用例先验证Gaming无写未确认，再验证Turbo提交。
- 最终提升权限服务回归：**585/585、失败0、跳过0**，`service-regression-final.trx`，21:52:52 +08结束。不是当前全仓1369例重跑。
- 中间测试fixture修正及失败日志全部保留：初次把首tick当作已过3样本暖机；新增Worker fixture须在runtime构造前设Ready，且从Balanced开始。最终断言覆盖真实提交和原升级结果。
- 独立Sol复审未见新增阻断。旧围栏用反射注入，未重演真实未知写跨epoch；此证据范围不扩张。

## 候选真实运行

候选为当前Release目录的**框架依赖Service**，与已安装113 GUI配对；Contracts DLL SHA一致。正式服务先停止，固定pipe无并行服务，复用原`C:/ProgramData/JiaolongControlCenter/control-journal-110`，未新建空journal。

- run1：脚本把`C:/…`与进程`C:\…`误判为身份改变；无控制字段写入。绑定原PID/birth及规范化路径正常TrayExit，原会话Completed，marker协作停候选。恢复观察进程的退出码为null，不能记为0；随后独立13组及OEM/配置对账通过并恢复113服务。原失败及恢复文件保留。
- 修正路径比较后的run2：**2/2**完成启动读取→CPU/GPU/风扇只读导航→托盘保持/打开→正常退出→重开；每轮在线/退出及最终13组控制快照一致，6个OEM字段一致。14份保护配置原字节不变。
- run2时间21:57:56–21:59:21 +08；当前Balanced/PPT65W，仅作为本轮新鲜基线，未写回历史值。GUI使用`--accept-secondary`；第二轮可见主窗UIA矩形`4088,422,2384,1487`。未主动切换输入法。
- run2候选后台退出码0；正式113服务恢复Running。会话`d85246bc-4279-4860-ad28-0a21c9383c40` Completed，fields0/unresolved0，GUI0。
- 偏好SHA `0AEA2036B1278756E5AC797E6FCD3E018EE03B02B6DF1EF07D0025DB49CAC74E`；自动配置SHA `41DCF1D5682DE181CB207931CBA4BEAA06DA2EA3057DD779157E84CDA0CC028A`，均与试前一致。

生命周期证据位于同根`feature-completion-r8-candidate-readonly-run1/`及`feature-completion-r8-candidate-readonly-run2/`，wrapper、恢复及最终源码清单位于上列r8目录。原证据不删；没有生成新MSI或安装新正式版。

## 仍待完成

本轮未启用真实自动调度。同模式直接请求已由隔离测试证明零写，实机生命周期只证明兼容及退出收尾，不证明自然规则派发。自然应用匹配/RequireActivity活动、目标应用启动停止组合及实机派发仍待。不同模式涉及OEM ITSM/STSM与延后COMM副作用的准确逆向恢复仍缺证据；不以同模式验收替代。后续使用新鲜试前值，逐点立即准确恢复。
