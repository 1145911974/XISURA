# XISURA 1.0.68 验收（2026-10-06）

- 客户端 436/436、安装包 14/14 通过；Release publish、完整 MSI 校验通过。保留 3 个工具链警告：既有 ServiceConfig、SDK 两个 DLL 的语言元数据长度。
- 本机最终 MSI 安装退出 0；客户端与服务 DLL 均匹配最终发布载荷，服务 Running。
- Native UI：CPU/GPU/风扇/灯光均可进入；管理草稿 SPPT 55→56，返回后硬件温限/SPL/SPPT/频率仍 99/82/120/3.3 GHz；再进入保留 56，复原草稿后退出。预设文件哈希未变化。
- 关闭随模式应用仍可管理、保存与另存为；另存为显示明确覆盖说明，取消不写入。
- 本机实际加载 coreclr.dll、Microsoft.UI.Xaml.dll、Microsoft.WindowsAppRuntime.dll 均来自安装目录；MSI 文件表还验证 DWriteCore.dll、hostfxr.dll、vcruntime140.dll。未执行全新无运行库虚拟机验收。
- 当前用户登录任务：HighestAvailable、InteractiveToken、无 Delay；电池不阻止，重复触发忽略新实例。最终两轮 Task.Run 前后唯一 PID 49260、Responding，副实例退出 0；没有实际注销/重启。
- 安装验收发现并修复风扇初始化回调访问未创建控件；修正版实际启动通过。硬件读回超出编辑范围的 82/120 W 保留原值；输入与滑动条仍受编辑范围约束。
- README 五张截图为最终已安装 1.0.68；仅捕获所属窗口于非主屏 DISPLAY1，临时置顶后还原，未捕获主屏或其他窗口。

MSI：XISURA-1.0.68-x64.msi，184143916 bytes。
SHA256：539dc6ac8127c362d12c30183568201fc48334032bdcf68a46be1540c1930235。

交互：默认当前电脑；使用档位直接应用；管理只编辑；保存不应用；保存并应用独立；另存为选择位置。自动跟随按页记忆档位，缺失默认均衡；显示输出及 A 面标识直接控制，不由预设覆写。

构建：C: 临时新目录分别 self-contained publish 客户端/服务，再以 PayloadPublishDir、SkipPayloadPublish=true、BuildProjectReferences=false 打包；缺关键离线依赖即失败。发布不依赖旧 .publish 剩余文件。

清理：自动审批以“策略阻止”拒绝清理自启动诊断生成目录，未绕过，暂留；最终 MSI、校验文件保留供下载与回滚。