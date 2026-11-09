# Hhub 0.5

正式版本：**0.5**。整理日期：2026-11-03。本文合并替代此前的产品规格、进度和预览版文档。

2026-11-09 后续方向：Hhub 作为产品主体，先确定 [统一插件体系](Plugin-system-v1.md)，再按新合同重写插件。本文保留为既有 0.5 版本说明，不将旧 API 自动视为新体系的稳定 SDK。

**2026-11-04 当前主页与设备范围修正：**主页左侧为选中设备主卡，占约 2/3；右侧为较窄的五行预选列表，占约 1/3，更多外设局部滚动。默认设备列表、计数与场景设备只包含游戏外设：外接键盘、鼠标、耳机、手柄及匹配插件声明的其他游戏外设。内置指针/触控板、内置 USB 输入控制器、摄像头、磁盘、打印机、未确认用途的普通 HID 和内置扬声器不进入默认列表。后文及鼠标 0.2.1 / 0.3 联调记录中的左右布局、上一轮将内置设备放入列表的说明均为历史行为，以本段为准。

插件开发与验收以已确认的 [Hhub 插件开发准则 v1](Plugin-development-v1.md) 为基线。准则描述目标要求，现有实现仍须逐项核对，不代表全部能力已经完成。

2026-11-03 设备分类与音频输出枚举调整见 [通用设备识别规则](Device-identification.md)：区分实体、接口能力与音频端点，取消复合输入的固定鼠标优先级。该说明替代旧版相应分类规则。

图标使用已确认的透明青色 H，统一用于程序、任务栏、托盘与侧栏。PNG 保留透明通道，ICO 包含多种尺寸。

## 主体稳定契约（0.5 / API 1）

本轮可运行构建为 `artifacts/0.5-core/Hhub.App.exe`，不生成或更新安装包。既有用户配置继续使用原数据目录。

### 布局

主体尺寸集中在 `src/Hhub.App/ShellLayout.cs`：展开/收起侧栏 208/52，导航图标 22、文字 14、行高 40，H 图标 28，标题 24、正文 14，标题间距 6、分区间距 16、列表间距 8，场景卡片固定 160×160。均为逻辑像素，交由 Windows 处理 DPI，不整体缩放文字。主体只提供顶栏、侧栏、页面标题与通用容器；专业控件由插件界面模块实现，不按品牌在主体增加分支。

### 协议与兼容

清单保持 schemaVersion=1，当前主体版本为 0.5.0，界面 API 为 1。可选 minimumHostVersion 默认 0.5.0；requiredCapabilities 默认为空。已知能力为 transport.standalone.v1、transport.stdio.v1、page.tiles.v1、page.module.v1、settings.page.v1、navigation.v1、diagnostics.v1、background-activity.v1、device-scenes.v1。不满足最低版本、所需能力或界面 API 时，安装/加载前明确报错，不覆盖原插件。

标准 tiles 页面仅支持 metric/command。复杂的 declarative 页面必须提供 uiAssembly，界面模块文件必须随包存在，uiApiVersion 必须等于 1。富界面模块负责自己的控件和布局，主体不解释各品牌或音频处理逻辑。界面模块使用共享契约，不引用 Hhub.App；模块运行在主体进程，采集/控制仍在独立进程，因此不能宣称 UI DLL 具备进程故障隔离或沙箱权限。

stdio-v1 固定为 UTF-8 JSON Lines：快照 snapshot、命令 command、结果 result、停止 shutdown。命令结果按插件和 requestId 配对；未知消息类型、损坏 JSON、缺失必要字段会关闭故障会话并取消等待命令。新增不兼容消息必须升级协议，不通过 stdout 输出普通日志。旧插件未声明 supportsCommandResults 时，发送完成只表示已派发，不表示设备已生效；新控制插件应提供确认结果。快照时效以主体接收时间计算。

### 运行生命周期

启动策略（按需/常驻/停用）与状态（停止/运行/停止中/故障）分别记录。显示侧栏入口不构成启动需求；打开页面、实际命令、选中设备、有效场景与插件声明的后台活动才构成需求。停用优先。隐藏到托盘释放不可见页面需求，仍有后台活动或场景需求的插件继续运行。

同一插件的启动/写入/停止按生命周期顺序处理，停止会及时取消待确认命令。运行需求在停止期间发生变化时重新处理，不丢弃切页请求。一项插件启动失败不阻塞其他插件。故障插件不会被按需调度无限重启；用户重新打开入口、执行命令或选择重新启动才重试。损坏或不兼容的一个随附插件包不会使整个主体启动失败。

插件页面退出时释放页面订阅、计时器和界面模块持有的资源；模块卸载是请求，不保证立即回收全部内存，插件开发者需要正确实现 Dispose。运行事件通过主体指定的调度器通知界面，后台故障清理不直接访问 WinUI。

隐藏到托盘时清理并分离不可见页面，不在隐藏期间重新加载界面模块；恢复窗口时按当前状态重建。退出程序前使用同一清理路径；某个 UI 模块清理失败仍会尝试卸载其他模块。退出失败时恢复可操作页面，允许用户重试。

### 验收边界

自动检查覆盖并发启动、命令交叉回执、停止与命令竞争、损坏协议/异常退出、显式重试、兼容性拒绝及保留旧版。不操作桌面。用户实机检查：1000×740、1280×820、最大化窗口；100%/125%/150%/200% 缩放；深浅色；侧栏展开/收起；快速切页后回到监测；隐藏/恢复托盘；关闭独立插件面板后按需退出。以上交互与多屏 DPI 仍需截图反馈确认。

## 使用

简体中文安装包适用于 Windows 11 x64。默认安装到 `%LOCALAPPDATA%\Programs\Hhub`，无需管理员权限，创建开始菜单入口，可选桌面快捷方式。随包提供运行组件和监测插件。覆盖安装和卸载会请求正在运行的 Hhub 正常退出。覆盖安装保留已安装的插件与配置。卸载移除安装文件、快捷方式、受管插件和缓存，默认保留用户设置、场景及插件数据。交互卸载可选择同时删除这些数据。静默卸载默认保留数据，显式传入 `/DELETEUSERDATA` 才会同时清理宿主数据和已知旧版插件数据。安装完成后的启动选项默认不勾选。

