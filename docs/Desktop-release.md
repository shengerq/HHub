# HHub Desktop 主体发布与更新

主体版本取自 `src/Hhub.Desktop/Hhub.Desktop.csproj`。Windows x64 交付包含桌面程序、原生托盘和运行依赖。插件通过自己的签名包发布。

## 本地构建

在主体根目录运行以下命令。`Dotnet` 指向 `global.json` 对应的 SDK，`CppCompiler` 指向 Windows x64 MinGW `g++.exe`，其同目录需包含 `windres.exe`。

```powershell
./tools/Build-RebuiltHost.ps1 -Dotnet (Get-Command dotnet).Source -CppCompiler (Get-Command g++).Source -Package
```

输出为 `artifacts/HHub-Host-win-x64.zip` 和同名 `.sha256`。包仍自包含。Desktop 及 UI 合同项目通过细分的 WinUI 包引用渲染组件。打包省略调试符号，使用 ZIP 的最大压缩级别。

## GitHub Releases

主体读取公开仓库 `shengerq/HHub` 的 `updates.json`。稳定渠道接受 `vA.B.C`，预览渠道也接受 `vA.B.C-preview.N`。更新版本必须高于当前版本。同一版本优先提供 Windows x64 安装程序。

使用 `tools/Build-DesktopInstaller.ps1` 构建安装程序及 ZIP。安装器编译需要 Inno Setup 6.6 或更新版本。发布版本标签应指向源码提交。先创建草稿并上传产物，对照本地 SHA-256 验证 GitHub 上传结果。公开发布后，将已发布版本和安装资源信息写入 `updates.json`。

自动检查在打开桌面后执行一次。发现更新时主窗口提示，用户可点「查看更新日志」进入「设置 → 关于」。当前版本可直接查看随附日志并预览同一提示。用户点击下载后，应用校验文件大小及 SHA-256，再保存到指定目录。

桌面预览会在用户的插件库源中首次加入 HHub-Plugins 的 `catalog.json`。已有库源照常保留，用户可以在「设置 → 插件 → 库源」移除该目录。目录和包的访问由插件库的发布方负责。

## 启动与后台验收

- 在「设置 → 常规」可预设启动延迟和启动后进入托盘。延迟提供立即、15 秒、30 秒或 60 秒，默认 30 秒。启用开机自启动后，两个设置在 Windows 登录时使用。手动打开会立即显示。
- 测试主窗口出现、主页和插件切换及系统辅助功能中的动画开关。页面过渡由 WinUI Composition 执行，遵循 Windows 的动画开关。 [WinUI Composition](https://learn.microsoft.com/en-us/windows/apps/develop/composition/xaml-comp-interop)
- 开启托盘效率模式后关闭主窗口到托盘，核对仍在运行的 `Hhub.Tray.exe`。使用 Windows 进程 API 校验 EcoQoS 位及低于正常的优先级。再手动打开主窗口，确认托盘恢复正常，桌面进程以正常优先级启动。 [Windows 进程电源节流策略](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-setprocessinformation)
