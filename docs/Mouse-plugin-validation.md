# 鼠标插件 0.1.0 验证记录

日期：2026-11-03。设备范围：雷柏 VT3S MAX，有线 / 2.4G。硬件实测由用户在另一设备上完成；本轮没有执行真实鼠标设置写入，也没有自动打开桌面面板。

| 检查 | 结果 |
| --- | --- |
| `dotnet build Hhub.slnx -c Release -p:Platform=x64` | 成功，0 警告、0 错误 |
| `tests/Hhub.Core.Tests` | 127 项通过 |
| `tests/Hhub.Mouse.Tests`，含实际插件包安装 | 62 项通过 |
| 发布 EXE 描述符枚举 | 通过，无厂商查询/写入报文 |
| 发布 EXE 的 stdio-v1 | 快照、缺失目标拒绝、未知命令拒绝、关联回执及 shutdown 通过 |
| 对应源码 ZIP 解压后重新构建并运行鼠标离线测试 | 61 项通过（不包含现成二进制包的额外安装检查） |
| .NET 运行组件许可及第三方声明 | 已随二进制包保留 |

离线检查覆盖双设备物理隔离、连接目标解析、错误型号、端点歧义、帧长度/Report ID、Feature 回复关联、Busy/截断帧、XY 与其他档位保留、DPI/回报率修改、首次原值备份、恢复、部分失败、最终状态不一致及无备份/损坏备份阻止写入。模拟传输结果不替代目标固件与实机确认。

交付位于 `artifacts`：`Hhub.Mouse-0.1.0-win-x64.zip`、`hhub.mouse-0.1.0.hhub`、`Hhub.Mouse-0.1.0-source.zip`，各自带 `.sha256`。独立 ZIP 与 .hhub 内容相同，完整解压后运行 `Hhub.Mouse.exe`。

尚待用户验证：两种连接的枚举/型号与端点、实际回读布局、电量与充电标记、DPI 与回报率写入及恢复、休眠/唤醒、拔插、线与接收器同时连接、官方软件争用及窗口交互。遇到失败先刷新实际状态，再在面板导出诊断；诊断包括设备路径、可读序列号、TX/RX、错误与原值备份。

构建脚本：[Build-MousePlugin.ps1](../tools/Build-MousePlugin.ps1)。说明：[插件 README](../plugins/Hhub.Mouse/README.md)。