安装包尚未进行数字签名。已完成发布资源与编译检查，未在开发环境执行 Hhub 安装向导或启动程序。校验文件与安装包同目录，后缀为 `.exe.sha256`。

构建命令为 `./tools/Build-Installer.ps1`，使用 Inno Setup 6.7.3；安装脚本位于 `installer/Hhub.iss`，中文翻译来自 Inno Setup 同版本的 ChineseSimplified.isl，并保留原文件署名。

运行 `artifacts/0.5/Hhub.App.exe`，保留整个目录。适用于 Windows 11 x64，发布包自带运行组件。打开前先在托盘退出旧版。关闭窗口隐藏到托盘，托盘菜单可恢复或退出；重复打开当前版本会请求显示已有窗口。

用户数据位于 `%LOCALAPPDATA%\Hhub`，监测插件配置位于 `%LOCALAPPDATA%\Hhub.SystemMonitor`。启动时自动安装缺失的随附监测插件，或升级已选监测插件的较旧版本，保留停用和启动模式设置；不更新没有随附包的第三方插件。插件独立维护版本号，当前随附监测插件为 0.4.3。主页不再显示启用/更新监测按钮。

## 架构与范围

采用 C#、.NET 10、WinUI 3。插件有独立可执行入口，不引用主体程序集；新增插件无需修改主体。

| 模块 | 主体负责 | 插件负责 |
| --- | --- | --- |
| 系统 | 数据展示、时效与调度 | 采集、转换、聚合、分析 |
| 设备 | 发现、粗分类、端口、实例身份与匹配 | 精确名称、图片、电量、控制 |
| 插件 | 安装、启停与通用声明页面 | 独立功能及能力声明 |
| 场景 | 前后台识别、优先级、绑定与命令派发 | 执行配置与动作 |

VID/PID 匹配产品，每个设备实例另有身份。同型号设备分别保留；换端口后的稳定归组待完善。部分设备可能没有 VID/PID。

已实现主体和真实系统监测；音频处理与 EQ 由独立音频插件提供。鼠标控制新增 VT3S MAX 插件 0.2.0，界面内嵌于主体内容区，待目标设备复测。其他设备控制、遥控、训练、在线市场及主题资源包尚未实现。市场计划采用独立目录和发布后台，GitHub 可作为开发者下载来源。

## 界面与场景

- Windows 系统/浅色/深色主题、统一背景、共享文字样式与半透明选项。H 图标展开或收起侧栏。
- 系统、设备标题可进入详情。监测只用数字。设备列表五行视口，更多设备局部滚动，主设备来自实际列表选择。
- 设备默认无配置，参数由插件声明。场景选择共享状态，添加场景在当前界面完成。
- 单击场景切换并锁定，双击进入详情，展示名称、设备配置、插件动作和应用识别。匹配规则不代表已赢得优先级。
- 所有插件支持按需、常驻或停用。页面、选中设备、当前场景和执行中的命令产生需求。隐藏窗口释放不可见页面的需求，持续场景仍保留。
- 设置提供主题、半透明、侧栏及监测偏好。用边框和必要提示表示错误，避免额外状态按钮。

## 系统监测

采集和处理在独立插件中，使用 LibreHardwareMonitorLib 0.9.6 和 Windows 接口，主体只格式化与展示。缺失或过期数据显示“—”，不补造读数。

CPU/GPU 展示可获得的名称、负载、频率和温度。网络速率由网卡计数差值计算。物理磁盘多行显示容量与温度，分区数据独立保留，不能把盘温盲目关联到盘符。

内存展示利用率、同一行的已使用/可用、频率和温度。频率来自 Windows 配置频率，模块频率不同则汇总未知。温度取 DIMM 实际传感器最高读数，仅在已有 PawnIO 时读取，不安装驱动。缺少传感器或有效读数时保持未知，不以 CPU 温度代替。

参考：[Windows 物理内存字段](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-physicalmemory)、[LibreHardwareMonitor DIMM 实现](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/v0.9.6/LibreHardwareMonitorLib/Hardware/Memory/DimmMemory.cs)。

## 安装与边界

功能型插件（category 为 utility）安装后自动出现在侧栏主导航下方；其他类型可通过可选 navigation 清单声明加入。navigation 支持 title、glyph（Segoe Fluent Icons 字形）和 command（可选的无参数打开命令）。点击入口打开插件声明页，若声明 command 则发送该命令打开独立面板；仅打开页面或执行命令时产生运行需求。侧栏入口不会令插件常驻，停用时置灰，卸载后移除。旧 utility 插件未声明导航信息时使用名称和默认图标。

随附音频与 Windows 游戏设置插件在独立面板打开时报告 backgroundActivity，防止主体切换页面或隐藏到托盘后提前停止插件；面板关闭且无其他需求时释放运行需求。该声明由插件提供，主体无需识别具体面板。

插件页执行带参数命令前使用通用参数表单，验证后才发送；页面命令必须在清单中声明。设备场景配置按插件和资源保存，新增 DPI、回报率等不同资源不会互相覆盖，同资源/互斥命令才替换已有值，再次编辑读取已保存参数。

插件页的市场和管理区域支持拖入单个 `.hhub` / `.zip` 插件包。拖入时显示放置框，离开或放下后恢复；无常驻新按钮。拖放与本地选择文件使用同一安装流程，已有同 ID 插件先确认替换，安装后进入管理列表。文件夹、其他类型、无效清单和损坏包会显示错误；安装不会因拖放而额外运行插件。

系统详情和插件详情顶部显示插件提供的采集说明，可在当前卡片内展开原因、影响和处理方式；不放在主页，不使用额外状态按钮。缺少 PawnIO 时提供官网链接与安装后重启插件的说明；已安装但缺少读数时区分硬件/虚拟机/权限等可能性，不承诺装驱动就能获得所有读数。Hhub 不自动下载、安装驱动或修改系统安全设置。

插件快照可提供 `diagnostics` 数组，每项包含 id、title、message，可选 helpUrl/helpText。主体通用渲染，仅将 HTTPS 地址作为可点击的帮助链接；主体不根据具体插件名称判断驱动依赖。无诊断或恢复正常时自动隐藏，重复快照保留展开状态。

