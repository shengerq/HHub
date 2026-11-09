# Hhub 0.5 通用设备识别规则

更新：2026-11-04。此实现面向通用设备结构，不包含 EZ63、雷柏、ITE 等型号的主体识别分支。

## 当前产品范围

底层可以为诊断保留完整设备结构，默认列表面向游戏外设。`GamingPeripheralPolicy` 在主体刷新设备时统一应用，主页计数、设备页及场景设备使用同一份过滤后的列表。

展示外接键盘、鼠标、耳机、手柄、键鼠复合设备；匹配插件可以补充其他外接游戏外设类型。内置触控板/指针、内置 USB 控制器、摄像头、磁盘、打印机、未确认用途的普通 HID 和内置扬声器不进入默认列表。不能因摄像头附带键盘按键接口就把它列为游戏键盘。

USB 设备的内置判断取物理 USB 父节点的 `DEVPKEY_Device_Capabilities` / `CM_DEVCAP_REMOVABLE`；不使用 HID 子接口的不可移除标记来误删整台外接设备。属性缺失表示未知，不伪造“内置”结论。该判断依赖 Windows 总线驱动报告，错误或缺失的报告仍可能影响范围判断。

手柄从 HID Generic Desktop 的 Joystick、Game Pad、Multi-axis Controller 用途或 XUSB10/XUSB20 兼容接口识别。附带耳机插孔的手柄仍以手柄作为主类型。

声卡报告为 Headphones/Headset 的模拟耳机仍展示；声卡只报告共用 Speakers 端点时，不把它冒认为独立耳机。当前默认列表不展示该内置扬声器端点，音频插件仍可按自身能力枚举和选择输出。

主页固定为左主卡、右预选列表，约 2:1。以下底层分类与音频枚举说明用于识别与诊断，不代表所有可识别设备都进入产品列表。

依据：[可移除能力](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/overview-of-the-removable-device-capability)、[父节点与子节点的可移除标记](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/troubleshooting-the-implementation-of-container-ids)、[XUSB10 描述符](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-xusbi/601a3107-6583-4e80-867e-118829cb573e)。

## 分清三个层次

1. 连接实体：Windows 设备树及设备容器给出的实体或连接，例如 USB 复合设备、蓝牙设备。
2. 功能接口：键盘、鼠标、触控板、厂商 HID 等功能。同一实体可以同时具有多个接口，接口类型不直接等于实体主类型。
3. 音频输出端点：Windows Core Audio 提供的实际可用输出。模拟耳机不必有独立 USB/HID 节点，不能只通过 PnP 输入设备查找耳机。

Windows 会为 HID 顶层集合创建独立设备节点，多个集合可以来自一个物理设备。参考：[HID 顶层集合](https://learn.microsoft.com/en-us/windows-hardware/drivers/hid/top-level-collections)。

## 主分类

保留一个实体的全部接口分类、路径和关联实例，供筛选、插件匹配及旧配置使用。展示主类型不再采用固定的“鼠标优先于键盘”规则。

- 摄像头、磁盘、打印机及音频功能不会被它们附带的 HID 按键接口抢占主类型。
- 内置触控板结合 ACPI/I2C 拓扑与 Digitizers / Touch Pad 用途识别，并归并其鼠标兼容接口。只有鼠标兼容信息时显示“内置指针设备”，不猜测触控板型号。
- 同时包含键盘、鼠标接口时，先取物理产品描述给出的类型，再参考唯一 USB Boot 接口类型。该判断属于通用证据判断，不能保证所有复合接收器都具有唯一主产品类型。
- 主类型证据不足时显示“键鼠复合设备”，保留两种能力。不会因为存在一个鼠标接口就认定整台设备是鼠标，也不会把所有复合设备反过来认定为键盘。
- Windows 标准名称 Integrated Camera / Integrated Webcam 显示为“内置摄像头”。原始名称仍保留在详情中。其他厂商名称不翻译成未经证实的产品用途。

兼容标识来源：[DEVPKEY_Device_CompatibleIds](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/devpkey-device-compatibleids)。USB 兼容标识编码接口类别、子类和协议；不是主体维护的产品型号名单。

## 音频识别

主体仅使用 NAudio.Wasapi 2.2.1 调用 Core Audio 做只读枚举和通知监听，不启动音频处理、录音、播放，也不改变音量、默认输出或路由。

读取当前 Active 的渲染端点、名称、ContainerId、控制器关联及 FormFactor。系统声明为 Headphones / Headset 的端点可显示为“耳机”。远程网络音频输出排除在默认外设列表外。

USB 和蓝牙音频端点优先通过真实父链、音频控制器实例或设备接口路径关联到物理设备，再用唯一设备容器补充。关联有歧义时保留端点。声卡的多个模拟输出保持独立。孤立的蓝牙免提服务和 `Microsoft_HFP_Audio_Device_*` 占位端点留在原始诊断，关联到真实耳机时保留为它的接口和实例别名。

直连 USB 耳机显示「USB」。Windows 音频拓扑确认插孔尺寸时显示「3.5 mm」，经 USB 声卡接入时显示「3.5 mm · USB 声卡」。只有声卡父节点证据时显示「声卡接口」，关联信息不足时显示「接口未确认」。共用的扬声器端点无法确认被动耳机插拔或型号时，保留系统端点的实际身份。插孔类型来自 [Windows 插孔描述](https://learn.microsoft.com/en-us/windows/win32/api/devicetopology/ns-devicetopology-ksjack_description)。

音频端点变化由 Core Audio 通知触发去抖刷新。所有事件均为观察，不修改用户的音频配置。

依据：[音频端点属性](https://learn.microsoft.com/en-us/windows/win32/coreaudio/device-properties)、[端点形态枚举](https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/ne-mmdeviceapi-endpointformfactor)、[蓝牙音频端点容器](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/audio-endpoint-container-id)。

## 插件补充准确产品类型

新增通用能力 `device-classification.v1`。插件使用此能力时应在 `requiredCapabilities` 中声明，以便旧主体拒绝不兼容插件。

静态 `devicePresentations[]` 可附带可选 `category`；动态 `values.devices[]` 可通过真实 `instanceId` 提供 `category`，关联实例同样有效。静态声明不需要启动后台进程。未填写时保持通用识别结果，旧插件清单仍兼容。

```json
{
  "vid": "1234",
  "pid": "5678",
  "name": "示例设备",
  "category": "键盘"
}
```

类型标签可由插件扩展，不维护品牌与型号的主体分支。已知类型显示相应图标；其他类型保留通用图标并加入筛选项。动态更新同步图标，避免文字已经变为键盘、图标仍是鼠标。

## 面向使用者的反馈

列表以“类型 · 连接方式 · 已知端口”辅助辨认；设备详情提供可展开的 Windows 识别依据与原始名称。`ITE Device(8910)` 之类描述只能证明 Windows 所报告的接口，不能凭名称确认它是外接键盘、灯控或某个内部控制器。

设备页更多菜单提供“复制设备识别诊断”，含原始节点、兼容标识、音频端点、归并结果与枚举错误。只有用户点击时写入剪贴板，不自动上传。此入口用于修正通用规则，不是要求每位使用者自己研究设备协议。

本轮通过构造拓扑验证多型号通用规则、复合输入、模拟/USB/蓝牙音频、重复容器及原实例兼容。当前环境实际完成 Core Audio 只读枚举、通知注册释放和 PnP 观察器检查；目标实机的接口描述和声卡行为尚待实机验收。
