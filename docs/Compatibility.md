# 开发兼容关系

主体统一预览：`0.6.0-preview.1`。公共合同与 SDK：`1.0.0-preview.1`。schema：2。协议：`hhub.plugin/1`。

插件仓库通过 `sdk.lock.json` 记录本仓库的精确提交，再构建固定版本 NuGet 包。真实插件尚未按新合同迁移；现有 schema 1 / stdio-v1 插件只属于成熟 0.5 路径，不在统一预览兼容承诺内。详见 [插件兼容矩阵](https://github.com/shengerq/HHub-Plugins/blob/main/docs/Compatibility.md)。

版本升级流程：变更接口时更新对应包版本和公开说明；插件仓库显式更新来源提交与精确 PackageReference；执行跨仓库安装、通信、预设、更新、卸载检查。稳定 SDK 发布后还必须保存已发布插件原包并回归，不能每次重新编译旧包来证明兼容。当前只有开发模拟包，不宣称已有稳定兼容保证。

`Hhub.UnifiedHost` 0.6.0-preview.1 NuGet 包仅供跨仓库集成测试使用，不属于插件公共 SDK，也不能成为真实插件运行依赖。
