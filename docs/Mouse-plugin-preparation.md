# 鼠标插件开发准备：剩余项目分析与雷柏 VT3S MAX

日期：2026-11-02。首批设备由用户确定为 **Rapoo VT3S MAX，有线 / 2.4G**，不包含蓝牙。

2026-11-03 后续进展：已实现独立 `Hhub.Mouse 0.1.0` 实测版、打包和离线检查，使用及限制见 [插件说明](../plugins/Hhub.Mouse/README.md)。本文保留准备阶段的分析和当时验证记录，不代表当前仍未实现插件；目标硬件实机验证仍待完成。

本文基于本地源码、清单、许可文件和现有 Hhub 控制台检查。没有运行第三方应用、发送 HID 控制报文或更改鼠标设置；第三方 README 的支持列表和测试声明不视为本次实机验证。本文是开发准备，不表示已交付鼠标插件。

## 1. 结论与项目分工

建议新增独立 `Hhub.Mouse` 插件，沿用 .NET 10、独立窗口和 `stdio-v1`。Hhub 负责设备列表、插件生命周期与场景，插件负责端点识别、协议、能力判定与控制结果。首版只完成 VT3S MAX 的识别、实时状态、DPI 和回报率闭环，再扩展型号。

| 剩余项目 | 本地实际材料 | 对鼠标插件的作用 | 不能直接采用的部分 |
| --- | --- | --- | --- |
| FerrisPulse | C# 源码、HID 传输、各品牌 Helper、测试脚本 | 首批雷柏协议的主要研究来源，重点看端点分组、遥测、寄存器 | 全局单设备状态、部分成功判定、宽泛能力默认值、整套托盘与应用识别 |
| ClickSync | 无构建依赖的 WebHID JS 源码、独立品牌协议文件 | 对照雷柏编解码、读写队列、统一设置键和型号能力 | 浏览器授权/事件机制，通用 VID 兜底，以及用发送成功代替设备确认 |
| OpenMouse | Preact/TypeScript 控制台及测试；协议为外部依赖 | 待提交设置、组合写入、会话归属检查、能力驱动界面 | 当前目录没有鼠标协议包源码，不可据控制台支持列表承诺硬件适配 |
| InputLab | README、导出格式、发布说明和使用条款，没有源码 | 主机输入送达间隔、有效性标记和导出语义参考 | 无法移植测量实现；不做反编译；首版无需捆绑此工具 |
| ClickSyncMouseTester | 只有 v0.8.2 x64 EXE | 日后可由用户独立运行作对照 | 本地没有源码、说明和授权材料，无法评估算法或作为可复用库 |

YuqiEngine 和 FxSound 已有专项分析及对应插件，不在本轮重复移植。Windows 鼠标加速度仍由 `Hhub.WindowsSettings` 管理，鼠标插件操作硬件参数，避免两个插件重复写同一设置。

## 2. 首批型号：标识、端点与待核实项

### 2.1 目录标识不等于实机确认

FerrisPulse 的 `src/Hardware/RapooDeviceHelper.cs` 明确包含：

| 目录项 | 标识 | 当前证据 |
| --- | --- | --- |
| VT3S MAX | `24AE:1421` | 无线型号目录 |
| VT3S MAX | `24AE:4621` | 有线型号目录 |
| VT3S MAX MASTER | `144B / 464B` | 单独目录项，不能混作本型号 |
| VT3S MAX V2 | `1B07 / 4B07` | 单独目录项，不能混作本型号 |

先按 `1421 / 4621` 建立候选白名单，实际枚举与设备型号回读一致后再启用写入。源码还处理 `14FF / 1500 / 1501` 通用接收器，通过设备型号寄存器解析实际型号；这些 PID 不应无条件写入首批清单。

不能仅凭“VT3 系列”或 PID 差值推导兼容。首批有线与接收器必须各留一份描述符、固件信息、读回状态和 TX/RX 样本。

### 2.2 本地雷柏协议线索

主要来源：FerrisPulse 的 `RapooProtocol.cs`、`RapooDeviceHelper.cs`、`RapooDeviceHelper.Status.cs`；对照 ClickSync 的 `src/protocols/protocol_api_rapoo.js`。

