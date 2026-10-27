# SET-02：原生窗口诊断与铺满尺寸DPI修复

2026-10-28；SET-02仍Open。79项完成29、剩余50（36.7%）；本轮新增闭合0，无新安装/发布。

## 已安装113的真实窗口证据

`readonly-hwnd-run1`首次未在Hidden启动后执行TrayOpen，无法找到唯一可见native主窗；结果Passed=false保留。正常TrayExit、原配置及13组/OEM恢复Passed，没有Resize或偏好写入。随后一次v2启动参数遗漏`-ExecutionPolicy`前缀，进程结束、run2目录未创建、GUI0后才纠正启动；不是因观察超时重启。

`readonly-hwnd-run2`通过：先真实TrayOpen，再绑定PID44832、创建时间及主HWND **8458642**，使用PerMonitorV2读取原生几何、未过滤UIA根候选及`AutomationElement.FromHandle`。

| 阶段 | 原生HWND | Visible | 客户区 / DPI | UIA根候选 | FromHandle Offscreen |
|---|---:|---|---|---:|---|
| 显示 | 8458642 | true | 1573×983 / 96 | 1 | false |
| 关闭隐藏 | 8458642 | false | 1573×983 / 96 | 0 | true |
| 托盘重开 | 8458642 | true | 1573×983 / 96 | 1 | false |

显示时外框`2725,281–4314,1272`在副屏；未切换显示配置、输入法、鼠标或键盘。此轮不执行Resize、不修改Remember或尺寸值。证明普通托盘生命周期和新鲜HWND诊断路径可用，不能解释旧Transform缩放后的缺失，也不能宣称记忆重开、手动拖拽或跨DPI实机已通过。

最终会话`aa57a973-7406-4a30-aad9-7ec898136940` Completed，5个恢复步骤全部Verified、GPU recovery ready，无fields/pending/unresolved或残留所有权。00:24:46 +08独立捕获Balanced；13组控制及6个OEM字段与新鲜试前一致，14份保护配置原SHA不变；GUI0、113服务Running。偏好SHA `0AEA2036B1278756E5AC797E6FCD3E018EE03B02B6DF1EF07D0025DB49CAC74E`；AUTO SHA `41DCF1D5682DE181CB207931CBA4BEAA06DA2EA3057DD779157E84CDA0CC028A`。

## 已修源码，未运行候选

独立源码审查发现`PrototypeWindow.xaml.cs`的铺满工作区路径缓存原DPI像素，退出保存却除以当前DPI。例如144DPI缓存1920×1200像素，之后96DPI保存会被误记为1920×1200 DIP，而原逻辑尺寸应为1280×800 DIP。

最小单文件修复：缓存改为`WindowSizePreference`（DIP）；铺满状态保存直接使用缓存DIP；还原先移动回原位置，再按目标DPI转换像素。普通窗口保存路径不变。未改尚未复现的WM_SIZING/铺满状态语义；源码审查未发现普通副屏启动无条件覆盖Remember尺寸的问题。

- `PrototypeWindow.xaml.cs` SHA `F4B5ED8DC4F7ECA01F63E61AACD17781941E9D09AF7A5A41217AEFFE0E2C8432`。
- Release构建成功；4项窗口定向测试及GUI完整回归 **583/583、零跳过**。不等同全仓回归或真实跨DPI运行。PRI257/PRI263资源语言警告仍存在，本轮未扩大修改范围。
- 构建GUI DLL SHA `54DA9EBC0596B43D13C2023FD0E202319D0FAEE87E59D076434B4A6A10D816B0`，程序集仍1.0.113.0，为未交付的本地构建，不能与已安装113相混淆。
- 已安装GUI/Service SHA分别仍为`415D5CD7AAC7BEBF06D4920D01E5024EF6100689DCC33623167BDF5BE40CD88A`、`69953DE2D78B4B400BC15113F4770601FF3F836AD13A31A94E5ADECD68C136F6`。

## 未执行动作及剩余

自动审批拒绝两条命令，均报`blocked by policy`：清理生成的`tests/Jiaolong.ControlCenter.Tests/bin/Release`；生成并启动候选GUI验收助手。命令整体未执行，候选脚本/运行目录均不存在；未改用其他路径重试或绕过。未提供更具体拒绝原因。269MiB测试输出暂保留，C盘约535MiB空闲；其TRX/日志及源码修复前备份另存证据目录。旧目标的历史清理拒绝也继续保留。

仍需实际运行此修复，验证Remember开启保存与新进程重开、关闭开关忽略保存尺寸、手动拖拽及跨DPI/铺满还原。跨屏时序尚无运行证据，不得宣称该缺陷已实机闭合。不得为此切换显示配置。完成候选必要运行验收后，才可冻结正式版本、安装本机及上传GitHub。

证据根：`C:/Users/Administrator/AppData/Local/Temp/XisuraUsableRelease-20261026/feature-completion-r10-settings/`；包含`readonly-hwnd-run1/run2`、`code-review/fit-dpi-before`、`window-regression.trx`、`gui-regression.trx`及对应结果/日志。已通过的诊断和原始失败、恢复证据均保留；不重复Resize或同一只读生命周期。
