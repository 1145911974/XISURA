# SET-02：113副屏窗口尺寸验收未通过

2026-10-27，已安装1.0.113.0。SET-02仍Open；不把程序化缩放算手动拖拽，也不把同DPI观察算跨DPI实机验收。本轮未修改产品源码、构建或安装新版本。

## 尝试与恢复

1. `installed113-run1`：助手未规范化路径分隔符，身份比较失败；未切换设置或缩放。主会以journal会话ID、PID、创建时间及真实安装路径核实进程，正常托盘退出。`root-recovery.json`证明偏好原SHA未变；不是放宽身份门禁后继续调节。
2. `installed113-run2`：初始窗口位于副屏，客户区1573×983、DPI96、外框1589×991。真实设置页开启“记住窗口大小”，然后UIA Transform请求客户区1280×800（外框1296×808）。随后未再找到唯一可见主窗，助手失败；正常清理退出时偏好保存为true/848/531。没有得到缩放后可靠窗口矩形，也没有完成托盘保持、新进程尺寸恢复或手动拖拽验收。原失败结果及CleanupError保持不改。

848×531对应的外框尺寸约864×539，与请求值相差约1.5倍。随后只读核查确认：PowerShell助手默认system-aware、系统DPI144，而目标副屏DPI96。修正为UIA/Forms初始化前设置助手线程PerMonitorV2，再进行一次有界验证。

3. `installed113-run3-permonitor`：目标1280×800，正常清理退出实际保存1280×800，证明前次尺寸倍率来自助手DPI上下文。但即使增加三秒有界观察，缩放后仍未找到唯一可见UIA主窗；没有得到可靠缩放后矩形或完成记忆重开。产品源码无尺寸变化即Hide/重建的回调，现证据不能判定产品主动隐藏。停止重复此Transform路径；没有改显示配置、移动鼠标、注入键盘或切换输入法。

助手保护拒绝了未记录的尺寸保存过渡。主会对run2、run3分别核对：原会话已Completed且GUI0；只有本轮拥有的三个偏好字段变化，全部非目标字段语义相同。记录恢复前文件原件及SHA，在独占文件句柄内重新核对当前SHA后，准确恢复各自试前完整字节，SHA256均为`0AEA2036B1278756E5AC797E6FCD3E018EE03B02B6DF1EF07D0025DB49CAC74E`。各轮`root-final-authority.json`均Passed：独立13组受控配置一致、偏好原SHA一致、Completed、fields0/pending0/unresolved0、GUI0、113服务Auto/Running。恢复结果单独记为Passed，不把失败的功能验收改为通过。

## 后续要求

下一诊断用变更前已绑定HWND/PID/创建时间，只读对照IsWindow、IsWindowVisible、原生客户区/外框矩形与AutomationElement.FromHandle、未过滤UIA根候选；不再用Resize补证据。先区分真实隐藏、UIA枚举缺失、Offscreen属性与多候选，再选择验收方式。保留真实手动拖拽、记忆重开、关闭开关忽略保存尺寸及跨DPI未验证项；不得为跨DPI验收切换显示配置。

证据保留于`C:/Users/Administrator/AppData/Local/Temp/XisuraUsableRelease-20261026/feature-completion-r3-settings/`：三个运行目录的`result.json`；run1的`root-recovery.json`；run2/run3的`original-window.json`、`small-before-resize.json`、`owned-transition-*.json`、`preferences-before.bin`、`root-preferences-before-recovery.bin`、`root-preference-recovery.json`、`root-final-authority.json`；`dpi-context-readonly.json`。原件用于归属及恢复追溯，保留在C盘。
