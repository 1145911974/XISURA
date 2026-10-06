# XISURA 1.0.70 使用冲突与自启修复 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement task-by-task.

**Goal:** 稳定风扇曲线，统一自适应控制权，删除失效动画/调度，登录及时启动，离线安装可用。
**Architecture:** 服务唯一调度自适应；实际手动 CPU/模式操作先暂停，草稿/被拒请求不改运行状态。状态回读按值去重，不取消正在拖动的曲线。
**Tech Stack:** WinUI 3 / C# / Windows Task Scheduler / WiX MSI。

- [x] FanCurveDraft/Plot/Workspace：相同曲线不重建或动画；拖动/节点放置不被回读打断；EC 参考曲线保留灰色；分策略面板不重播同一状态。
- [x] HomeHero：删除 8 层旧强冷贴图、未调用动画与 StrongCoolingMotion；保留自适应自己的开关反馈和高对比处理，旧贴图不再打包。
- [x] AutomationWorkspace/PrototypeWindow：删除服务绑定时不可达的客户端执行器及接口；拒绝内置狂飙覆盖时不关闭自适应。检查模式/预设/跟随/强冷/内置策略控制权。
- [x] App/StartupRegistration/Service.wxs：先显示窗体，再后台修复登录登记；登录任务正常优先级、失败重试、无延迟/电池限制；服务自动启动不延迟。
- [x] 回归（本会话新测试总数 ≤10）、新目录自包含客户端/服务及 MSI；本机升级、菜单/曲线/自适应实机检查、实际登录任务启动与单实例验证。
- [x] 私仓发布新 MSI、校验文件、说明；远端大小及摘要匹配。实际冷启动/无运行库机器验证边界已说明，交付包保留。
- 清理本轮构建载荷/截图/日志被执行环境 “blocked by policy” 拒绝，临时产物保留；此前被拒清理亦未重试。