本地插件使用 ZIP/.hhub 包，安装前检查清单和路径，更新先验证并备份。独立进程和权限声明不等于 Windows 沙箱，当前没有在线签名审核服务。

停止采集后的缓存电量不代表实时电量，进程存在不代表正在播放音乐。具体硬件控制需插件实现。

## 开发与验证

需要 .NET 10 SDK：

```powershell
dotnet build Hhub.slnx -c Release -p:Platform=x64
dotnet run --project tests/Hhub.Core.Tests -c Release
dotnet run --project tests/Hhub.Platform.Tests -c Release -p:Platform=x64
./tools/Build-Preview.ps1
```

默认发布到 artifacts/0.5，产品版本 0.5，内部文件版本 0.5.0.0。插件与协议版本独立管理。

2026-11-02 本轮复核核心 85 项、Windows 平台 6 项检查通过。物理 DIMM 温度、主题切换、双击场景及托盘交互仍需用户实机验收；控制台检查不替代交互验证。开发过程不操作桌面，不自动启动界面。

后续优先处理实机反馈、完善插件能力与在线目录。

## 场景详情布局

页面与分区标题统一为 24，标题下方紧接相关内容。详情标题为“场景 · 日常”等。顶部“应用识别”在当前卡片内展开 Windows 运行进程列表，支持搜索与多选，勾选即时保存并启用自动切换，不弹出独立选择窗口。应用处于前台时触发绑定场景；改绑到新场景时替换旧的前台场景关联，后台规则保持不变。退出匹配应用后按现有优先级及默认场景恢复。

下方“设备”列出本场景的设备绑定、插件动作与参数；其他已连接设备显示无配置，断开设备的已有绑定继续保留。全局插件动作单列，避免混入设备绑定。

场景卡片右键菜单、场景详情的“更多”菜单支持删除场景，确认后清理其应用规则及设备绑定；删除当前场景时回到剩余的日常场景或第一个场景，至少保留一个场景。

侧栏展开宽 208、收起宽 52，导航图标 22、文字 14、行高至少 40，H 标识 28，保持中心线对齐。卡片参照 Windows 设置，深色填充 #2B2B2B、浅色填充 #FBFBFB，使用细描边和 4 圆角。设备、插件、设置行统一图标列、文字间距与纵向对齐，列表间距 8，图标区不另加底色。主页数量显示为“6 个外设”等。

侧栏同时设置 WinUI 模板内部的图标容器高度和项目最小高度，避免内部仍按 16/36 的默认尺寸缩放图标及测量底部入口。

主页设备区恢复左侧详情、右侧五行列表的等高布局，随窗口可用高度调整，更多设备局部滚动。详情设备名为 18，页面标题保持 24。系统卡片缺失传感器仅显示“—”及悬停提示，采集进程报告错误时才使用红框；磁盘改为每盘一行：名称、已用 / 总容量、温度数值，不显示“温度”标签。

## 独立插件协议 v1

新增独立的 **Windows 游戏设置 0.2.1** 插件（`hhub.windows-settings`）：关闭鼠标加速度、关闭连按 Shift / 长按右 Shift 激活快捷键，并可逐项恢复原值；提供游戏模式、录制、图形、刷新率和电源设置入口。停止与卸载不会自动恢复，操作前备份位于 `%LOCALAPPDATA%\Hhub.WindowsSettings`。安装包为 `artifacts/hhub.windows-settings-0.2.1.hhub`；运行 `tools/Build-WindowsSettings.ps1` 可独立构建，完整预览构建也会随附。详见 [插件说明](../plugins/Hhub.WindowsSettings/README.md) 与 [YuqiEngine 分析](YuqiEngine-windows-settings-analysis.md)。

包根目录包含 plugin.json。包为 ZIP，可使用 .hhub 后缀。id 仅允许小写字母、数字、点和连字符。每个 id 保留一个安装版本；更新先验证新包并备份旧版。

```json
{
  "schemaVersion": 1,
  "id": "author.tool",
  "name": "工具名称",
  "version": "1.0.0",
  "author": "作者",
  "description": "列表中显示的插件功能名称或简短描述",
  "category": "utility",
  "entryPoint": "Tool.exe",
  "transport": "stdio-v1",
  "arguments": [],
  "permissions": [],
  "commands": [{ "id": "apply", "name": "应用", "resource": "tool.configuration" }],
  "deviceMatches": [{ "vid": "24AE", "pid": "0000" }],
  "page": {
    "title": "工具名称",
    "elements": [
      { "type": "metric", "label": "状态", "key": "status" },
      { "type": "command", "label": "应用", "command": "apply" }
    ]
  }
}
```

VID/PID 仅为格式示例，不是目标鼠标实际标识。category 可为 device、monitor、utility、theme，主体不按具体品牌写分支。

## 独立入口

entryPoint 无参数时应能独立工作。standalone 模式只使用 arguments，由插件展示自己的窗口。stdio-v1 模式追加 --hhub-stdio，通过 UTF-8 JSON Lines 通信；stdout 仅输出协议，诊断写 stderr。无需引用 Hhub SDK。

主体发送命令：

```json
{"type":"command","requestId":"随机标识","command":"apply","values":{}}
```

插件报告指标：

```json
{"type":"snapshot","values":{"status":"就绪","temperature":45.2}}
```

主体停止请求：

```json
{"type":"shutdown"}
```

先请求停止，超时后终止主体启动的进程树；不接管外部独立实例。设备命令默认派发只表示已发送；插件可声明 supportsCommandResults=true，主体会等待同 requestId 的结果，15 秒内未收到结果则报超时。

```json
{"type":"result","requestId":"请求标识","success":true}
{"type":"result","requestId":"请求标识","success":false,"error":"失败原因"}
```

插件只能确认自身实际完成的工作，不能将发送设备报文直接声明为设备生效。

## 页面激活与数据时效

- activationPages：可选页面名数组，当前使用 home、system 等路由名。主体按页面需求启动，用户停用优先。
- supportsCommandResults：可选，默认 false，兼容旧插件。
- snapshotMaxAgeSeconds：数据接收有效期，默认 10 秒，允许 1–3600 秒。
- 快照可包含 capturedAtUtc；主体独立记录接收时间，以接收时间判断超时，避免信任插件时钟。
- 安装包元数据可在不执行代码的情况下读取，随附目录只提供真实存在的 .hhub 包。

