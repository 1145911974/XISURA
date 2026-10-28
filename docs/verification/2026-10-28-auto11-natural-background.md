# AUTO-11：已有后台应用自然规则的局部验收

2026-10-28；AUTO-11仍Open，79项完成29/剩余50（36.7%），本轮新增闭合0。正式安装/发布仍为1.0.113；本轮使用r8候选Service配对已安装113 GUI，未安装新版本。

## 实际执行及证据

- 00:04:06 +08新鲜准入：V35/EC0.35、Balanced、GPU偏移及127个V/F偏移为0、无Fan plan/强冷；原会话Completed。持久设备基线匹配当前identity、native Balanced；不使用旧V39或Turbo基线。
- 00:12:28–00:13:42：实际GUI带`--accept-secondary`；不调用输入法切换。复用原journal，独占候选与正式服务；不启动替代Explorer、不造负载。
- UI一次保存：已有`explorer.exe`，运行中含后台、Office、`RequireActivity=false`；AC/DC五组映射全部slot2。编译目标均Balanced、四页Follow=false，不带CPU/Windows写入页；未修改活动/温控/驻留/冷却阈值。
- 配置revision58、同service session/epoch的真实诊断记录：Gaming候选Applied → Office应用条件累计**5.9973472秒** → Office Applied，后者operation `fbe748d6-defc-4a02-bac6-418e76bd202b`。最终Office为`SubmittedUnverified`、`CurrentTarget=null`，不伪装为独立实效确认。
- 停用后、真实TrayExit后及候选停止后：分别独立核对13组控制及6个OEM字段与新鲜试前值一致。14份保护配置准确原SHA、V35设备基线SHA不变；会话`6741f2d6-071d-4715-95a3-669b91e86e2f` Completed，所有恢复步骤Verified，无pending/fields/unresolved及残留所有权；候选退出0，GUI0，正式服务恢复Running。三个快照一致不证明观察窗口内绝无瞬时/隐藏写。

## 验收脚本失败与独立复核

原`natural-run1/result.json`保留`Accepted=false`：恢复完成后的诊断解析使用`Where-Object FullName -ceq'service-health.json'`，运行时缺少运算符参数分隔；不是产品失败，也未通过改断言或重演硬件将该运行改绿。原v6脚本、manifest和结果未改。

新增只读`Verify-NaturalRun1.ps1`对同一ZIP核验SHA、session/epoch/revision、规则、编译目标、真实决策链、三个时点独立控制/OEM对账、恢复及当前配置/会话；`independent-natural-run1.json` Passed。Sol只读复审确认上述局部结论，不等同原运行Accepted或AUTO-11完成。113身份由原runner停服务前的路径/GUI及Service SHA检查和恢复记录支持；独立verifier仅直接检查服务Running。

启动前另修两处验收守卫：每轮预热及Toggle前重新检查同模式准入；重启正式服务前核验全部14份原配置及设备基线SHA，部分CAS失败则保持Stopped/NeedsRoot。原值核验和坏SHA注入拒绝均通过；v5清单保留，首次实际执行用v6。

证据根：`C:/Users/Administrator/AppData/Local/Temp/XisuraUsableRelease-20261026/feature-completion-r9-natural-application/`。

- `natural-run1/`：原失败结果、真实运行/恢复/诊断引用及快照。
- `independent-natural-run1.json`：上述独立复核；诊断ZIP SHA `5F161525CD289267DD11FDA94E77C0C62F785CF26675627E6D7F50DFC200B052`。
- `root-final-installed-authority.json`：root最终重新核验113安装GUI/Service SHA、服务路径及Automatic/Running、同Completed会话、GUI0、AUTO/Follow/Keep全off，Passed。
- `pre-execution-guard-fix.json`、`restore-guard-check.json`、`execution-files.v5.csv/v6.csv`：执行前修复与冻结记录。

原始恢复/失败证据及诊断ZIP保留用于追溯，不复制完整发布树；均在C盘。

## 仍缺

`RequireActivity=true`自然活动阈值、应用启动/停止与不匹配反证、跨native模式及其OEM准确恢复、真实硬件页派发均未在本轮覆盖。已有Explorer的持续后台匹配不能替代这些验收。后续先补最小自然活动/启停证据，再集中冻结候选形成正式版本；不重演本轮已证明的同模式路径。
