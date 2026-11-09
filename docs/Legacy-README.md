# Hhub 0.5

Windows 11 设备与功能插件平台，采用 .NET 10 + WinUI 3。

统一插件体系已开始实现：[首轮实现与验证记录](docs/Implementation-2026-11-09.md)、[开发预览 SDK](sdk/v1/README.md)。使用 `./tools/Build-UnifiedPreview.ps1 -WithSimulation` 构建独立的空主体与模拟插件验收包；这仍是预览，不是已通过安装/更新/卸载全部验收的正式版。

2026-11-09 开发方向：以 Hhub 为主体，先确立统一插件体系，再按其重写插件。GameHub 作为协议、实现与测试资料来源，不通过通用适配层强兼容历史插件。架构与迁移顺序见 [统一插件体系设计](docs/Plugin-system-v1.md)。该设计尚未实现为稳定 SDK，以下 0.5 说明描述既有版本。

当前正式命名为 **0.5**。[0.5 版本说明](docs/Hhub-0.5.md)包含既有版本的使用说明、架构、系统监测、场景和插件协议；后续开发方向见上方统一插件体系设计。

目前交付免安装版，不生成安装程序。发布产物只保留当前版和上一版。

当前版：[拖放修复版免安装包](artifacts/Hhub-0.5-Portable-DropFix.zip)，运行入口为 `artifacts/Hhub-0.5-Portable-DropFix/主程序/Hhub.App.exe`。

上一版：[托盘版免安装包](artifacts/Hhub-0.5-Portable-Tray.zip)。

完整解压并保留整个目录，先从托盘退出旧版，再运行需要测试的版本。各自的插件包、对应源码和版本清单均保存在对应目录中。

已实现主体、系统监测、FxSound DSP / EQ、Windows 游戏设置及 [VT3S MAX 鼠标插件](plugins/Hhub.Mouse/README.md)。插件界面使用原生模块或 WebUI，由主体承载，新增插件无需给主体添加品牌分支。真实硬件能力仍以实机验收结果为准，在线插件市场尚未接入。

## 构建

需要 .NET 10 SDK；音频插件还需要 LLVM MinGW UCRT x64 工具链，先运行 `./tools/Build-AudioPlugin.ps1`（详见 `plugins/Hhub.Audio/README.md`）。在项目目录运行：

```powershell
./tools/Build-Preview.ps1 -OutputName development
```

上述命令发布临时开发构建到 `artifacts/development`，监测插件版本跟随源码清单。`artifacts/audio-native` 是音频插件编译依赖，不是额外发布版本。用户数据位于 `%LOCALAPPDATA%\Hhub`。

生成安装包使用 `./tools/Build-Installer.ps1`，需要 Inno Setup 6.7.3；可通过 `-CompilerPath` 指定 ISCC.exe。输出目录为 `artifacts/installer`。

