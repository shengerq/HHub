# 双仓库边界与当前验证范围

整理日期：2026-11-09。两个仓库均为私有开发测试仓库。原始 `Hhub`、`gamehub` 未移动、未删改，gamehub 的 Git 历史和未提交改动保留原状。

## 主体仓库 HHub

- `src`：成熟 0.5 主体、Core、PluginUi 与共享启动器，以及统一插件预览主体、独立合同与 SDK。保留旧功能实现作为迁移依据，不增加新旧协议通用适配层。
- `sdk/v1`：1.0.0-preview.1 合同说明与结构 schema；schemaVersion=2、协议 `hhub.plugin/1`。该版本未冻结为稳定 SDK。
- `examples`：SDK 文档示例；独立插件仓库中的同源模拟示例使用精确版本的 NuGet SDK，作为跨仓库验收对象。
- `tests`：Core、Platform、UnifiedHost 检查。基础 CI 执行 UnifiedHost 检查，构建现有主体；平台设备检查和旧 Core 全量运行单独记录，不把编译等同于实机通过。
- `tools`、`installer`：主体发布、SDK 打包、认证检查及安装器源码。CI 产物仅为开发预览，不自动创建正式 Release、项目证书或正式认证包。
- `docs/import-inventory.json`：导入来源与导入时 SHA-256；后续修改由 Git 跟踪，该文件不是当前文件摘要。

## 插件仓库 HHub-Plugins

`plugins` 保存新合同插件；`legacy/plugins` 保留现有八类插件的全部业务源码、清单、界面、许可和源码素材，`legacy/tests`、`legacy/tools` 保存对应检查及打包脚本。未通过修改旧清单或批量改名宣称已迁移。

旧方案依赖主体内部 Core / PluginUi，不能作为新 SDK 模式。插件仓库用固定主体提交构建一个忽略的旧版组合工作区，保留历史构建入口，供迁移核对。新插件只引用版本化公共 SDK。

GameHub V3 合同、实现及测试、旧 GameHUB 的插件、协议、研究和变更说明精选在 `reference`，只供参考，不加载其旧包体系。原始完整资料、厂商程序和历史构建仍在原工作区。第三方来源和固定版本另见插件仓库的 `docs/upstream-sources.json`。

## 功能与迁移约束

系统监测负责主页数据；耳机通过音频处理能力协作 FxSound；手机远控依赖主体状态、配对与场景控制；场景依赖统一控制与持久配置。拆仓不表示删掉这些关系。现有功能先保留，再按统一合同逐项重写；跨插件服务路由尚未完成，不伪报依赖已经迁移成功。

固件保持原有版本，暂不开发固件升级。轻量通过按需安装、按需运行、共享运行组件和可卸载模块实现。主题包仅保留提案。

## 忽略和来源

不导入密钥、个人用户数据、赞助收款二维码、运行日志、缓存、bin/obj、历史 artifacts、厂商安装器、驱动二进制或 SDK 归档。构建需要的 FxSound DSP 源码、预设和许可保留；已签名驱动从固定上游提交恢复到忽略目录，由原打包脚本再次验签。没有删除原始输入。

`Test-Hygiene.ps1` 检查仓库路径、8 MiB 文件上限和常见凭据模式。它不构成完整安全审核。现有第三方许可保留；没有替作者改变许可，也没有为全部组合产物新增统一开源许可。正式分发前仍需核对全部许可证义务。

## 开发检查

Windows x64，使用 global.json 指定的 .NET 10 SDK：

```powershell
./tools/Test-Repository.ps1 -DotnetPath (Get-Command dotnet).Source
./tools/Build-UnifiedPreview.ps1 -DotnetPath (Get-Command dotnet).Source
```

前者构建新解决方案、运行统一协议及生命周期检查、编译成熟主体并打包精确版本 SDK；后者发布不含可选插件的空主体。`-WithSimulation` 另生成模拟验收包，其临时签名不等于正式项目认证。源码中的旧验收记录是历史证据，本轮结果单独记录。
