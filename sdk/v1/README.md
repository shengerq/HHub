# Hhub 插件 SDK 开发预览

版本：1.0.0-preview.1。实现日期：2026-11-09。这是正在验证的合同子集，不是冻结的第三方稳定 SDK。新主体预览仅支持本文实际列出的操作，不承诺已有旧插件兼容。

## 当前可以运行的内容

- 独立的 Hhub.PluginContracts 数据合同，无 WinUI 或 Hhub.Core 引用。
- C# SDK 的 IPluginBackend 与 PluginApplication。两个类别共用相同接口。
- `.hhub` 包清单 schemaVersion=2，通信标识 `hhub.plugin/1`。
- `hello`、`discover`、`read`、`set`、`apply`、`shutdown`；目标身份、请求与响应关联、按目标独立的 revision、读回、失败恢复和未知状态保护。
- 经过固定项目认证身份校验的 CMS 文件清单签名、包安装和已安装负载重新校验。
- 明确为模拟的鼠标及 EQ 示例；未接入任何真实设备或音频处理。

## 从源码构建

Windows x64，.NET SDK 10.0.403 或 global.json 允许的版本。在 Hhub 目录执行，工作区本地 SDK 路径可以替换为自己的 dotnet：

```powershell
dotnet build src/Hhub.PluginSdk -c Release
dotnet build examples/Hhub.SamplePlugin -c Release
dotnet run --project tests/Hhub.UnifiedHost.Tests -c Release
./tools/Build-UnifiedPreview.ps1 -WithSimulation
```

最后一条命令发布自包含主体、单独生成两个模拟包。不会自动安装它们。测试认证公钥被固定在该次模拟构建，临时私钥不落盘；该构建不是正式认证发行版。不传 WithSimulation 时构建没有项目认证身份的空主体，未知包均拒绝，不能把作者自签证书作为项目认证。

新主体入口和模拟包路径记录在 `artifacts/unified-v1/latest-build.json`。主体使用 `%LOCALAPPDATA%/Hhub-Preview-v1`，测试认证构建再使用其 `test-certification` 子目录，不读取或迁移 `%LOCALAPPDATA%/Hhub` 旧数据。

## 插件入口与后端

### 首页监测服务

首页消费 `home.monitor` 主版本 1。插件在 `provides` 中声明服务、版本和自己支持的字段，并在 `read` 中返回这些能力的值。主体按服务和字段 ID 绑定，不按插件 ID 识别。采集方式、驱动和授权逻辑由插件维护。

| 位置 | 字段与单位 |
| --- | --- |
| CPU | `cpu.name`，`cpu.load`（%），`cpu.cores`，`cpu.threads`，`cpu.clock`（MHz），`cpu.power`（W），`cpu.temperature`（°C） |
| GPU | `gpu.name`，`gpu.load`（%），`gpu.clock`（MHz），`gpu.power`（W），`gpu.temperature`、`gpu.memory.temperature`（°C），`gpu.memory.used`、`gpu.memory.total`（GiB） |
| 内存 | `memory.load`（%），`memory.used`、`memory.total`（GiB），`memory.clock`（MT/s），`memory.temperature`（°C） |
| 网络 | `network.download`、`network.upload`（Mbps） |
| 磁盘 | `storage.disks` 为物理盘清单，元素包含 `name`、`total`（GiB）和 `temperature`（°C，值为 `[number]` 或空数组）。`storage.volumes` 为逻辑卷清单，元素包含 `name`、`used`、`total`（GiB）。 |

设备名称是字符串，数值采样为 `[number]` 或空数组。暂未实现的字段可以省略，首页显示「—」。Lab 可用现有 `--plugin-lab` 入口加载独立测试包来验证该契约。

监测插件可以声明会话级可写布尔能力 `monitor.sensor.authorization.request`。写入 `true` 申请授权，并由插件在 `monitor.sensor.authorization` 报告实际进度和结果。请求值只确认收到申请，授权采集由插件负责。

### 外设发现服务

首页与场景页消费 `devices.discovery` 主版本 1。提供方声明只读能力 `devices.connected`，返回当前连接的外设数组。每项包含稳定的 `id`、显示用的 `name`、`category` 和 `connection`。同一物理外设的接口由提供方归并成一项。没有外设时返回空数组。配置与电量由对应的设备插件提供。

设备插件以同一个实体 `id` 返回设备目标，主体用匹配目标的 `name` 更新设备名称。设备插件可以在签名包根目录提供 `device-page.json`，按 `sections` 和 `rows` 声明设置页。`header` 中的行在所有页签上方可用。分组可用 `selector` 指定选择器名称，每次显示其中一行的编辑器。`illustration` 引用包内的 SVG 或 PNG，也可以在顶层 `artwork` 中通过 `valueFrom` 和 `variants` 声明由单项整数能力选择的图片，取值从 1 开始，并在需要显示图像的分组设置 `showArtwork`。每行引用已声明的能力 ID，由主体渲染标准控件，再通过 `set` 写入并确认回读。缺少当前读数时，该行不可写。

