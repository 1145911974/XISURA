# GPU-07：113只读功率上限显示复核

2026-10-27，MRID6-23／7745HX／RTX4070 Laptop／V35、EC0.35，已安装1.0.113.0。GPU-07仍Open；本轮没有产品源码修改、构建、硬件数值写入或新增安装包。

## 实际结果

- 系统`nvidia-smi.exe`为有效Catalog签名，签署者Microsoft Windows Hardware Compatibility Publisher，文件公司NVIDIA，版本8.17.16.2101，SHA256 `19DFD1B1CD1E0BB24C720F18252BAFF639A0F967F305DC409470218EE24D039C`。原助手只接受NVIDIA署名而拒绝有效微软硬件目录签名；查明后按实际签名类型核验，没有修改产品信任规则。
- 18:15:45独立只读查询，同一GPU UUID `GPU-603acbd9-d976-5cbc-9f78-1516fd0e4dc8`、驱动621.01：`enforced.power.limit=109.87 W`、`power.max_limit=140 W`，退出码0。
- 已安装113副屏GUI真实显示“当前111 / 最高140 W”。18:17:52.839服务快照为109.18/140 W；UI观察为18:17:52.748。执行上限动态变化，两份异时快照没有精确相等，不能声称同步逐值比对通过。
- 正常退出后独立13组受控配置一致、偏好及自动化配置哈希一致。完整GPU原始快照逐字比较为false，不能据此宣称其全部字段相等。最终Completed、fields0、pending0、unresolved0、GUI0，113服务Automatic/Running。
- GUI使用`--accept-secondary`；未调用输入法切换或鼠标/键盘注入。首次助手缺少依赖，在GUI启动前中止；原目录保留，补齐依赖后run2通过。

## 边界与后续

`enforced.power.limit`表示当前执行上限，`power.max_limit`表示允许最高上限；单位W。它们不证明用户请求的固定功率上限可读、可写或可准确恢复。字段历史依据见[驱动只读调查](2026-10-12-capability-closure/gpu-driver-readonly.md)。不增加功率写入口，不用动态读数或默认值作恢复目标。

本机当前完整查询可用，无增加字段回退的必要证据。静态审查另发现：若执行上限为N/A而最高值可读，Parser保留最高值，但UI仍显示全未知；本机未复现此组合，本轮未修改。后续修复应单独验证这个显示分支，不把NVAPI百分比当W，也不把其他执行字段无条件视为完全相同的别名。

原始证据保留于`C:/Users/Administrator/AppData/Local/Temp/XisuraUsableRelease-20261026/feature-completion-r3-gpu07/`：`smi-authority.json`、`power-query.json`、`evidence-index.json`；`inspect-installed113-run2/`包含UIA、服务快照、独立前后状态、对账和`root-authority.json`。这些文件用于审计和恢复追溯，保留在C盘。
