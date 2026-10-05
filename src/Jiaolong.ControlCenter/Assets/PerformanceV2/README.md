# Performance V2 assets

当前目录不包含从整页效果图裁切的运行时素材。

- CPU 运行边界含动态温度、功耗、频率、电压和连接状态，使用 WinUI/Win2D 原生绘制。
- 卡片、文字、滑轨、输入框、按钮、边框、阴影和光晕保持原生，避免把动态内容烘焙进 PNG。
- 后续只有无法稳定用 XAML/Win2D 表达的静态装饰才可加入；每张 PNG 必须通过 `tools/Visual-Reconstruction/Audit-TransparentAsset.ps1`。
- 素材记录必须包含：已拍板效果图文件名、源矩形、用途、着色规则和缩放方式。
