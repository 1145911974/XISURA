# SET-02：铺满后手动缩放的缓存修复

## 缺陷与补丁

源码确认：普通尺寸A→铺满→手动缩放至B，`fittedToWorkArea`仍为true，`SaveWindowSizeIfEnabled`继续保存A；再次点铺满也会错误恢复A。原native尺寸约束回调未更新铺满状态。

最小接线：`WM_ENTERSIZEMOVE`捕获铺满时的实际客户区尺寸，转换为当时DIP；有效`WM_SIZING`只记录发生过缩放；`WM_EXITSIZEMOVE`读取最终实际客户区，仅逻辑尺寸确有变化才清铺满标志。取消/拖回原尺寸、纯移动、DPI-only变化和最终读取失败均保留缓存；每轮结束清临时状态。决策放入内部23行`WorkAreaFitSizingSession`，未重写窗口或恢复框架。

根修改与集成；Sol独立只读核实调用/取消边界，未见阻断缺陷；Luna核台账31/79及SET-02仍Open。

## 验证

- 新增4个测试方法：有效缩放及后续交互隔离、取消/原尺寸/读取失败、纯移动与未铺满、跨DPI逻辑尺寸。
- 首次缺类型编译失败，随后空实现暴露未使用字段告警；可编译空实现的目标断言出现预期false失败。该红灯只证明新增状态决策测试能拒绝空实现，**不是旧GUI实机复现**。早期Temp日志已缺失，不计作当前可复核原件。
- 10:44 +08 Release构建及定向8/8、零跳过；含新增4项、既有窗口持久化/摆放3项、native尺寸入口1项。TRX计数和SHA直接核对，原件：[regression-restored.trx](retained/2026-10-28-fit-sizing/regression-restored.trx)，[结果](retained/2026-10-28-fit-sizing/result-restored.json)，[源码/构建身份](retained/2026-10-28-fit-sizing/verification.json)。PRI257/PRI263既有资源警告仍在。
- 候选GUI DLL SHA256 `2705E9BA0996DA49A3BDF6451E9C5BDF44B03642877D56A9801B2533A1238C3B`；FileVersion仍1.0.113.0，**不同于正式安装113，未启动、安装或发布**。

整数DIP会忽略不足一个DIP的微调；单测不证明Windows真实取消/消息顺序/DPI切换时序。记忆保存重开、关闭记忆开关和实际手拖仍待，SET-02保持Open。本轮新增完成0；79项完成31、剩余48、39.2%，全量ETA无可靠依据。

## 并行清理与证据边界

运行中发现原`C:/Users/Administrator/AppData/Local/Temp/XisuraUsableRelease-20261026`及测试obj还原文件缺失，用户确认**另一个工具仍在清理**。缺NuGet导入时`dotnet test --no-restore`曾exit0却未执行测试，已把该结果标Passed=false并保留[result.json](retained/2026-10-28-fit-sizing/result.json)；不当作通过。随后在C盘还原依赖，获得上列实际8/8 TRX，直接留存在工作树。

本轮复验后停止继续构建及GUI/硬件验收；不重建空历史恢复证据、不重试已拒绝操作。本轮没有硬件写入、GUI启动、输入法切换、安装或远端上传。保留少量工作树日志/TRX用于验收，构建输出暂供候选后续运行复用；不删除已被拒清理的共享路径。清理结束后先确认源码、证据和权威journal完整性，重新取得新鲜基线再做硬件测试，不按历史快照写回。