监测标准资源：monitor.refresh、monitor.collection.pause、monitor.collection.resume、monitor.interval（seconds 参数）、monitor.network（id 参数）。主体根据 resource 查找插件的命令 ID，不绑定特定插件名称。

## 声明页面

首轮支持 metric 和 command，主体统一自适应网格。未知类型显式报不支持。后续按协议版本增加曲线、热点、表单等声明能力。复杂自定义界面可先通过 standalone 独立窗口运行。

## 主页标准数据

监测插件的 values 可以提供以下可选字段。字段不存在时主体显示“—”，不会自己采集数据。数值单位由协议固定，格式化在展示层完成，不承担传感器计算。

| 字段 | 含义 / 单位 |
| --- | --- |
| cpu.name / gpu.name | 名称字符串 |
| cpu.load / gpu.load / gpu.memoryLoad / memory.load | 百分比 0–100 |
| cpu.clock | GHz |
| gpu.clock | MHz |
| cpu.cores | 核心数 |
| cpu.temperature / gpu.temperature | 摄氏度 |
| memory.total / memory.used / memory.available / storage.used | GB（插件统一后的展示单位） |
| network.download / network.upload | Mbps |

设备插件可以在 values.devices 提供数组，逐实例补充信息：

```json
{"type":"snapshot","values":{"devices":[{"instanceId":"系统提供的原始实例标识","name":"插件提供的名称","connection":"2.4GHz","battery":68}]}}
```

插件须保留 instanceId，避免两个相同型号互相覆盖。上述内容仅定义数据格式，不表示已实现电量读取或设备控制。

## 设备匹配与命令参数

- deviceNames：搜索用的产品名称/别名数组。
- deviceMatches：vid、pid、category、connection 为可选条件；同一条规则的已填条件需全部符合，规则之间任选一条符合。空规则不匹配。
- 从设备上下文或设备场景下发命令时，values.deviceInstanceId 是主体提供的实际设备实例。插件应在执行前核对设备身份，不能把 VID/PID 当作唯一实例。
- commands[].conflictResource：可选，同一场景内互斥操作共享资源名，例如暂停/继续使用 monitor.collection。
- commands[].parameters：可选，声明 key、name、type（number/string/boolean）、minimum、maximum、defaultValue。主体通用渲染表单，不针对插件写专用参数代码。

示例：

```json
{"id":"setInterval","name":"设置刷新间隔","resource":"monitor.interval","parameters":[{"key":"seconds","name":"刷新间隔（秒）","type":"number","minimum":1,"maximum":10,"defaultValue":1}]}
```

旧清单仍按 schemaVersion=1 解析，新增字段都有默认值。插件业务执行和参数验证仍由插件自己负责。

## 设备配置与多磁盘

- `commands[].sceneConfigurable` 默认为 false。设备插件显式设为 true 后，该命令才进入设备场景配置表单；还需要 stdio-v1 和设备匹配条件。普通查询、电量刷新等命令不会自动变成设备配置。
- 所有类别的插件都支持用户选择按需启动。需求来自 activationPages、打开的插件、选中设备、当前场景和执行中的命令，不要求修改主体来接入新插件。
- `storage.disks` 是物理磁盘数组，每项包含 `id`、`name`、`total`（GiB 数值，界面沿用 GB 标签）、`temperature`（°C，可为 null）。`id` 为物理路径；不可用时可用插件提供的稳定硬件标识。
- `storage.volumes` 仍是分区数组，保留 `id`、`name`、`total`、`used`、`available`、`load`；不把物理盘温度盲目关联到盘符。

```json
{"id":"setDpi","name":"DPI","resource":"mouse.dpi","sceneConfigurable":true,"parameters":[{"key":"dpi","name":"DPI","type":"number","minimum":100,"maximum":26000,"defaultValue":800}]}
```

此例只说明插件能力声明，不表示已实现鼠标控制。

鼠标插件的参考项目分析与准备记录见 [鼠标插件开发准备](Mouse-plugin-preparation.md)。当前 **雷柏 VT3S MAX（有线 / 2.4G）0.3.0** 保留 Feature 解析修正，主页改为左设备列表、右设备主卡；主卡依次显示名称、具体编号、连接方式及有效电量，右边为透明底单只鼠标本体示意图。宣传海报已删除，无电量读数时隐藏整行。点击设备卡直接进入内嵌 WebUI，设备插件不创建侧栏入口；侧栏扩展入口用于功能型应用。更新包为 `artifacts/Hhub-0.5-Mouse-0.3.0.zip`（含新版主体和插件包）；必须同时更新主体，再安装 `hhub.mouse-0.3.0.hhub`。使用见 [鼠标插件说明](../plugins/Hhub.Mouse/README.md)，通用开发接口见 [插件 WebUI](Plugin-WebUI.md)。实际硬件效果仍待用户复测。

0.3.0 按原生驱动截图增加八个设置分区与二级分页，左侧连接状态、右侧名称和有效电量，页面不滚动。可调节 DPI 档位表、已确认的按键映射、性能与传感器参数、电源等；板载宏、固件刷写及未确认动作明确标识支持限制。检查与实测范围见 [0.3.0 验证记录](Mouse-plugin-0.3-validation.md)。历史入口修正见 [0.2.1 验证记录](Mouse-plugin-0.2.1-validation.md)。

## 内存字段（监测插件 0.4.1）

### 2026-11-03 实机反馈调整：监测 0.4.4 与外设归并

本轮由主体对话负责系统监测与外设识别；鼠标通信、FXSound 和它们的界面没有修改。未生成安装程序。

