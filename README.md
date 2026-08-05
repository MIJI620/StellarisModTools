# 群星模组工具 / StellarisModTools

群星（Stellaris）模组工具——星系样式与地图可视化编辑器。

A Stellaris (4.x) mod tool — visual galaxy style & map editor.

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

## 功能 / Features

- **星系样式**：创建 / 编辑 / 复制 / 删除样式，形状参数可视化调参，自动导出预览图与按钮图标（按精灵表路径落盘）
- **动态 / 静态地图**：混排列表双向切换；点集编辑器（加点 / 删除 / 框选 / 多选拖动 / 镜像 / 旋转 / 超空间航道）；加载集合（多套根目录预设，切换即重载）
- **本地化**：中 / 英一键切换（界面语言 endonym 显示）；本地化键逻辑值 / 显示值分离
- **保存 / 规整化**：统一保存（转圈进度、仅失败弹窗）；"全部规整化"只改内存、保存时统一落盘
- **导出**：按样式增量导出（哈希比较），静态地图导出设计点集

## 解析器特性 / Parser Highlights

- **相邻双引号配对**（用户实测原版确认）：通用代码文件（`.txt` / `.gfx`）中字符串从第一个 `"` 读到**下一个** `"` 即终止——`from = "07" to = "03"` 解析为**两条独立赋值**（非贪婪合并）
- **本地化 yml 行贪婪**：值 = 第一个 `"` 到行内最后一个 `"`（`LocalisationParser`，不走 Lexer，互不干扰）
- **抗爆炸**：遇到坏 token（未闭合引号 / 多余闭括号 / 非法键）不崩溃——记录错误行、跳过、继续解析（与游戏行为一致）

## 构建 / Build

```bash
dotnet build Stellaris.Editor/Stellaris.Editor.csproj -v q
dotnet run --project Stellaris.Tests/Stellaris.Tests.csproj -v q   # 38 项测试
```

## 许可 / License

MIT License（附加声明：禁止**完全原样**打包倒卖；改动任何内容即不受限）。见 [LICENSE](LICENSE)。
