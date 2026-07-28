<p align="center">
  <img src="src/VDict.WinUI/Assets/logo_round.png" alt="VDict Logo" width="128">
</p>

<h1 align="center">VDict</h1>

<p align="center"><strong>Windows 本地离线 MDX/MDD 词典阅读器</strong></p>

<p align="center">
  <a href="../../releases/latest">下载最新版</a> ·
  <a href="docs/PORTABLE.md">使用说明</a> ·
  <a href="README.en.md">English</a>
</p>

VDict 使用 WinUI 3 和 WebView2 展示词典正文，通过 SQLite 建立本地候选索引，
支持快速检索和多词典查询。词典、查询记录与设置均保存在本机，无需账号或云端服务。

![VDict 主界面](docs/images/main-window.png)

## 功能特点

- **MDX/MDD 阅读**：加载 `.mdx` 词典及配套 `.mdd` 图片、音频等资源。
- **快速查询**：使用本地索引即时显示候选词，支持精确查询、词条重定向和查询历史。
- **多词典查询**：可同时管理和查询多部词典。
- **现代化界面**：支持浅色、深色和系统主题，并可调整字体与正文显示。
- **本地优先**：无需账号，不含遥测和云同步，数据保存在本机。

## 快速开始

1. 从 [Releases](../../releases/latest) 下载名称以 `win-x64-portable.zip` 结尾的便携包。
2. 将压缩包完整解压到可写目录。
3. 运行 `VDict.exe`。
4. 打开“设置 → 词典”，添加 `.mdx` 文件。

> VDict 不提供或分发词典文件。配套 `.mdd` 资源通常应与 `.mdx` 文件放在同一目录。

## 兼容性与要求

| 项目 | 状态 |
| --- | --- |
| Windows 10 1809 或更高版本，x64 | 支持 |
| MDX 词典与 MDD 资源 | 支持 |
| 图片、音频、样式和相对资源 | 支持 |
| 依赖脚本或外部内容的词典 | 可能受安全策略限制 |
| WebView2 Evergreen Runtime | 需要 `120.0.2210.0` 或更高版本 |

不同 MDX/MDD 生成工具的扩展行为可能存在兼容性差异。

## 隐私与数据

- VDict 不包含遥测、账号、云同步或远程数据库。
- 查询词、词典内容、设置和本地路径不会由应用上传。
- 设置、索引、缓存、日志和 WebView2 数据保存在程序旁的 `Data/` 目录。
- 升级时保留 `Data/` 目录即可继续使用原有数据。

词典正文中的脚本、外部导航、下载和 Web 消息受到安全策略限制，外部操作需要明确的用户交互。
更多信息见[便携版文档](docs/PORTABLE.md)。

## 反馈与许可

- 遇到错误或有功能建议，可以在 [Issues](../../issues) 中反馈。
- 版本变化见[变更日志](CHANGELOG.md)。
- VDict 使用 [MIT License](LICENSE)，第三方组件及许可证见
  [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

VDict 由作者借助 AI 辅助开发。最终的功能取舍、内容审核和版本发布由项目维护者负责。
