# HOME-09：Win键既有实物结果与安装113关联

HOME-09闭合：继承用户已确认的基础Win键开关实效，并补齐当前安装版本、生产调用与只读UI状态关联。本轮没有按Win键、安装真实钩子、屏蔽输入、切输入法、修改产品源码或生成新版本。

## 证据及范围

- [2026-10-16基础快捷项报告](2026-10-16-basic-quick-controls.md)第8–9行记录：用户实际关闭后单按左Win不打开开始菜单，开启后恢复，Esc收起且最终开启；当时候选PID17380及路径已登记。[历史台账](2026-10-12-capability-closure/field-acceptance-ledger.md)第35行一致。沿用该明确实物结果，不把本轮静态检查说成重新按键验效。
- 当前安装GUI为1.0.113.0，DLL SHA256 `415D5CD7AAC7BEBF06D4920D01E5024EF6100689DCC33623167BDF5BE40CD88A`；Service DLL SHA256 `69953DE2D78B4B400BC15113F4770601FF3F836AD13A31A94E5ADECD68C136F6`，与113正式交付记录一致。冻结源码`daa4ea5b6254e8f120cafad19592319d27852a30`。
- `WindowsKeyLockController.cs`当前SHA256与113冻结source-lock均为`B2945DE76F4FCDDC5BD0A302F141B88AEE94B6AC30ED5751B89C530582D5B7AA`。本地Git记录最后修改为2026-10-05，当前无diff，作为源码连续性旁证；不把Git历史等同旧候选构建清单。
- 当前`PrototypeWindow.xaml.cs`及`HomeSidebar.xaml.cs`也与113冻结输入同SHA。审查当前实际路径：Sidebar事件→`OnQuickSettingChanged`的WinKey分支→`TrySetEnabled`，依据真实返回状态更新Sidebar/托盘，失败保持原状态；正常退出调用controller.Dispose。开启表示Win键可用，关闭安装本进程低级键盘钩子，只吞左右Win的key-down，不发服务硬件请求。该逻辑不主动选择输入法。
- 113冻结TRX中的`WindowsKeyLockControllerTests.Enabled_state_means_windows_key_is_available`为Passed，验证假委托安装/卸载及状态语义；它不证明物理键实效，实效依据仍为上述用户确认。
- 本轮已安装113的真实副屏UIA（18:17:52，PID37484及创建时间已绑定安装路径）显示可见且Enabled的“Win键已开启”。该次正常退出及前后13组受控配置一致，偏好哈希一致；随后窗口尺寸尝试亦已准确恢复。最终权威检查Completed、fields0/pending0/unresolved0、GUI0。

## 证据限制

10月16日报告所列临时候选及原始只读证据目录当前不存在，也没有找到当日source manifest，因此无法声称当日候选与113二进制逐字相同。`PrototypeWindow`整体经历修改，不声称跨日期整文件一致。本次按“用户既有实物确认＋当前明确相同控制语义＋113冻结源码/安装身份＋只读最终状态”继承基础功能结论；不外推其他应用的热键兼容、左右Win的所有组合键或退出后继续锁键。

新关联证据：`C:/Users/Administrator/AppData/Local/Temp/XisuraUsableRelease-20261026/feature-completion-r4-home09/`内`installed-source-association.json`、`frozen-regression.json`、`root-final-authority.json`。复用实窗证据`feature-completion-r3-gpu07/inspect-installed113-run2/runtime/gpu-current.json`及该轮生命周期对账，避免重复启动或重复按键。证据保留C盘，113正式安装/发布状态不变。
