# 插件 WebUI 与设备展示

2026-11-05：模拟耳机可使用 `deviceMatches[].audioRenderEndpointId` 精确绑定 Windows 播放端点，并声明公共能力 `device.audio-endpoint-match.v1`。匹配检查真实 `DeviceRecord.AudioRenderEndpointIds`（大小写不敏感），不按 Realtek 等共享声卡名称推断耳机型号；旧主体因能力缺失拒绝安装。该绑定是用户指定的插孔输出身份，不能识别之后插入的其他模拟耳机；更换声卡或驱动导致 ID 变化后须重新绑定。接入示例为 `plugins/Hhub.HpOverture`。

2026-11-03。主体新增通用能力 `page.web.v1`、`page.full-content.v1`、`device-presentation.v1`。鼠标插件 0.2.0 使用这些能力；已有原生 WinUI 插件模块继续使用 `IPluginUiModule` / `IPluginPageView`。

插件可用一个薄模块返回 `new WebPluginPage(context, "web")`，在 `web` 文件夹提供 HTML/CSS/JS。清单声明 `uiAssembly`、`page.layout="declarative"`，并为新能力填写 `requiredCapabilities`。`page.fullContent=true` 隐藏主体内容标题和默认内边距，插件拥有内容区；主程序保留窗口顶栏和侧栏。

页面通过 `window.chrome.webview.postMessage({type:"command",id,command,values})` 发送命令。主体只派发清单中声明的命令，调用同一 stdio 后端；通过 `{type:"result",id,success,error}` 返回结果，`{type:"snapshot",values,fresh,enabled,theme,deviceInstanceId}` 同步数据。页面必须区分目标编辑值和已确认设备状态，并用 `fresh` 禁止过期状态下的控制。设备上下文会作为默认目标传入，具体端点核对仍由后端完成。

WebUI 使用 WebView2 虚拟 HTTPS 本地资源映射，按插件 ID 分开浏览器数据目录。只接收本地页面的桥接消息，阻止外部导航与新窗口；资源、布局和样式由插件维护。入口不存在或 WebView2 初始化失败时在原内容区显示错误，不弹出独立设置应用。

静态设备展示例：

```json
"devicePresentations": [
  {"vid":"24AE","pid":"1421","name":"雷柏 VT3S MAX","image":"assets/vt3s-max.jpg"}
]
```

主体按产品标识匹配静态名称和图片，图片须是包内存在的 PNG/JPEG/WebP/SVG 文件。动态快照 `values.devices[]` 可通过真实 `instanceId` 覆盖 name、number、connection、image、battery；组合设备还会匹配成员实例。名称与图片可以独立于实时采集状态显示，电量只取新鲜有效数据，不将停止采样的缓存当实时值。主体不包含鼠标品牌专用分支。

### 主页设备概览

设备插件声明 `device.overview.v1` 和 `deviceOverview`，可在已匹配设备上提供静态 `name`、`category` 和 `connection`。实时概览随 `values.devices[]` 的实例项发布。

```json
{
  "instanceId": "实际设备或成员接口的实例 ID",
  "overview": [{"label":"DPI","value":"800"}],
  "currentConfiguration": "板载配置",
  "configurations": ["日常使用", "游戏"]
}
```

可选 `refreshCommand` 引用该插件的读取命令，主页选中设备时传入 `deviceInstanceId`。可选 `applyCommand` 引用唯一参数为字符串 `name` 的命令，调用时也传入实际设备实例。插件在写入前验证实例，回读确认后才更新 `currentConfiguration`。没有可用选项时下拉框禁用。对于通过协作接口应用的配置，插件声明 `integrationOptionsKey`、`integrationCurrentKey` 和 `integrationEndpointKey`。主体从协作方的新鲜快照获取配置，只有播放端点属于当前设备时才显示该配置为当前项。

设备类别通过设备卡进入，不生成侧栏入口；侧栏扩展用于功能型应用。鼠标 0.2.1 使用 `device-entry.v1` 和 `device-artwork.svg.v1` 能力声明。页面可发送 `{type:"back"}` 返回来源设备页，此动作只调用主体导航，不经过硬件后端。

参考：[Microsoft 本地内容映射说明](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/working-with-local-content)、[SetVirtualHostNameToFolderMapping](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2.setvirtualhostnametofoldermapping)。

主体保留文件安装拖放的接收权。WebUI 不应将插件包拖放作为网页导航或自行安装；共享 WebPluginPage 的透明背景在初始化前设置，加载完成前保持隐藏。UI DLL 从不可变副本加载，同目录资源一起复制；插件应使用 Plugin.Directory 访问包根资源，或使用 Assembly.Location 访问随 UI 模块分发的同目录资源，不能依赖 UI DLL 仍位于安装目录。