参考 `examples/Hhub.SamplePlugin/Program.cs`。入口检查 `--hhub-plugin-v1`，读取自身程序集旁的 plugin.json，再调用 PluginApplication.RunAsync。共享启动器下 AppContext.BaseDirectory 是主体目录，因此不能用它查找插件资源，应使用自身 Assembly.Location。

实现 IPluginBackend：DiscoverAsync 返回 PluginTarget 数组；ReadAsync(targetId) 读取该目标的完整能力对象，WriteAsync(targetId, capabilityId, value) 写一个完整能力值，BackgroundActivity 表示确有持续任务，DisposeAsync 释放任务与资源。ReadAsync 的值集合与清单能力完全对应。SDK 克隆状态，写入后独立读取确认；失败尝试恢复原能力值并验证完整基线，恢复失败进入未知状态，重新成功读取前拒绝写入。

目标 ID 由插件生成并保持稳定，不能用显示名区分同型号设备。PluginTarget 包含 id、name、kind、connection、online、simulated；kind 与插件类别一致。function 插件也需要声明软件服务目标，不能跳过发现。多个在线目标必须明确选择；断线目标不自动替换，重连后先读再写。无硬件序列号时，开发者必须说明身份限制，不能承诺跨端口和有线/无线身份自动合并。

### 托盘电量服务

Lab 提供 `tray.battery` 1.0。提供方在 `provides` 声明该服务以及电量能力，清单包含 `device.battery.read` 权限，签名包根目录包含独立的 `tray-battery.exe`。Lab 有一个符合条件的提供方时，由 C++ 托盘启动该程序，将托盘 PID 作为唯一参数传入。程序在标准输出发出 4 字节小端无符号整数，0 到 100 表示百分比，101 表示读数不可用。提供方负责处理设备断开、读数过期与托盘退出，主体使用 Windows 托盘提示显示插件名和百分比。

`tray.battery` 1.1 追加插件数据目录作为第二个参数，每帧在电量值后追加 32 字节外设 ID 哈希。读数 102 到 202 表示上次确认的百分比，减去 102 得到原值。主页按外设 ID 匹配该数据。插件可将上次读数缓存在自己的数据目录，设备断开或无法匹配 ID 时发出 101。

真实插件必须把后端替换为实际硬件或软件实现。模拟状态不属于真实读数；报文、目标身份与设备支持范围未知时不得声明已实现。

## 清单与值模型

```json
{
  "schemaVersion": 2,
  "protocol": "hhub.plugin/1",
  "id": "example.tool",
  "name": "示例功能",
  "version": "1.0.0",
  "kind": "function",
  "entryPoint": "Example.dll",
  "capabilities": [
    {"id":"example.enabled","name":"开关","value":{"type":"boolean"},"writable":true,"persistence":"session"}
  ]
}
```

kind 为 device 或 function；版本使用 System.Version 支持的数字形式。DLL 后台由主体共享 .NET 10 启动器运行，EXE 入口独立运行。运行时只读包目录；用户数据路径从 `HHUB_PLUGIN_DATA` 环境变量获得。

值模型支持 boolean、integer、number、string、array、object。对象属性全部必需，拒绝未知属性；数组声明 items 和 maxItems；数值可带 minimum/maximum；字符串有 maxLength。schema 深度不超过 8，单层对象最多 64 属性，数组最多 256 项。该模型是有界子集，不是支持任意外部 JSON Schema。

## 通信示例

UTF-8 JSON Lines，无 BOM，stdout 只输出协议，stderr 仅诊断。当前单行上限 262144 个 .NET char，结构深度上限 16，单请求超时默认 5 秒；请求按会话串行执行。首请求必须握手，再发现目标，随后读取目标建立已确认状态。

```json
{"type":"hello","requestId":"1","protocol":"hhub.plugin/1","pluginId":"example.tool"}
{"type":"result","requestId":"1","protocol":"hhub.plugin/1","pluginId":"example.tool","success":true,"revision":0,"backgroundActivity":false}
{"type":"discover","requestId":"d","protocol":"hhub.plugin/1","pluginId":"example.tool"}
{"type":"result","requestId":"d","protocol":"hhub.plugin/1","pluginId":"example.tool","success":true,"targets":[{"id":"software.default","name":"示例服务","kind":"function","connection":"local","online":true,"simulated":true}]}
{"type":"read","requestId":"2","protocol":"hhub.plugin/1","pluginId":"example.tool","targetId":"software.default"}
{"type":"result","requestId":"2","protocol":"hhub.plugin/1","pluginId":"example.tool","targetId":"software.default","success":true,"revision":0,"values":{"example.enabled":false},"backgroundActivity":false}
{"type":"set","requestId":"3","protocol":"hhub.plugin/1","pluginId":"example.tool","targetId":"software.default","capabilityId":"example.enabled","value":true,"expectedRevision":0}
{"type":"result","requestId":"3","protocol":"hhub.plugin/1","pluginId":"example.tool","targetId":"software.default","success":true,"revision":1,"values":{"example.enabled":true},"backgroundActivity":true}
```

