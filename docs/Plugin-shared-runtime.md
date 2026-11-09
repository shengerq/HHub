# 共享 .NET 10 插件后台入口

Hhub 0.5 公共能力：`runtime.dotnet10.shared.v1`。配套完整主体须包含 `Hhub.PluginHost.exe`、其 DLL/运行配置及主体自包含 .NET 10 环境，文件共用同一主体目录，不在各插件包中重复发布运行时。

托管插件在清单中填写 `.dll` 后台入口、`transport: "stdio-v1"`，并把 `runtime.dotnet10.shared.v1` 放入 `requiredCapabilities`。后台项目使用 .NET 10、`UseAppHost=false`、`SelfContained=false`，提供托管 Main 入口。后台 DLL 及私有依赖、`.deps.json` 放进插件包；不要附带 CoreCLR、WPF 或主体公共程序集。

主体在独立进程中执行 `Hhub.PluginHost.exe <包内后台.dll> [清单参数] --hhub-stdio`。程序参数通过 ArgumentList 传递；DLL 路径先由包路径校验解析。启动器执行托管入口并解析插件目录内的私有托管/原生依赖；业务、界面及设备协议仍由插件负责。

**目录含义**：后台复用主体运行时后，`AppContext.BaseDirectory` 是主体目录。插件资源路径应从自身程序集 `Assembly.Location` 所在目录解析，不能把预设、驱动或图片误读为主体私有资源。

后台只处理 stdio 协议，不应在缺少参数时弹出控制窗口。独立测试、预览和源码可以另外交付，不作为普通用户使用的前提。持续任务在快照中上报 `backgroundActivity` 及非空的 `backgroundActivityReason`；普通配置修改不创建虚假后台任务。

正常停止使用现有 shutdown 协议。插件自己的必要恢复辅助进程也必须复用共享入口，限定会话寿命，不能变成独立常驻控制服务。

`PluginRuntime.SharedRuntimeHostPath` 允许开发测试显式指定完整主体目录中的启动器；正式主体默认从自身目录查找。FXsound 的共享环境测试额外禁用外部 `DOTNET_ROOT`，检查实际加载的 CoreCLR 来自主体目录，再执行参数、生命周期及模拟路由测试。

## 2026-11-04：统一应用入口与插件设置

正式主体改为 `Hhub.App.exe --plugin <后台.dll> [参数] --hhub-stdio`，仍为每个插件单独创建后台进程，不初始化插件进程的 WinUI 窗口。保留 `Hhub.PluginHost.exe` 供已有测试工具兼容。`PluginRuntime` 的 `SharedRuntimeHostArguments` 与 `PreferSharedManagedBackends` 是通用设置；后者只接管具有 net10.0 运行配置和托管入口的旧 EXE 插件。原生或其他语言入口沿用原方式。

启动器通过 `HHUB_PLUGIN_ENTRY` 向工作子进程保留插件身份，兼容旧插件只向 `Environment.ProcessPath` 传工作参数的方式。新插件应显式传 `--plugin` 和自身 DLL。共享运行时不等于多个进程共享同一托管堆；任务管理器仍按各进程统计内存。

UI 与托管后台使用同一主程序路径，显式应用 ID 为 `Hhub.App`，后台由主体启动。上述方式用于保持应用身份和父子关系；Windows 任务管理器的分组是系统行为，不将“设置应用 ID”宣称为强制分组 API。管理员辅助进程、WebView2 或特殊语言后台仍可能由系统单独显示，需要实机验收。软件内不新增内存管理页面。

插件设置使用 `PluginUiContext.OpenSettings` / `CloseSettings` 在当前插件内容区内切换，顶栏和侧栏保留。依赖这两个新回调的插件应声明 `settings.embedded.v1`。设置由插件模块绘制，主体不再将各插件设置页集中到全局设置中。
