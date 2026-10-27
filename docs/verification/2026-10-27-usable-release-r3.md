# 1.0.110.0 Release R3：首版构建与实机验收

2026-10-27形成可验收正式构建；未安装、未替换本机版本、未对外发布。[支持矩阵](2026-10-26-usable-release-support.md)限定首版范围；原71项及高级功能继续作为后续库存。

## 产物与源码身份

- 目录：`C:/Users/Administrator/Documents/ChatGPT/蛟龙/.worktrees/release108/artifacts/usable-release-1.0.110-20261026-r3`；同名ZIP及独立SHA文件。
- 分支`codex/read-only-field-clarity-110`有既有未提交改动；未提交/推送。以冻结输入1413文件SHA清单定位源码，不用HEAD冒充完整源码身份。
- GUI、Service、Diagnostics均为`1.0.110.0`／AssemblyMetadata channel=`Release`，自包含.NET10；GUI包含Windows App SDK。
- 10测试工程1358/1358通过，0失败、0跳过；三个publish成功。冻结输入在构建前后、实机验收后均一致。
- 实机MRID6-23／AMD7745HX／RTX4070 Laptop／BIOS V35、EC0.35；不外推其他机型/固件。

| 组件 | EXE SHA-256 | DLL SHA-256 |
|---|---|---|
| GUI/Jiaolong.ControlCenter | `C2029EBFF36344555B8F42379A58020636E8546DE5B8652DDAD28221AF3274FA` | `E384CC66AE2FDD4A67C2DC91A572A1C0D2BAB4849E6D7A829E1C31AA6E53CBDD` |
| Service/Jiaolong.Service | `EB0CF2E03095C8AFD8E48D857D00E998747C6DEFCD18B48D1F3E520A49719F88` | `49FCC0BD78524EFE2F3E1F13A2321F64018AB298D36E38F20FDF4A062CA32A3D` |
| Diagnostics/Xisura.ReadOnlyHost | `2BBC6B61C7F744CCF995E50C39970DE9A6AF64E742185B5F984F35CF3C9BE666` | `8FD64EEE6FE4E85307BBA4E519E180C74DE409546DD08616F364AEB0751E1244` |

`source-inputs.csv` SHA-256：`4025F04B121284BC71507EE6D2312A51F9E10CC7E84B2429B118751319EDF4AA`。包内`SHA256SUMS.txt`覆盖全部交付文件；ZIP另附SHA。

## 首版阻断与重启边界

已修EC写前持久化、管理员IPC令牌/权限、Keep决定持久化/Freeze重试、CPU部分字段应用、灯光真实getter及退出停动态。高级风扇/强冷、GPU锁频/任意电压关闭直接及预设/AUTO间接写入。原DeepSeek33失败已独立复现/分类，原始输出保留；未通过Ignore、删除关键断言或永真断言刷绿。

实机发现空闲风扇交还错误返回Unknown，生成五个phantom ownership并卡Closing；现明确NoMutation，不制造恢复义务，真实写入和保留lease仍独立判定。用户于2026-10-26 21:55主动重启，明确要求保留重启后的Turbo/灯光。恢复脚本因跨boot/漂移在Service启动前停止，零硬件写入，未恢复旧Balanced/灯光/EDC或旧65W。

管理员维护入口核对原会话/进程/设备、原字节及SHA、限定五字段与两marker、无同会话真实EC/FW写记录、CAS和两次只读状态，留存用户决定后仅退役无写记录；普通跨boot恢复门槛未放宽。`external-restart-retirement/result.json`：Completed、CurrentUserStatePreserved、Controls13Unchanged、ProtectedConfigUnchanged均true，候选进程0；**OldBaselineRestored=false**，不能称已恢复重启前基线。该路径通过独立静态窄审。

## 正式产物实机结果

| 场景 | 结果 |
|---|---|
| 默认生命周期两轮 | 原journal上启动正式Service/GUI，读取、CPU/GPU/风扇页、托盘隐藏/打开、完全退出、重开均通过。每轮及最终13组控制独立比较一致，受保护配置不变，Service协作退出0。 |
| EC Auto/固件交还 | 第一轮实际GUI按钮通过；本软件未持有自定义风扇目标，无额外控制污染或退出阻断。仅证明空闲交还，不能称高级EC写入/恢复或RPM实效通过。 |
| Keep三会话 | S1开启Keep，Logo true→false，双getter确认，托盘保持/完全退出；S2重开确认false，立即恢复鲜读原值true，独立确认，再Keep退出；S3重开确认true，关闭Keep并默认退出。三会话Completed，KeepRetentionProven=true。 |
| 清理 | Logo原值、preferences及last-applied原字节精确恢复，其他控制/受保护配置一致；ExactConfigurationRestored=true，Error/CleanupError均null，Service退出0。Keep不豁免清理。 |
| 最终状态 | Completed，current/recovery字段0，unresolved=0，候选GUI/Service进程0，旧Service保持Stopped；859份原构建产物、1413份冻结输入零哈希差异。 |

CPU/GPU沿用既有窄B证据，本轮不重复数值点、不宣称负载性能/热稳定性A。主页18/18槽是Partial流程观察，不是全部实效通过。完全退出停止Follow/AUTO；Keep仅保留已应用静态值，渐变停在最后静态帧。

## 原始证据

根目录：`C:/Users/Administrator/AppData/Local/Temp/XisuraUsableRelease-20261026/`。原恢复证据保留，未换空journal。

| 相对路径 | 内容 |
|---|---|
| `final-r3-20261027-004158-896/` | result、10份TRX/日志、3份publish日志、源码前后清单、metadata/EXE SHA、final-integrity |
| `published-lifecycle3/` | result、两轮active/completed、真实IPC能力、GUI交还、逐轮/最终独立比较 |
| `published-keep-logo1/` | result、三会话/recovery、逐点双getter/13组比较、配置字节清理 |
| `external-restart-retirement/` | 用户决定、原journal三文件/SHA、维护日志、鲜读前后比较及退役结果 |
| `restart-retirement-tests3.log` | Keep/Prior-resume定向11通过、无跳过 |

使用说明见交付包根`README.zh-CN.md`，集中实物验收见[清单](2026-10-26-usable-release-user-checklist.md)。高级风扇、锁频/任意电压及未知OEM字段延期；GPU功率只读。完整自动触发组合、跨机型兼容与深入优化留R4。禁游戏/跑分/轨迹/MUX/睡眠/自行重启/EC盲写/驱动安装，不将禁测项目转交用户为必测。安装、替换本机版本及发布等待用户验收。
