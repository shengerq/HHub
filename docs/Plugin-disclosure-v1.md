# 插件开源与依赖披露 v1

在插件清单中增加 `disclosure` 对象，并将 `plugin.disclosure.v1` 加入 `requiredCapabilities`。声明该能力却省略披露内容时，主体拒绝安装和加载；旧主体会通过能力协商拒绝不受支持的包。以下是格式示例，示例项目地址不可作为实际发布内容：

```json
{
  "disclosure": {
    "schemaVersion": 1,
    "license": "MIT",
    "licenseFile": "LICENSE",
    "runtimes": [
      { "name": ".NET", "version": "10", "architecture": "x64", "provision": "host", "description": "插件后台使用 Hhub 提供的共享 .NET 运行环境" }
    ],
    "openSourceProjects": [
      { "name": "实际使用的项目", "version": "版本或提交号", "projectUrl": "https://example.test/project", "license": "MIT", "licenseFile": "licenses/project-MIT.txt", "attribution": "实际版权与贡献者署名" }
    ],
    "dependencies": []
  }
}
```

`runtimes` 必须非空。`provision` 为 `host`（主体提供）、`bundled`（插件携带）、`system`（操作系统/用户预装）；`architecture` 为 `x64`、`arm64`、`x86` 或 `any`。额外携带的运行环境须在 description 说明用途、额外体积及清理方式。

`openSourceProjects` 声明采用或改编的开源实现，包括实际修订、原作者与许可证；`dependencies` 声明运行所依赖的第三方组件及其许可证。无额外项目时显式使用空数组。所有组件字段必填，项目链接使用 HTTPS，不得包含凭据。复合许可或附加条款保留原文，不凭猜测替换许可。

`licenseFile` 是包内原文路径，不允许越出插件根目录；发布包必须包含所有声明的许可文件，保留上游 NOTICE。主体通用地展示声明，不对某个品牌或插件编写专用映射。

旧插件未声明时，关于页会标明缺失情况，扫描包内 `.deps.json` 和 LICENSE/NOTICE 文本供查看；从依赖记录读到的名称不等同于作者已经提供完整开源声明。

## 主体更新设置

“设置 → 关于”同一行展示 Hhub 名称与主体版本，下方为稳定版/测试版/开发版渠道和自动更新选项。渠道及自动更新偏好均可修改并持久保存，按用户要求暂不因发布源待配置而禁用界面。当前未提供实际主体发布源，此阶段不下载或执行更新，也不声称当前已为最新版本。开源与依赖区域将主体及各插件放入同样宽度、同样底色的展开卡片；主体卡片内统一显示运行环境、依赖和开源许可。

更新提醒从关于页移到展开侧栏的 Hhub 名称旁，仅当更新源提供当前渠道、且高于当前版本的真实版本时显示 NEW。点击后进入软件更新视图，显示“当前版本 → 目标版本”及更新入口；无可用更新时不显示标记。当前只实现了可用版本信息的界面接入点，发布源检查、包下载验证及自动安装仍待真实发布源接入，界面不会自行构造更新信息。
