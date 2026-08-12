# 群星模组工具 / StellarisModTools

**V0.2** —— 开源免费的群星（Stellaris 4.x）模组可视化编辑工具：星系样式、地图、法令/决议/静态加成、科技、本地化与资源可视化编辑。

**GitHub**：<https://github.com/MIJI620/StellarisModTools> ｜ **反馈 / 交流**：GitHub Issues

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

## 开发历程

本工具的最初开发源自 2021 年左右的一个群星舰船制作器——当时只是想做一个便捷的可视化舰船组件制作工具，后因长期未维护且性能过差而停更；2026 年重新开始开发，先尝试用 Python，再次遇到性能瓶颈，最终转向 C#。

## 导航功能 / Navigation & Features

- **综合**：法令 / 星球决议 / 静态加成 / 战略资源 4 个选项卡；法令与决议字段级保存（所属文件可指定）；静态加成字段级保存（本地化 `modifiers_{ModPrefix}_l_{lang}.yml`）
- **科技**：节点图（文本标签模式）+ 搜索 + 右键新建/修改/删除/保存/导出（分块渲染防 OOM）；字段级保存 + 格式化省略（未知字段保留）
- **地图**：壳页 2 选项卡（地图 = 动态/静态整页叠放、星系样式 = 样式页整页叠放），选项卡只包列表+搜索那一小块；右编辑区统一 420px，横/纵尺寸调整三页通用
- **索引**：语言字典（按 key 去重 + 详情本地化组件）/ 加成字典（本地化复用组件、真实键 LocKey）/ 图形索引（gfx .dds + 注册键分类 + 切帧预览）；三页列宽调整通用
- **设置**：目录集合管理（重载入该集合、导入启动器集合）、界面语言切换、模组前缀、帮助（大文本块）、关于
- **保存规范**：所有保存必须显式登记（pending），用户显式触发（右键"保存"/保存按钮）才落盘，经 SaveRunner；统一写 roots[-1] + 自动建目录

## 构建 / Build

```bash
dotnet build Stellaris.Editor/Stellaris.Editor.csproj -v q
dotnet run --project Stellaris.Tests/Stellaris.Tests.csproj -v q   # 168 项测试
```

完整公开 API 功能索引见 [README-API-INDEX.md](README-API-INDEX.md)。

## 许可 / License

MIT License（附加声明：禁止**完全原样**打包倒卖；改动任何内容即不受限）。见 [LICENSE](LICENSE)。