shutdown 不要求响应，后台应释放资源并退出；主体最多等待 2 秒后终止自己的后台进程树。超时或损坏响应使当前会话无效，不自动重放写入。业务拒绝返回稳定 errorCode；当前 SDK 包含协议错误、非法能力值、revision 冲突、验证恢复和未知状态等结果。

当前生成的结构 schema 位于 schemas/。它们由真实 C# 合同导出；还必须运行 SDK 语义校验与一致性检查，不能只凭 JSON schema 通过就宣布插件可用。

## 打包与认证工具

源码示例及私有依赖构建到一个独立目录，包含 plugin.json、业务程序集、deps.json 和必要依赖。不要携带主体业务程序集、重复运行时或私有文件。

```powershell
dotnet run --project tools/Hhub.PluginTool -c Release -- schema sdk/v1/schemas
dotnet run --project tools/Hhub.PluginTool -c Release -- certify ./payload ./candidate.hhub ./reviewer.pfx
dotnet run --project tools/Hhub.PluginTool -c Release -- verify ./candidate.hhub ./reviewer.cer
```

PFX 密码由 HHUB_SIGNING_PASSWORD 环境变量提供，不通过命令行参数传入。certify 是维护者审核后的离线操作，不自动完成代码审核或杀毒扫描。不要将项目私钥交给作者或放进包。

`certification.p7s` 是带内容的 CMS/PKCS#7，SHA-256 签名覆盖由工具序列化的 UTF-8 文件清单原始字节。清单含 pluginId、version 和按路径排序的全体负载 path/length/sha256。只排除认证文件本身。验签后逐项校验实际文件集合；增删文件、改清单或依赖均拒绝。

签名证书 SHA-256 固定指纹必须被主体信任。归档字节摘要用于传输检查；签名清单的 SHA-256 是安装内容 ID，安装后撤销检查使用该 ID，不能通过重压缩或更改本地回执绕过。

## 服务、预设与更新

清单可用 provides 声明 `{contract, version, capabilityIds}`，requires 声明 `{contract, majorVersion, optional}`。提供者只能导出自己已声明的能力，主体按合同及主版本匹配；没有可选 EQ 不影响设备插件，多个提供者须明确选择。当前已实现声明校验与 ServiceCatalog 解析，尚未实现跨插件调用路由、授权和提供者生命周期，不得把此部分当作已运行的服务总线。

预设属于插件 ID、目标 ID 和 presetId，不属于插件安装版本。主体只保存可写且 persistence 不是 session 的值；不把读数、连接状态和临时运行开关保存成配置。当前界面可以保存、查看和应用当前目标的默认预设；多预设命名管理及正式迁移器仍待实现。未知预设格式拒绝覆盖，升级后不能忽略旧预设回到默认值。

`apply` 请求使用 targetId、expectedRevision 和 values 对象（1～128 项），不携带单值 capabilityId/value。主体检查插件/目标身份，SDK 在任何写入前校验全部参数，只允许可写且非 session 能力。顺序写入全部值，完整读回逐项确认后 revision 仅增加一次。失败对已尝试的能力按逆序恢复，包括可能已部分生效的失败写入；单项恢复失败仍尝试其余项。恢复后验证完整基线，确认恢复返回 transaction.rolled-back，否则进入 transaction.state-unknown，重新成功读取前禁止写入。这是有验证和补偿的批量操作，不承诺硬件写入过程具有不可观察的原子性。超时不自动重放；跨目标预设迁移不自动发生。

更新持有跨进程存储锁和插件会话锁；运行中的插件禁止替换或卸载。提交前校验所有目标预设，并保存完整插件数据快照和恢复日志；候选启动须完成握手、发现及在线目标读取。失败或更新中进程退出，重新打开存储时恢复旧版本与原始数据。保留当前和上一个程序版本，清理无恢复用途的暂存和快照。首次安装只校验和安装，不自动启动插件。认证并不构成操作系统沙箱，已认证的原生插件依然具有当前用户权限。

## 未完成与发布限制

当前合同子集尚缺目标身份实机验证、跨插件服务调用、订阅/增量、取消与幂等事务协议、插件页面模块 ABI、多预设命名管理和正式数据迁移器。现有 UI 是能力验证表单，不是最终专业设备页。

主体安装器、签名主体更新与崩溃回退、完整卸载/残留审计、正式认证密钥与签名撤销清单分发尚未接入。原生硬件和真实 EQ 未迁移。完成这些门槛前，不把此预览升级为稳定 SDK 或正式产品。

提供给 AI 开发时须同时提供本文件、schemas、合同源码和示例。只能实现已公开操作，不能猜测未完成接口、设备协议或实机通过结果。生成代码必须通过一致性测试，硬件验收另行记录。