| 用途 | 本地实现中的形状 | Hhub 接入约束 |
| --- | --- | --- |
| 控制 OUT | UsagePage `FF00`、Usage `000E`、Output 长度 33 | 必须映射到用户指定的设备实例 |
| Feature 回复 | UsagePage `FF00`、Usage `000F`、Feature 长度 33 | 必须与控制集合属于同一设备组 |
| 状态 IN | UsagePage `FF00`、Usage `0002` | 原实现允许 9–65，VT3 测试使用 19；首批按实机描述符收紧 |
| 状态报文 | Report ID `07`；X/Y 为小端 16 位；状态档位为 0 起始 | 对外统一档位语义，验证长度、类型、范围；无效电量为未知 |
| 寄存器 | System bank `08`：回报率 `80`、X 表 `88`、Y 表 `C8`、档位数 `96`、当前档位 `98` | 均为源码线索，尚未对 VT3S MAX 实机验证 |
| 型号回读 | Comm bank `00`、地址 `04`、长度 2 | 区分接收器枚举 PID 与实际鼠标型号 |

必须区分 WebHID API 单独传入的 Report ID 与 Win32 缓冲区首字节，不能直接复制 JS 数组后重复添加或遗漏 Report ID。还要区分中断 OUT 与 Feature 传输，不以“换另一种发送方法重试”代替确定端点。

FerrisPulse 的端点分组保留父实例及 MI，移除集合编号；这是值得研究的方向，但其 `SelectEndpoints` 最终只选一个组，而且偏好有线。Hhub 需要返回所有候选组，再按明确的目标实例选择，不能把“优先有线”当成多设备身份方案。

### 2.3 不能继承的成功判定

- `SetRapooPollingRate` 用 `ok1 || ok2` 判断寄存器及性能模式写入，随后缓存目标回报率；这不能证明整体成功。
- `UpdateRapooHardwareStages` 用 `okA || okB` 接受 X/Y 表的部分成功，且未将档位数和激活档位写入结果纳入最终判断。
- `SetRapooXyDpi` 在缺少表缓存时生成默认 DPI 表。新插件必须先读真实表，不能把默认值写进用户未知的其他档位。
- 雷柏 Helper 与状态部分包含静态句柄、当前设备组和共享缓存；`BuiltInDrivers` 的雷柏入口没有把目标实例传进写入方法。
- `HardwareCommandBus` 按 VID/PID 加锁；新插件应按实际传输会话/接收器通道串行化，同型号不能因此共享目标状态。
- `GetSupportedPollingRatesForModel` 对非特定 1K 型号默认返回至 8K；通用能力模型的 26,000 DPI 上限、默认步进与协议的 16 位范围也不等价。不能据此宣称 VT3S MAX 的实际能力。

新实现应执行“验证目标与能力 → 读取原值 → 发送完整操作 → 关联有效响应 → 回读相关寄存器/状态 → 比较目标值 → 报告结果”。多步写入不是硬件原子事务，发生部分失败须重新读取实际状态并报告；恢复也须单独确认，不承诺天然回滚。

## 3. 其他项目的具体可用内容

### FerrisPulse

`IHardwareDriver`、`DeviceCapabilities` 与注册表机制体现了按能力派发的分层；`HidTransport` 提供 SetupAPI/HID 枚举及 Overlapped I/O。可参考接口职责，按 .NET 10 重建实例化会话和资源管理，不搬运整个 C# 5 / .NET Framework 4.0 应用。

`tests/test_rapoo_vt3_protocol.ps1` 已有状态、XY、电量边界、Feature 回复、物理组隔离和有线优先等样本。它们可以指导新测试设计，但本轮未运行该项目测试，样本也不能视为 VT3S MAX 真机抓包。

### ClickSync

`refactor.core.js` 统一 stdKey，`refactor.profiles.js` 管理范围、能力和映射，`device_runtime.js` 负责连接。雷柏协议文件拆出了编解码、排队传输和规划器，适合对照寄存器编码。

不能把同厂 VID、UsagePage 或产品名兜底作为写权限。雷柏文件的筛选条件存在 `24AE + FF00` 宽泛兜底。另一个具体例子是 ATK 的 `setBatchFeatures`：失败时回读纠偏，发送成功后直接把 nextState 写入缓存，没有成功路径回读确认。新插件必须保留“目标设置”和“设备已确认状态”两份状态。

`src/tools/mouse-main.js` 使用浏览器事件、`performance.now()` 和合并事件分析；可参考操作引导与统计呈现，不将浏览器事件频率称为 USB 实测轮询率或真实点击延迟。

### OpenMouse

本地 `package.json` 和 lock 固定 `@openmouse/protocol` 为 `0.49.0`，键盘协议为 `1.6.1`；当前没有 `node_modules`，真正的鼠标 driver registry、Rapoo HID 等通过包导入。

`pending-changes.ts` 按设置键去重、按共享字节/事务分组，并支持写入优先级。`profile-write-ownership.test.ts` 覆盖切换设备后旧异步写入不得作用于新会话，以及 A/B/A 切换令牌失效。新插件的会话代次和取消机制应覆盖这些场景。

