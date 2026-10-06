# 托盘弹出与收纳验证 · 1.0.74

## 原因与修复

副屏验收参数覆盖了快捷控制台的选屏，并永久设置 `IsModalActionPending=true`，屏蔽了失去激活后的收纳。移除这两个运行路径覆盖；真正的模态操作保护保留。

Shell 回调现在保留 `wParam` 中的物理坐标并按有符号 16 位解码，在 UI 投递前捕获。重复同屏通知不会闪烁，跨屏点击会重新定位，正在收纳时同样有效。

依据：[Microsoft NOTIFYICONDATAW version 4 回调坐标](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/ns-shellapi-notifyicondataw)、[WindowActivationState](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.windowactivationstate)。

## 检查

- 新增两项：延迟投递保留鼠标/键盘坐标（含负坐标）；验收参数不能覆盖托盘定位或禁用收纳。
- 客户端 444/444；安装 14/14；MSI 构建 0 错误、3 项既有 WiX 警告。
- 升级安装成功：MSI 返回 0，注册版本 1.0.74.0，服务 Running，GUI 响应正常，客户端/服务 DLL 与新发布内容哈希一致。

## 已安装窗口原生验证

未修改硬件设置；输入只作用于本应用空白区域。通过本进程 Shell 回调传递选屏坐标，读取实际 HWND 边界与透明度，无人工移动窗口。

| 点击锚点 | 实际屏幕 | 物理窗口尺寸 | 内部点击 | 外部点击 |
|---|---|---|---|---|
| DISPLAY1 副屏 | DISPLAY1 | 360×480 | 保持展开 | 淡出并收纳 |
| DISPLAY2 主屏 | DISPLAY2 | 540×720 | 保持展开 | 淡出并收纳 |

外部点击后 120 ms，两屏窗口仍可见且 alpha=244（原为 255）；再等待 450 ms 均隐藏。旧版两屏均不收纳且 alpha=255，主屏锚点仍显示在副屏。

副屏淡出 80 ms 后向主屏发送按钮释放和选择两次通知：最终 DISPLAY2、可见、alpha=255，无旧屏停留。验收主窗口仍使用副屏参数，证明该参数不再影响托盘行为。

## 交付

- `XISURA-1.0.74-x64.msi`：182459744 bytes。
- SHA256：`0bab4d55421e88c7ca005ad3f042e40bde7ab984a5810097ebf08945a5935754`。
- 本机安装回执：`2026-10-06-control-ownership/install74.json`。
