# XISURA 1.0.110

蛟龙控制中心，Windows 11 x64桌面应用。当前版本 **1.0.110 Stable**；安装包、便携包和SHA-256见 [GitHub Releases](https://github.com/1145911974/XISURA/releases/tag/v1.0.110)。

本版已在MRID6-23／Ryzen 7 7745HX／RTX4070 Laptop／BIOS V35、EC0.35上完成实际启动、托盘、退出和重开验证。其他机型/固件尚无同等验收结论。原71项功能库存并未全部完成；以下支持范围优先于旧版本说明。

## 安装与运行

使用 `XISURA-1.0.110-Stable.msi` 升级或安装，再从桌面/开始菜单打开XISURA。安装包包含.NET10、Windows App SDK和所需VC运行库，无需另装这些运行库；不会安装硬件驱动。GUI需要管理员权限才能连接服务并执行写入。

关闭窗口会驻留托盘；完全退出请使用托盘菜单。默认“退出后保留配置修改”关闭；开启时保留已应用值，完全退出后Follow/AUTO和软件动态灯效均停止，渐变停在末静态帧。

使用过手工测试版或自定义恢复日志目录的机器，升级必须延续原恢复记录和服务参数；不能用新空目录绕过恢复。本机升级验证已保留原记录、用户配置和当前控制值。安装失败保留日志，不手工删除用户配置或journal。

## 当前支持

- 主页已验主模式/快捷项目；可靠CPU/Windows字段、GPU核心/显存偏移与V/F；常亮/渐变灯光、配置/预设、设置和诊断。
- A为实机独立核验；B仅为明确字段/数值范围的可靠写入、读回和恢复，不代表负载效果或稳定性。主页18/18槽是Partial流程观察，不能称全部实效通过。
- 风扇只交付EC自动/固件空闲交还；固定RPM、曲线、联动、最大目标、强冷及风扇预设/跟随写入不支持。
- GPU锁频、任意电压不支持；GPU功率只读。未知OEM CPU字段拒写，其他可靠字段仍可使用。

详细说明见[支持矩阵](docs/verification/2026-10-26-usable-release-support.md)、[安装发布记录](docs/verification/2026-10-27-install-github-release.md)及[后续优化计划](docs/progress/2026-10-27-post-release-roadmap.md)。灯光循环已取消，不列为延期功能。

## 构建

.NET10 SDK、Visual Studio Windows/WinUI及C++构建工具、WiX7；依赖版本由项目锁文件控制。对GUI、Service和Diagnostics发布时必须显式使用 `-c Release -r win-x64 --self-contained true -p:XisuraBuildChannel=Release`。安装包可通过 `dotnet build installer/Jiaolong.Installer/Jiaolong.Installer.wixproj -c Release -p:XisuraBuildChannel=Release` 构建；正常流程会重新发布离线payload。

本次发布沿用已验R3源码，补齐离线运行库；应用核心程序集与R3 SHA一致。原软件回归1358/1358通过、无跳过，安装器另15/15通过；实际安装文件、配置保留和服务生命周期均独立核验。源码冻结清单及构建/运行摘要随Release提供。
