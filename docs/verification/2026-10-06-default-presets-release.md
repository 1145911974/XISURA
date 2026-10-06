# XISURA 1.0.69 · 默认预设验收

- 客户端 440/440、MSI 14/14；回归覆盖 72 空档完整默认、旧字段补齐/大小写/旧共享频率、保存值与文件不变、未知版本/非对象数据回退、取消与非法身份拒绝。
- 读取经 ControlPresetStore；CPU 使用 SavedPerformancePreset.ReadDraft/PerformanceDraftValidator，GPU 使用页面实际私有模型及 ValidPreset，风扇使用 FanCurveState/FanCurveSafety，灯光使用 LightingDraft.IsValid。
- CPU 原推荐值保留；GPU 2400 MHz 推荐上限按设备范围适配，核心/显存及 127 个 VF 偏移默认 0；风扇为现有模式推荐完整曲线；灯光为现有默认效果。
- 缺字段仅在内存补齐，保留已有值、名称及文件；MUX 与 A 面标识独立控制逻辑保留。
- 实际 MSI 从 1.0.68 升级至 1.0.69.0，退出 0；客户端/服务 DLL 与发布载荷相同，服务 Running，PID 28576、Responding。
- UI 自动化下拉定位受限，未据此宣称所有档位逐一硬件写入通过；未运行无运行库虚拟机或实际重启。
- 新目录 self-contained 发布客户端/服务后打包；既有 3 个工具链警告、0 错误。MSI 内置运行库校验通过。

MSI：XISURA-1.0.69-x64.msi，184131628 bytes。
SHA256：e9e45c4055806a9359254257e1883432799740a98c2f2b72c7171d9f94cba15f。

临时目录/验收文件因此前自动审批“策略阻止”未清理；安装包与校验文件保留供交付/回滚。
