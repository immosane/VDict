# VDict.Presentation.Tests

## 模块职责

本项目验证 Presentation 的平台端口适配、设置草稿和 ViewModel 状态机。当前包含 70 个 `[Fact]` 或 `[Theory]` 声明，不创建真实 WinUI 窗口或 WebView2。

## 目录结构

- `Services/`：`ReaderWebResourceAdapter` 授权、范围和维护行为。
- `Settings/`：编辑会话与 section 缓存。
- `TestDoubles/`：fake Catalog 和 dialog。
- `ViewModels/`：Reader 与设置窗口状态、竞态和命令。
- `GlobalUsings.cs`：xUnit 全局 using。

## 工作流程

测试直接引用 [`VDict.Presentation`](../../src/VDict.Presentation/README.md)，通过 fake `IDictionaryCatalog`、dialog 和立即 dispatcher 控制异步路径。候选/查询/保存竞态使用可控任务，确保只提交最新状态。

## 测试

```powershell
dotnet test tests\VDict.Presentation.Tests\VDict.Presentation.Tests.csproj --configuration Release --no-restore
```

## 修改注意事项

- 新平台副作用应先定义接口并提供 test double。
- ViewModel 测试同时断言数据、命令可用性、状态消息和取消后的不变性。
- 资源适配测试必须覆盖过期 generation、错误 frame/capability、范围请求和维护 drain。

## 文档同步要求

测试 double、测试分类、ViewModel 状态或资源适配覆盖变化时，更新本文件、[`tests/README.md`](../README.md) 和相关 Presentation 文档。

## 相关文档

- [仓库开发约束](../../AGENTS.md)
- [测试总览](../README.md)
- [Presentation 模块](../../src/VDict.Presentation/README.md)
