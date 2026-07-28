# VDict.Core.Tests

## 模块职责

本项目验证 Core 中的平台无关模型、策略、设置算法和正文转换。当前包含 162 个
`[Fact]` 或 `[Theory]` 声明，适合作为快速回归层。

## 目录结构

- `Environment/`：诊断探针与时间线隐私/并发。
- `Localization/`：显式语言、Windows UI 语言变体和
  English 回退解析。
- `Policies/`：候选、历史、导航、资源、网络、安全、缓存与缩放策略。
- `Settings/`：规范化、变更分类和三方合并。
- `Text/`：HTML/CSS 扫描、重写、正文构建与渲染。
- `ReaderDocumentBuilderTests.cs`：跨 Text/Models 的正文组装行为。
- `GlobalUsings.cs`：xUnit 全局 using。

## 工作流程

测试直接引用 [`VDict.Core`](../../src/VDict.Core/README.md)，不启动 Windows UI、不访问真实词典，也不依赖网络。测试数据以 inline theory、内存 HTML/CSS、fake logger 和可控时间/取消令牌为主。

## 测试

```powershell
dotnet test tests\VDict.Core.Tests\VDict.Core.Tests.csproj --configuration Release --no-restore
```

主要覆盖：安全 URI/路径、Web 消息、latest-wins 取消、历史和路径、界面语言
默认/系统解析/比较/分类/合并、设置冲突、HTML 正规化、资源 URL 重写、
CSP/nonce、主题/脚本注入和文档诊断。

## 修改注意事项

- 新 Core 策略应优先在此建立边界和反例测试。
- 安全测试同时覆盖允许项、拒绝项、lookalike、越界、过期 token 和跨 frame 重放。
- 并发测试应避免固定 sleep，优先使用 `TaskCompletionSource` 或可控 gate。

## 文档同步要求

测试分类、覆盖边界或运行要求变化时，更新本文件、[`tests/README.md`](../README.md) 和对应 Core 模块文档。

## 相关文档

- [仓库开发约束](../../AGENTS.md)
- [测试总览](../README.md)
- [Core 模块](../../src/VDict.Core/README.md)
