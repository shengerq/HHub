# HHub

通过统一插件调节不同品牌外设的 Windows 平台。主体以 .NET 10 / WinUI 3 实现，功能按需安装、运行和卸载，共享公共运行组件，保留原设备固件，暂不开发固件升级。

此仓库公开维护 HHub 主体。当前 Desktop 主体为 `0.7.0`，插件 SDK 为 `1.0.0-preview.1`。安装包、更新日志和纯主体 ZIP 在 GitHub Releases 发布。

插件独立管理于 [HHub-Plugins](https://github.com/shengerq/HHub-Plugins)。现有八类插件完整业务源码、清单、界面、素材、测试和旧打包入口保存在该仓库的 `legacy`；后续按统一接口重写，不增加通吃旧插件的适配层。GameHub 只作为插件、协议研究和分析参考。

- [仓库边界与构建说明](docs/Repository-layout.md)
- [Desktop 主体发布、启动与更新](docs/Desktop-release.md)
- [统一插件体系](docs/Plugin-system-v1.md)
- [SDK 开发预览](sdk/v1/README.md)
- [兼容关系](docs/Compatibility.md)
- [本轮验证记录](docs/Validation-2026-11-09.md)
- [已实现范围与待办](docs/Implementation-2026-11-09.md)
- [成熟 0.5 功能说明](docs/Hhub-0.5.md)

Windows 11 x64，安装 `global.json` 指定的 .NET SDK 后：

```powershell
./tools/Test-Repository.ps1 -DotnetPath (Get-Command dotnet).Source
./tools/Build-UnifiedPreview.ps1 -DotnetPath (Get-Command dotnet).Source
```

第一条构建主体与 SDK 并运行统一自动检查；第二条发布空主体开发预览，输出到忽略的 `artifacts`。加入 `-WithSimulation` 生成独立模拟验收包，不自动安装，不代表正式认证。安装器源码与检查入口保留，安装、更新和卸载的完整实机验收仍待完成。

系统监测的主页数据、耳机与 FxSound 协作、手机远控和场景依赖均保留为已有功能的迁移范围。主题包仅保留提案。编译、模拟协议验证、UI 验收和实机结果分别记录。

历史记录中的 artifacts、工作区日志、原第三方源码路径属于原始项目上下文，未作为历史构建包上传。个人数据、二维码、密钥、缓存和厂商安装程序不进入仓库；原始工作区完整保留。