- CPU 摘要用功耗替换核心数，单位 W；GPU 增加功耗，显存使用由百分比改为已用 GB。旧 `cpu.cores`、`gpu.memoryLoad` 数据保留兼容，但不再用于摘要这两个位置。
- 新字段 `cpu.power`、`gpu.power` 为实际传感器瓦数；优先 CPU/GPU Package，不把 TDP、百分比负载、核心局部功耗当成整包功耗。没有对应传感器时返回 null。
- `gpu.memoryUsed`、`gpu.memoryTotal` 为 GiB 数值，沿用全软件 GB 标签。采集库 SmallData 的 MiB 除以 1024，Data 的 GiB 原值保留；不从百分比猜容量，不混入共享系统内存。
- CPU/GPU 型号单列换行，避免与标题挤在同一行。主页和系统详情共用同一摘要组件。
- 遍历子硬件采集 DIMM 等实际传感器，磁盘温度优先 Temperature / Composite，再读取其他可用温度。每块盘仍独立显示已用 / 总容量和温度。
- `monitor.pawnIoInstalled` 只代表安装记录；新增 `monitor.pawnIoAccessible` 和可空的 `monitor.pawnIoAccessError`，通过打开驱动句柄检查访问，不发送额外模块或 IOCTL。能打开驱动不代表所有采集模块与硬件都受支持。`monitor.lowLevelAvailable` 改为是否取得 CPU 温度或功耗读数，不再直接照抄安装记录。
- `monitor.missingSensors` 列出当前缺失指标。在系统详情提示依赖、驱动访问或传感器未提供读数；主页不增加提示按钮。

外设数量现在统计归并后的连接实体，不再直接统计 Windows PnP 节点：

- 同一 USB 物理父节点的鼠标、键盘、厂商 HID 集合合为一项。蓝牙优先实际 DEV 父节点，缺少该节点时以有效 ContainerId 辅助归并。
- 不以 VID/PID 或型号相同作为归并依据。两个同型号接收器插在不同端口仍为两项；一个复合接收器提供鼠标和键盘接口时显示一项连接实体，其分类能力保留两种。
- 默认排除远程桌面、Hyper-V 等虚拟输入、单纯软件音频端点、USB 控制器/集线器和蓝牙服务节点。保留有 ACPI/I2C 父节点的内置键盘、触控输入，以及 USB/蓝牙输入、音频、USB 摄像头、磁盘和打印机。
- 名称优先 Windows 总线报告的产品描述；没有有效描述时用类别与 VID:PID 辅助辨认，具体产品名称、电量及能力仍可由插件提供。
- 列表显示已取得的 USB 端口位置，详情保留完整连接路径、所有接口和关联实例。端口是 Windows 报告的拓扑位置，不能保证等同于机身印刷编号；缺失时不编造位置。
- `DeviceRecord` 新增 `MemberInstanceIds`、`InterfacePaths`、`Categories`、`LocationPath`。保留一个原设备实例作为主 ID；插件上报任一关联实例都能补充这一项。原场景绑定和主页选择也按关联实例匹配。复合设备次分类仍参与插件匹配。

边界：一个接收器实际配对多个外设时，Windows 通用枚举通常只能确认接收器实体；配对对象的拆分与命名需插件提供，主体不推测数量。驱动访问受限、没有 DIMM 温度传感器、磁盘控制器不暴露温度等情况仍可能显示“—”；本轮没有安装驱动，也没有目标实机传感器验证。

依据：[Windows 连接位置属性](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/devpkey-device-locationpaths)、[总线报告的设备描述](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/devpkey-device-busreporteddevicedesc)、[LHM 0.9.6 NVIDIA 传感器](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/v0.9.6/LibreHardwareMonitorLib/Hardware/Gpu/NvidiaGpu.cs)、[DIMM 实际传感器与限制值](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/v0.9.6/LibreHardwareMonitorLib/Hardware/Memory/DimmMemory.cs)。

- `memory.clock`：Windows 报告的配置频率 MHz，可为 null；来源说明在 `memory.clockSource`。
- `memory.modules`：模块 name、clock、total 数组，各模块 clock 单位 MHz、total 单位 GiB。
- `memory.temperature`：DIMM 实际读数的最高温度 °C，可为 null。不能用温度上限、分辨率、CPU 温度填充此字段。
- `memory.used` 与 `memory.available` 保持原字段，展示层合并在同一行。


场景卡片、添加入口和内联创建卡固定为 160×160 逻辑像素的正方形，不随窗口拉伸；图标与名称上下居中排列，数量超过可用宽度时局部横向滚动。