`supported-devices-data.ts` 的 VT3 条目为 `pending`，不是 VT3S MAX 已验证支持的证据。若后续采用包内实现，需要另行取得锁定版本源码、许可证和测试材料，本轮没有安装或下载它。

### InputLab 与 ClickSyncMouseTester

InputLab 明确是 binary-only，当前目录甚至没有其运行包。`EXPORT_SCHEMA.md` 将鼠标流标为 `RawInputMouse`，区分目标回报率、主机到达率、有效间隔、批量送达、多设备混流和无效样本。可参考这些公开字段的语义，独立实现 Hhub 测量功能；不复制其评分阈值或推定算法。

ClickSyncMouseTester 只有文件版本 `0.8.2.0` 的 EXE，大小 13,468,672 字节，SHA-256：

```text
431EB54F035DB20718E15B9A98918DAD3892EFE4C05DF7ECEC8BCE070AAF15D9
```

没有执行或反编译该文件，也没有将其名称相似视为与 ClickSync 相同的源码或许可证。

### 本地许可材料登记

这是本地文件事实登记，不对组合分发作法律结论：ClickSync 的 LICENSE 是 GPL v2；FerrisPulse 是 GPL v3；OpenMouse 及 lock 中的鼠标协议包标注 AGPL-3.0-or-later。InputLab 的自定义条款允许原始包使用/分享，禁止修改、反编译和分发修改版。ClickSyncMouseTester 当前没有随附许可。

FerrisPulse 的 `docs/rapoo-tray-MIT.md` 记录部分雷柏报文和端点逻辑源于 Iris 的 rapoo-tray，并保留 MIT 文本。这不等于 FerrisPulse 的整个雷柏实现均为 MIT。若希望复用该上游，先取得对应原始源码和版本，逐文件确认来源；目前尚未完成这一步。独立进程架构也不自动消除复制第三方代码带来的许可义务。

## 4. Hhub 现有接口与实际缺口

已核对 `Models.cs`、`PluginPolicies.cs`、`PluginRuntime.cs`、`ScenePlan.cs`、`PlatformServices.cs`、`MainWindow.Devices.cs`、`MainWindow.CommandForm.cs` 与 `MainWindow.Scenes.cs`。

| 能力 | 现状 | 首版处理 |
| --- | --- | --- |
| 独立入口、stdio、停用/按需 | 已有；结果等待上限 15 秒 | 直接复用；插件内部 I/O 和取消必须有界 |
| 指定设备命令 | `values.deviceInstanceId` 已传入 | 插件重新枚举并核对，不接受仅 VID/PID 的写入 |
| 设备信息补充 | `values.devices` 按原始 instanceId 合并 name、connection、battery | 实现精确节点映射；其他鼠标参数暂在插件窗口展示 |
| 接口选择 | 每个实例只保存枚举到的第一个接口路径 | 插件自行枚举 HID 端点并解析设备树关系，不直接打开该路径作为控制端点 |
| 身份关联 | 记录 InstanceId、ContainerId、Location；命令只带 InstanceId | 插件从系统查询其父级/容器，映射普通鼠标集合与控制集合；有歧义拒绝写入 |
| 连接分类 | 主体常显示 USB/HID，不能据此辨别有线与 2.4G | 插件基于端点与型号协议补充；不可只按 USB 总线推断有线 |
| 场景配置 | 静态清单 + sceneConfigurable + number/string/boolean | 首版只声明已验证型号共同支持的命令，插件再验证离散值与步进 |
| 动态能力 | 没有逐实例动态命令范围、枚举参数或数组表单 | 首版复杂档位表放插件窗口；多型号动态能力作为后续通用协议扩展 |
| 回执 | success/error，不能返回结构化实际设置 | success 仅用于已确认结果；实际状态通过新快照发送 |
| 数据时效 | 依快照接收时间过期 | 插件不能用新心跳包装旧电量；休眠/离线时省略或置空实时字段 |
| 场景重试 | 同一动作键会记录为已尝试，失败后需显式重试或相关状态变化 | 测试休眠唤醒、断开重连、插件重启；不能依赖周期循环自动重发 |

Hhub 当前未给插件发送完整设备上下文，但传入的实例 ID 足够作为系统查询起点。第一阶段可在插件内部完成映射；若实际硬件证明需要主体提供额外信息，应增加通用可选字段，而非给主体写雷柏专用分支。

