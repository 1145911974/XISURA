# XISURA 当前状态

更新：2026-10-27。唯一当前状态；[目标](USABLE-RELEASE-GOAL.md)，用户最新决定优先。

**功能恢复 3/3：** 接线/软件回归、实机往返/安装、GitHub正式交付均完成；高级EC实效、任意电压等仍为后续库存。用户已批准复用已有/同设备接口、恢复入口后继续优化，不因缺效果遥测统一禁用。恢复、权限及禁测边界仍有效。

**当前本机1.0.111.0／Release。** MSI退出0、无需重启；已装文件匹配最终自包含payload，配置和13组控制未变。安装后GUI两轮启动/托盘/退出重开、Windows服务停止/重启通过；服务Automatic/Running，延续原权威journal。GitHub正式[v1.0.111](https://github.com/1145911974/XISURA/releases/tag/v1.0.111)已发布，5项资产大小/服务端SHA逐项一致；冻结源码`62c61c36a8329ce3900481254daf40f8b5d567c4`，后续main仅同步交付文档。持续授权包含每正式版本机安装及GitHub上传、必要提交/推送，无需再次确认；不夹无关改动、不改仓库可见性。

## 111已完成与限制

- 原生强冷接回OEM method20，独立WMI确认GUI 0→1→0，立即恢复鲜读原0。不依赖高级EC映射；只证明固件开关，不承诺各模式满转速。修复第二次点击被丢弃、同步完成后队列滞留。Keep/Dispose软件覆盖，111原生强冷Keep未另做实机点。
- 恢复固定RPM、曲线、联动、自动上限入口/接口。V35的1060.bit7映射，PawnIO/既有WinRing各4次读回均0；各点2次值写（开启/恢复），准确恢复原0、零转速目标写。本机高级调速尚未生效，不能将开放入口记为完成。
- GPU手动频率上限/解除接回nvidia-smi，权限、审计、丢确认/退出软件路径通过；无当前锁值getter，不伪造读回，手动提交不入自动预设/跟随，不在退出时默认reset。未做数值实机点；任意电压参考为空实现，功率只读。
- CPU/Windows可靠字段、GPU偏移/VF、常亮/渐变灯光、预设、设置、诊断沿用。OEM未知温限/SPL/SPPT单位/恢复未闭合，不阻断独立可靠字段。

## 构建与验收证据

证据根：`C:/Users/Administrator/AppData/Local/Temp/XisuraUsableRelease-20261026/`。

- 最终`unlock111/final-20261027-140212-794`：1368/1368、零失败/跳过；1417项源码前后零变化。GUI/Service/Diagnostics Release自包含发布，版本1.0.111.0。
- `unlock111-lifecycle2`：原生强冷准确往返、两轮生命周期、13组对账Passed，HostExit0。原始result的HardwareTestWrites=false为旧脚本字段；`native-control-summary.json`明确2次OEM写、零EC目标写。
- `unlock111-lifecycle1`原关闭超时及失败保留；cleanup独立确认强冷回0、13组恢复，候选协作退出。完成fanFirmwareRecovery是归档证据，不能删除或一律视为待办。
- `install-publish-111/installation-result.json`及`installed-lifecycle/result.json`均Passed。
- 产物：`artifacts/usable-release-1.0.111-native3`；MSI：`artifacts/install-release-1.0.111/XISURA-1.0.111-Stable.msi`；SHA256 `8E130EF4CEAD3EAE0A3833A73672FB61CC437A7F68C1AC268D701DEAC33A54D2`。
- GUI DLL SHA256 `58B3911A4C770D3CDBF9E3C101CD087C6649A4EE1C6377BDDB550EB1CD799DBE`；Service DLL `89297965A451AA4D219364B9F5EEEC4FEAFD0674059FD9518C2887799B1683EC`。
- [功能/限制/后续方向](../verification/2026-10-27-feature-restoration.md)。110历史证据保持原状；R0 3/3、R1 3/3、R2 3/3、R3 4/4为110历史阶段。原71项仍是后续库存；主页18/18仅Partial，旧阶段4的1/6不是整体百分比。

## 边界与接续

MRID6-23／7745HX／RTX4070 Laptop／V35 BIOS、EC0.35。用户主动重启并要求保留的新状态有效，不写旧65W。数值点立即恢复准确试前值并独立核验，失败/取消同样；Keep不豁免。Keep默认关闭，开启保留已应用静态值；完全退出停止Follow/AUTO及软件动态控制。

不reset/clean，不删原始恢复凭据，不建空journal绕冲突；111凭据不可交110解析，不直接降级启动旧服务。禁游戏/跑分/轨迹/MUX/睡眠/自行重启/EC盲写/驱动安装，不启动109。全部输出C盘。便携1427文件、源码1467文件逐字节核验通过。已清理3份过期111生成候选约1.64GiB，原恢复日志/哈希清单保留；正式资产和110回退包保留。下一步按反馈补V35风扇/OEM/NVIDIA，不将延期功能记为完成。

清理临时合并payload、重复MSI及wixpdb被自动审批以`blocked by policy`拒绝；暂留C盘，不绕过。