监测插件 0.4.3 在 storage.disks 增加可选 used 字段（GiB），通过 Windows 物理盘—分区—逻辑卷关联汇总已用空间；跨物理盘卷或缺少映射时返回 null。该数值是可读取逻辑卷的文件系统已用空间，不包含未挂载分区的未知占用。旧插件缺少字段时界面显示“—”，不当作零。参考：[Windows 磁盘关联](https://devblogs.microsoft.com/scripting/how-can-i-correlate-logical-drives-and-physical-disks/)。

## 音频插件 0.1.1

`hhub.audio` 是具有实际音频处理能力的独立插件，内置原生 FxSound DSP，无需 FxSound.exe。插件提供 5/10/15/20/31 段 EQ、中心频率、五类音效、总增益、平衡、Q 倍率、旁路及预设。面板参考 FxSound 布局，采用 Hhub 青色、卡片和浅深色主题。

从随附目录安装插件后进入侧栏 FXsound。名称后是总开关；预设、音效、EQ 和输出设备均在同一功能页面调节，使用主体的卡片、字号和控件。页面按窗口自适应，不滚动，较多 EQ 频段自动分行。首次使用可由用户在面板中安装随附、保持原始签名的虚拟音频驱动；构建与测试不自动安装驱动或切换默认输出。实时链路、设备切换与听感仍需用户实机验收。详细说明见 [音频插件文档](../plugins/Hhub.Audio/README.md)。

协议增加兼容字段：`supportsBackgroundActivity` 默认 false，声明后插件可在新鲜快照中报告布尔值 `backgroundActivity=true`，将持续会话纳入按需启动需求；停用模式仍优先。`shutdownTimeoutSeconds` 默认 2.5，可取 1–30 秒；音频插件使用 8 秒完成路由恢复和音频线程清理。


本轮代码检查修复：参数命令缺少输入表单、设备场景第二项配置覆盖第一项、插件页面/侧栏引用无效命令、音频打包脚本固定使用旧版包名。现按清单读取版本与导航元数据；旧版功能插件保留默认侧栏入口，更新至随附音频 0.1.1 / Windows 游戏设置 0.2.1 后可使用专属图标和独立面板打开命令。

## Windows 设置插件局部刷新（2026-11-03）

Windows 游戏设置 0.3.0 已迁移至插件自带的内嵌 WinUI 模块，移除独立窗口与外部设置跳转。当前三项真实开关使用原生 API 持久更改；正常无额外说明，错误在对应行显示。更改后只回读受影响选项，缓存和控件按行版本更新，不重建 ScrollViewer。背景轻量轮询每次仅读取一项。

主体收到运行状态、命令回执或设备变更时保留打开的插件页，无关设备属性变更不再触发完整枚举。设置插件 53 项无界面检查通过。用户要求不操作桌面，未打开主程序或系统设置、未更改真实参数；截图与滚动实机验收待用户提供。单独主程序构建位于 `artifacts/0.5-windows-settings`，插件包 `artifacts/hhub.windows-settings-0.3.0.hhub`。全解决方案检查受到监测插件 HardwareSampler.cs 的 PawnIo.IsLoaded 编译错误阻挡，Windows 设置插件与主程序可单独构建。

2026-11-03 本轮验证：154 项核心/监测协议检查通过（包括外设复合节点归并、同型号设备分离、别名兼容、MiB/GiB 换算、驱动访问诊断及真实监测进程生命周期）；平台枚举、热插拔观察器和进程识别检查通过。WinUI 主体与监测插件发布成功。当前虚拟机默认外设列表为 0，远程/虚拟节点已排除；这不能代表实机枚举与传感器验证完成。

免安装输出：`artifacts/0.5-monitor-devices/Hhub.App.exe`，随附 `hhub.system-monitor-0.4.4.hhub`。运行或复制到其他电脑时需要整个 `artifacts/0.5-monitor-devices` 文件夹；不只复制 EXE。原先安装程序与实测 ZIP 未覆盖，避免混淆旧版与新版。

## .NET 共享插件运行环境（2026-11-04）

通用能力 `runtime.dotnet10.shared.v1` 已实现。主体发布会自动携带通用 `Hhub.PluginHost` 入口，其 4 个入口文件复用主体目录的 .NET 10，不另外复制一套运行库。声明该能力的插件使用 stdio-v1 与 DLL entryPoint，在独立进程中运行；原 EXE 插件继续兼容。UI 模块沿用主体的 WinUI 和公共契约。旧主体不认识新能力时拒绝安装；新主体缺少通用入口时报错并标记故障。

Windows 游戏设置 0.3.1 只打包业务 DLL、deps.json、UI DLL、清单和说明共 5 个文件，不含独立 EXE、.NET、WinUI、公共程序集和调试符号。功能范围仍是 3 项真实设置，未扩充功能数量。新主体构建 `artifacts/0.5-shared-runtime`，插件包 `artifacts/hhub.windows-settings-0.3.1.hhub`。本轮插件 61 项无界面检查和主体 161 项核心检查通过；共享进程在 PATH 为空、DOTNET_ROOT 无效时完成真实只读通信，未操作桌面或修改真实 Windows 设置。其他历史插件需分别迁移，不能据此宣称全部插件已精简。

## 2026-11-04 系统监测 0.5.0

CPU 利用率下新增“核心 / 线程”一行，分别读取物理核心数和系统逻辑线程数；保留功耗。GPU 温度改为同一行显示“核心 / 显存温度”，显存使用保持 GB。显存温度只接受 GPU Memory / GPU Memory Junction 传感器，不用核心或热点温度补齐。

CPU 频率优先使用硬件核心传感器平均值；读取不到时回退到 Windows Processor Power Information 的 CurrentMhz。该值是 Windows 电源管理报告值，不能等同于逐核心真实/有效频率，所以界面在回退时标为“系统报告频率”，悬停和系统详情均显示来源。核心/线程来自 Win32_Processor，线程数另有 GetActiveProcessorCount 回退。

监测插件独立进程可在用户主动选择系统页更多菜单“授权读取硬件传感器”后，启动只读采集子进程并请求 UAC。主体窗口不提升权限，不自动安装 PawnIO，不关闭安全功能。采集子进程仅通过当前用户的随机命名管道接收 sample/stop，读取既有驱动与 Windows 指标；离开授权对话、取消、停止或退出时必须清理进程。当前用户与被授权账户不同的情况可能无法通过当前用户管道，须明确失败而不能放宽管道访问控制。授权只提供访问条件，不保证硬件存在对应传感器。

NVIDIA 显存用量和功耗增加独立 NVML 只读回退，按唯一显卡名称确认目标；两个同名显卡无法唯一匹配时不跨显卡填充。功耗由毫瓦换算瓦，显存由字节换算 GiB（界面沿用 GB）。驱动不提供的项目保持 null，不把功耗百分比或 TDP 当作瓦数。没有分发 NVIDIA 驱动或 nvml.dll。

磁盘温度在 LibreHardwareMonitor SMART/NVMe 采集之外增加 Windows Storage Reliability Counter 回退，使用 MSFT_Disk 的完整对象关联，按物理磁盘编号匹配，避免同型号盘混用温度。此查询在后台执行，采样不等待存储提供程序；结果有缓存和有效期，失败只影响该回退。Memory DIMM 温度仍取实际传感器，权限、SMBus、固件或硬件不支持时明确说明原因。

系统详情显示安装版本、实际采集端版本、权限、CPU 频率来源与各项缺失原因，避免只显示一个笼统提示。采集端版本为 0.5.0，新主体随附同版本插件包，通过现有更新流程替换较旧版本。主页不增加权限或驱动按钮。

本轮 179 项核心及真实插件协议检查、11 项监测专项检查通过。专项覆盖频率无驱动回退、核心/线程区分、慢存储提供程序不阻塞、同型号磁盘身份匹配、采集管道、并发启动、连续采样、取消授权和退出无残留。测试未触发真实 UAC、未安装驱动或写入硬件。目标电脑 NVIDIA 功耗/显存、CPU MSR、DIMM 与磁盘温度的实际可用性仍需实机验收，不能把虚拟机测试当作传感器实测成功。

当前交付沿用既有监测 EXE 包部署方式，尚不能据此宣称全部插件已完成《插件开发准则 v1》的共享运行环境迁移；该迁移需按主体公共入口另行验收。

参考：[Windows 报告的 CPU 频率](https://learn.microsoft.com/en-us/windows/win32/power/processor-power-information-str)、[存储可靠性计数器](https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/msft-storagereliabilitycounter)、[磁盘与可靠性计数器关联](https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/msft-disktostoragereliabilitycounter)、[LHM NVIDIA 传感器](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/v0.9.6/LibreHardwareMonitorLib/Hardware/Gpu/NvidiaGpu.cs)。

## Windows 游戏设置 0.4.0（2026-11-04）

在插件内部新增按显示器区分的刷新率、HDR、DRR，以及真实电源计划、GPU 硬件调度、游戏模式、控制器 Game Bar、指针速度及键盘重复参数。单屏通常合计 13 项控制，实际可用性由硬件、驱动、系统和权限决定。内嵌布局、单项回读及共享运行时沿用现有公共接口，本轮不修改主体代码。

刷新率先试用，15 秒确认后保存，由独立保护进程处理超时、断管和退出时的恢复；只修改频率，不改分辨率或屏幕位置，并校验显示器身份。恢复失败明确报告。HDR 区分 HDR/WCG；DRR 先执行不改动系统的配置校验，不以高刷新率推测支持；GPU 调度区分待重启配置，权限不足时禁用而不提升主体。其他选项为持久更改，退出或卸载不自动恢复。

93 项无界面检查通过，新增硬件写入使用模拟后端验证逻辑。当前执行会话没有活动显示器，只验证真实电源与输入读取及只读协议；显示相关实机切换、多屏交互、GPU 调度重启生效尚未验收。插件包为 `artifacts/hhub.windows-settings-0.4.0.hhub`，继续只包含 5 个插件自身文件，不携带 .NET/WinUI 运行库。详细范围及依据见 [插件说明](../plugins/Hhub.WindowsSettings/README.md)。

## Windows 游戏设置 0.4.1

标题右侧通过现有插件 HeaderControls / headerPlacement=right 增加“给予管理员授权”，只在用户点击后请求 Windows UAC。管理员权限限定为插件固定的 GPU 调度读写接口，使用受限 ACL 和双方进程身份校验的通道；不提升主程序，不安装服务，插件会话结束时释放。授权完成只回读受影响的 GPU 调度项，保持其他选项与滚动位置。

显示区合并为“显示”，单屏不重复列显示器标题，多屏保留目标区分。键盘开关改为“连按五次 Shift 开启粘滞键”“长按右 Shift 八秒开启筛选键”，关闭只禁用触发方式。刷新率通道修复为先连接再初始化 AutoFlush，覆盖截图中的 Pipe hasn't been connected yet。

新版包 `artifacts/hhub.windows-settings-0.4.1.hhub`，仍只携带 5 个自身文件。116 项检查覆盖真实本地管道、权限请求状态和取消、迟到授权释放、受限命令及已有功能；没有触发真实 UAC、没有修改本机设置或操作桌面，UAC 和显示修改的实机验收仍由用户完成。

## 设置整理、批量插件安装与采集修正（2026-11-04）

- 全局设置移除重复的插件启动策略和各插件专用设置；管理功能集中于插件页。FXsound 0.7.1 自己提供小齿轮及返回入口，仍显示在主体内的内容区，不修改音频处理算法。
- 应用根层承接插件拖入，文件选择器允许多选。批次先独立校验每个包，同一插件取最高版本；更新汇总确认后依次替换，单个失败继续其余包，完成后统一报告结果。替换前释放嵌入式页面、停止对应后台，避免自己的 UI 模块占用旧包。
- 系统详情的“采集报告”标签承接诊断：左侧为报告列表，右侧为当前选中详情。刷新保留选择，暂时无新快照不清空，后续完整快照确认恢复后保留“已恢复”记录。记录在当前主体会话内保留，不宣称跨重启持久化。
- 监测端 0.5.1 修正 PawnIO 访问掩码：按 LHM 使用 FILE_READ_DATA / FILE_WRITE_DATA，而非额外请求通用访问权限。独立驱动探测失败不再抹去采集库实际提供的正数 CPU 温度、频率或功耗；未确认的零值保持不可用。错误详情显示实际 Win32 原因和编号。
- 磁盘温度在原有 LHM / Windows 可靠性计数器之外增加 StorageDeviceTemperatureProperty 原生只读查询，读取真实温度项，拒绝缺失值和温度阈值，按物理磁盘路径归属；仍在异步采集任务中执行。
- UI 与支持的托管后台统一使用 Hhub.App.exe 的无窗口入口，保留独立进程及正常 shutdown。只在任务管理器查看各进程内存，不增加软件内资源页面。系统实际折叠分组及管理员辅助进程归属需要 Windows 实机确认。

验证：核心测试包含批次预检、重复版本、无效文件隔离、诊断恢复记录；监测测试包含原生描述符解析、阈值区分、异步磁盘回退及采集子进程生命周期。发布主体已用真实监测、鼠标、FXsound、Windows 设置后台执行只读协议及退出检查，没有启动桌面界面、修改设备参数、路由音频或安装驱动。真实 CPU 功耗/温度、DIMM 和磁盘温度能否恢复仍需在用户实机复测，不以虚拟机缺少传感器作为修复成功的证据。

依据：[LHM PawnIO 0.9.6 实现](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/v0.9.6/LibreHardwareMonitorLib/PawnIo/PawnIo.cs)、[Windows NVMe 温度查询](https://learn.microsoft.com/en-us/windows/win32/fileio/working-with-nvme-devices)、[温度描述符](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-storage_temperature_data_descriptor)。

## 监测 0.5.2：授权与实际读取链路（2026-11-04）

此前版本未解决用户实机的 CPU 功耗/温度、DIMM 温度和磁盘温度缺失，不能将包安装检查、协议检查视为传感器验收。本次经用户明确同意，首次启用监测自动请求 Windows UAC，仅提升传感器采集后台，主体和其他插件保持普通权限。

- 默认自动请求采集授权；正在授权或已建立的采集会话声明持续任务，页面切换保留会话。暂停采集时只发布缓存快照及原采集时间，避免停止监测又重新弹 UAC。退出 Hhub、停用插件或关闭设置中的“硬件传感器读取”会释放会话。下一次重新启动仍由 Windows 决定是否要求 UAC，不承诺永久提权。
- 取消 UAC 后关闭自动授权并保存，刷新、切换页面或重启不会自动反复请求；用户可在监测设置中再次启用。
- 不再以卸载注册表项决定是否尝试访问 PawnIO 或枚举 DIMM。内存初始化从 CPU/显卡/磁盘初始化中拆开，内存路径失败不再中止其他类别；其他硬件组初始化失败也不会阻断独立磁盘温度回退。
- 核对 LHM 0.9.6 实现后发现，模块读取失败可以返回全零；CPU 零功耗不作为有效数据，也不将无 MSR 访问时的 TSC 回退频率冒充逐核心传感器频率。
- NVMe 温度新增标准 SMART/Health 日志查询，独立于通用温度属性；校验返回数据长度、协议类型和偏移，再将真实复合温度从开尔文换算为摄氏度。没有使用温度阈值、其他设备温度或估算值。

监测自动化检查覆盖 NVMe 数据解析、授权取消不重试、显式重试、用户设置迁移、独立进程管道与退出；未触发测试环境的真实 UAC 或安装驱动。上述修改仍需用用户实机的授权采集结果验收。若受支持指标仍缺失，应继续依据实际采集结果修复，不能用增加提示代替修复；若 DIMM 本身不提供传感器，则不编造温度。

源码依据：[LHM CPU 读取及回退实现](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/v0.9.6/LibreHardwareMonitorLib/Hardware/Cpu/IntelCpu.cs)、[PawnIO 输出处理](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/v0.9.6/LibreHardwareMonitorLib/PawnIo/PawnIo.cs)、[Windows NVMe 标准查询](https://learn.microsoft.com/en-us/windows/win32/fileio/working-with-nvme-devices)。

## 监测 0.5.3：重复磁盘与显存总量（2026-11-04）

用户实机截图已出现两块 NVMe 的温度，但同一磁盘被列出两遍。原因是 Windows WMI 的 PHYSICALDRIVE 路径与 DiskInfoToolkit 的设备接口路径不同。改用库实际查询到的 DriveNumber 映射系统物理盘编号后合并温度，保留 WMI 的容量和已用数据；同型号不同物理盘编号仍独立显示，不按名称或近似容量合并。

主页及系统页共用的 GPU 摘要改为“显存已用 / 总量”，同时显示 gpu.memoryUsed 和 gpu.memoryTotal，单位均为 GB；缺失的总量显示不可用，不用利用率反推。26 项监测检查通过，新增检查覆盖跨数据源合并、同型号多盘不误合并和有效温度/容量保留。界面未通过桌面自动化操作，实际排版由用户反馈验收。

## 托盘与应用运行行为（2026-11-04）

托盘悬停显示当前外设名称及插件提供的剩余电量，按实例缓存并处理过期、停用与断开。鼠标 0.3.3 声明通用电量刷新入口，其他插件按公开接口接入即可。未知电量显示“—”。用户不必为了查看电量打开设备页。

“设置 → 常规”新增：开机自启动（默认关闭）、启动后进入托盘（对开机启动生效）、最小化到托盘、关闭窗口时收起到托盘/退出应用、托盘效率模式、托盘显示电量。关闭和最小化默认收起到托盘，托盘右键“退出 Hhub”始终完整停止后台。托盘初始化失败时不会把唯一窗口隐藏。自启动在用户明确开启时写入当前用户 Run 项，运行命令包含 `--startup`；免安装目录移动后需重新开启以登记新路径。

隐藏时释放内容页面并关闭该窗口的背景合成，停止页面刷新。主体与普通共享后台进入实际 EcoQoS、降低调度与内存页优先级；存在不同能效类别时通过软 CPU Sets 优先小核，无大小核时保持系统调度。音频等延迟敏感任务保留正常调度。恢复窗口时恢复优先级与背景，不承诺固定内存降幅或所有进程必须只在小核执行。

卡片改为随深浅色变化的半透明叠层填充与细边框，移除原先不透明深色卡片填充；原有 Mica 背景仍由 Windows 提供，并服从系统透明效果设置。

验证涵盖真实 EcoQoS 开关及恢复、后台信号观察、大小核选择规则、测试注册表键下的启动注册/撤销、电量实例隔离与过期、原生托盘 ABI 和图标加载。没有操作桌面、启用本机实际自启动或改变用户已有软件进程的调度；悬停外观、实际开机启动、最小化/关闭行为及小核分配仍需用户实机界面验收。

依据：[EcoQoS 与效率模式](https://devblogs.microsoft.com/performance-diagnostics/reduce-process-interference-with-task-manager-efficiency-mode/)、[CPU Sets 软调度](https://learn.microsoft.com/en-us/windows/win32/procthread/cpu-sets)、[EfficiencyClass 定义](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-system_cpu_set_information)、[通知区域图标与自定义提示](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/ns-shellapi-notifyicondataw)。

## 拖放安装与界面闪烁修复（2026-11-04）

文件拖放由主体统一接收，覆盖内嵌 WebUI 区域；支持同时拖入多个本地 .hhub / .zip 文件。拖放提示改为透明输入层和小型主题卡片，不再用启动时的浅色填充覆盖全窗口。移入子控件不再反复隐藏提示层。浏览器初始化和销毁时隐藏尚未就绪的画面，避免默认白色表面闪现。

安装不相关插件时保留当前页面；更新当前打开的插件时才离开其界面。UI 模块和同目录资源先复制到按内容摘要隔离的用户缓存，再加载副本，避免原安装目录的 DLL 被界面锁住；取消安装前阻塞 UI 的强制 GC。插件通过 Assembly.Location 查找同目录资源的方式保持有效。

验证：185 项 Core 检查通过，包含 UI 副本资源完整性、旧副本锁定时仍可更新原包、内容变化隔离及相同内容复用。实际 Explorer 拖放和白屏观感仍需用户测试；没有通过桌面自动化操作。主体应普通权限运行，硬件采集后台单独授权；管理员权限主体不能接收普通 Explorer 的跨权限文件拖放。