清单仅负责发现候选插件，不承担最终硬件授权。不要先发布只有 VID 的宽泛匹配，也不要把 category 写成与主体实际中文分类不一致的 `mouse` 后误以为能匹配。

## 5. 建议实现拆分与命令

拟新增目录如下，当前尚未创建可运行工程：

```text
plugins/Hhub.Mouse/
  Program.cs / PluginConnection.cs       独立入口、stdio、回执
  MouseService.cs / MouseWindow.cs       用例与独立面板
  Devices/                              描述符、目标解析、连接与会话
  Transport/                            HID 枚举、句柄、取消、串行通道
  Protocols/Rapoo/                       编解码、VT3S MAX 适配与回读
  plugin.json / THIRD-PARTY-NOTICES.md
tests/Hhub.Mouse.Tests/                  离线协议与模拟传输测试
tools/Build-MousePlugin.ps1              独立打包，不自动启动设备控制
```

首版命令建议：`openPanel`、`refresh`、`setDpi`、`setPollingRate`。后两者在两种连接分别完成验证后才设 `sceneConfigurable=true`。资源分别使用 `mouse.dpi`、`mouse.pollingRate`，参数键保持通用，不写品牌专用主体代码。不要预先把 8K、DPI 上下限或步进硬编码成已验证能力。

每个会话保存目标节点、端点组、连接代次、实时状态和写队列。热拔插、模式切换、取消和重连使旧代次失效，旧任务不得继续写入新设备。若多个设备共享接收器通道，按实际通道加锁，目标设备状态仍独立保存。独立窗口与 Hhub 托管模式之间需要同设备写入互斥，避免两个进程争抢端点。

声明页可先提供状态和“打开完整面板”；DPI 档位表、XY、高级传感器、按键映射都在独立窗口逐步实现。无需先扩建 Hhub 页面协议。

## 6. 开发顺序与验收

1. **锁定材料与身份**：确定代码采用方式，保留来源、版本、许可和哈希；为有线/2.4G 建立描述符记录，确认 PID、Usage、长度、固件及设备节点对应关系。仅做描述符枚举时不发送厂商控制帧。
2. **协议与模拟传输**：独立编解码与状态模型，覆盖长度、Report ID、ACK/Busy、错误响应、字节序、电量 0/100/无效、档位边界。加入两个相同型号、错误端点、错误目标及取消回放。
3. **只读识别与状态**：分别确认两种连接的型号、电量、XY DPI、档位和回报率；区分“接收器在场”“鼠标在线”“充电”“历史缓存”。未知值保持未知。
4. **最小写入闭环**：保存真实原值，再实现 DPI / 回报率的写入和回读；保留未修改档位。覆盖部分写失败、Busy、超时、断开、恢复失败和官方驱动并发覆盖。未确认不得报 success。
5. **Hhub 集成**：单独打 .hhub 包；验证设备页映射、停用/按需、15 秒结果、场景切换和失败重试。退出插件不反复刷写设备，不把板载设置误当临时进程状态。
6. **实机验收后扩展**：有线单独、2.4G 单独、线与接收器同时连接、拔插、休眠唤醒、换端口、主体与独立面板争用；再决定开放 XY 多档位和其他功能。

测量功能放到控制闭环之后，采用按设备过滤的 Raw Input 和高精度单调时间戳，记录批量送达、低样本和多设备污染。界面区分“设备设置回报率”和“Windows 输入送达率”，不将它们等同于点击到屏幕延迟。ETW、中断亲和性、系统电源改动和通用优化不进入鼠标插件首版。

## 7. 本轮验证与交付边界

普通 `dotnet` 不在当前 PATH；使用 `tools/Build-Preview.ps1` 已约定的 SDK 路径 `%USERPROFILE%/.cache/hhub/dotnet/dotnet.exe` 执行：

```powershell
& "$env:USERPROFILE/.cache/hhub/dotnet/dotnet.exe" run --project tests/Hhub.Core.Tests -c Release
& "$env:USERPROFILE/.cache/hhub/dotnet/dotnet.exe" run --project tests/Hhub.Platform.Tests -c Release -p:Platform=x64
```

结果：核心 **85 checks passed**；Windows 平台 **6 项通过**，包括真实设备枚举、watcher 起停及进程场景信息。平台枚举得到 7 条记录、6 个可用接口路径，这只是当前执行环境结果，没有确认其中存在目标鼠标。

当前工作目录没有 `.git`，无法记录本次源码提交号或生成 Git diff。没有修改主体业务代码、第三方代码或安装包；没有编译第三方项目。尚未取得 VT3S MAX 双模式实机报文、确认真实能力、实现或验证鼠标硬件控制。
