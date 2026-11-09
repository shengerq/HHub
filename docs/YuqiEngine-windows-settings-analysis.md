# YuqiEngine 分析与 Windows 游戏设置插件

分析日期：2026-11-02。范围：本地 `third_party/YuqiEngine`，重点为 Hhub 可采用的 Windows 游戏设置。静态阅读关键链路，没有编译、运行或执行 YuqiEngine 的系统修改；不将仓库 AUDIT.md 中自述的验证视为本次验证。

## 架构与真实边界

这是 `.NET 8 + WPF` Windows 桌面工程，主要分为 Shell（界面）、Core（优化目录、契约、编排与业务）、Infrastructure.Windows（平台能力）、ModuleContracts 和 Modules（模块加载）。`ModuleLoader` 通过 Assembly.LoadFrom 加载模块并注册到依赖注入容器，是进程内模块机制；Hhub 当前使用独立可执行文件和 stdio-v1，不能直接安装它的 DLL。

`BuiltInOptimizationOperationProvider` 定义并校验 13 个模块、212 个操作。这个数量是目录数量，不等于 212 项均可执行，更不等于有效的游戏性能改进。`OptimizationProviderRuntimeManifest` 按实际运行类型区分 typed executable、managed fail-closed 与 unknown。部分旧操作类已经改为阻断执行的兼容层。

值得借鉴的是 `IChangeCapability` 的 Probe / Read / Prepare / Execute / Restore 生命周期，以及先保存恢复材料、再执行和复核的思路。直接引入整个 Core 会同时引入其业务框架及 NAudio、WMI、服务管理等依赖，不符合本次独立设置插件的范围。

README 明确说明：当前公开源码由更早期工程整理而来，历史 v0.1.1/v0.1.2 安装包和界面不代表这份源码；持续保持助手不在当前源码内。目录名 `source-v0.1.5` 也不能据此解释为完整 v0.1.5 产品。

## 需要筛选的项目

| 源码证据 / 选项 | 判断与本次处理 |
| --- | --- |
| `gaming.mode.enable` 与 `gpu.disable_game_mode_optimizations` | 分别描述启用与关闭游戏模式的方向，写入的值名也不同。不作为同一个可靠状态来源；本插件打开官方游戏模式页。 |
| `gaming.dvr.disable` | 使用机器级 AllowGameDVR 策略，影响范围超过单一后台录制选项。本插件打开录制页由用户选择。 |
| `gpu.enable_hags` | 文案含“降低输入延迟 1–3ms”，源码没有构成跨设备效果证明。进入官方图形页检查支持情况，不全局强制。 |
| `fps.disable_fullscreen_optimizations` | 批量改 GameConfigStore；源码自己提示新游戏、HDR 和覆盖层兼容性可能下降。第一版不加入全局改写。 |
| `cpu.disable_dynamic_tick` / `cpu.configure_hpet` / `cpu.use_platform_tick` | BCD 全局计时设置，源码标记 Extreme；不纳入本插件。 |
| `cpu.force_high_timer_resolution` | 源码明确禁止执行，原因是全局恢复状态无法由单一快照证明；不迁移旧实现。 |
| 关闭 VBS / 安全缓解、批量服务停用、网络“优化”、清内存 | 影响面大或缺少本机收益证据，不纳入第一版。 |
| 鼠标加速度、辅助功能快捷键 | 有官方 Win32 API、可读取当前配置、可逐项恢复；独立重写为本插件的直接操作。 |
| 刷新率、电源、逐游戏 GPU 选择 | 添加官方设置入口，结合设备和游戏实际选择。 |

不能把注册表目标相等当作性能收益证明，也不能把“页面已打开”回执当作某开关已生效。游戏设置需要按游戏进行帧时间、输入手感和功耗复测。

## 实现结果

新增 `plugins/Hhub.WindowsSettings`，插件 ID `hhub.windows-settings`，版本 0.1.0。采用 Hhub 既有通用声明页面，不改主体业务代码。三项直接操作各有恢复按钮，另有五个官方设置入口、刷新与真实状态展示。支持无参数独立控制台和 Hhub stdio 模式。

所有修改先保存首次原值、回读确认；恢复前比较当前值与本插件目标，外部修改时拒绝覆盖；损坏备份阻止修改。辅助功能只关闭激活快捷键，保留功能启用状态、其他标志与筛选时间参数。卸载或停止不自动恢复，数据保留用于重新安装后的恢复。

验证结果：完整 Hhub.slnx Release x64 构建通过，0 警告、0 错误；插件 33 项检查、主体 76 项检查及 Windows 平台检查通过。覆盖恢复机制、真实只读协议和安装包安装，现有 `artifacts/0.5/bundled-plugins` 已加入新包。未操作桌面、未启动设置页面，也未实际写入本机 Windows 参数；真实写入、恢复及界面观感仍需实机验收。

官方依据：[SystemParametersInfo](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-systemparametersinfow)、[STICKYKEYS](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-stickykeys)、[FILTERKEYS](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-filterkeys)、[Windows 设置 URI](https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-settings)。

## 0.2.0 完善

独立入口已改为原生图形窗口，双击即可运行；Hhub 页面新增“打开完整设置窗口”入口，使用同一进程和同一恢复文件。窗口展示设置说明、真实状态、备份与冲突，按状态启用操作按钮，并提供逐项恢复和全部恢复。错误显示红色反馈；操作执行期间阻止重复修改与关闭。

设置服务串行处理窗口和协议请求，防止并发覆盖恢复记录。全部恢复会保留冲突项目并继续其他项，存在失败时回执为失败并说明逐项结果。恢复文件保持版本 1，兼容 0.1.0。

新版 43 项检查通过，覆盖窗口控制层、并发读写、部分恢复、真实安装通信及离屏窗口渲染。已人工核对离屏预览并修复高 DPI 文字裁切。安装包 `artifacts/hhub.windows-settings-0.2.0.hhub` 与 SHA-256 校验文件已生成；现有 0.5 随附目录已更新。真实 Windows 参数仅做读取，实际修改后的体验仍由用户按需验收。
