# 自动化测试

## 模块职责

`tests/` 包含 Core、Infrastructure 和 Presentation 的 xUnit 测试。当前扫描到
299 个 `[Fact]` 或 `[Theory]` 声明：Core 162、Infrastructure 67、
Presentation 70。该计数表示测试声明数量，不等同于 Theory 展开后的实际用例数。

WinUI 窗口、原生输入和 WebView2 的端到端行为不在 xUnit 项目中直接执行，主要由
[`build/`](../build/README.md) 的 Windows UI 冒烟脚本验证；其中设置检查使用真实
指针单击覆盖下拉框首次打开，并验证词典主从面板响应式关系。

## 目录结构

- [`VDict.Core.Tests/`](VDict.Core.Tests/README.md)：模型、策略、设置与 HTML/CSS 处理的快速测试。
- [`VDict.Infrastructure.Tests/`](VDict.Infrastructure.Tests/README.md)：MDict、索引、Catalog、设置存储、环境和缓存的集成测试。
- [`VDict.Presentation.Tests/`](VDict.Presentation.Tests/README.md)：资源适配器、设置编辑会话和 ViewModel 测试。

## 工作流程

测试项目直接引用对应生产项目，并由 `Microsoft.NET.Test.Sdk`、xUnit、Visual Studio runner 和 coverlet collector 承载。公共目标框架与 warnings-as-errors 来自根目录 `Directory.Build.props`。

标准流程：

```powershell
dotnet restore VDict.sln
dotnet test VDict.sln --configuration Release --no-restore
.\build\Test-LocalizationResources.ps1
```

单项目示例：

```powershell
dotnet test tests\VDict.Core.Tests\VDict.Core.Tests.csproj --configuration Release --no-restore
```

确定性 MDX/MDD 基座回归：

```powershell
.\build\Run-RemediationPhase0A.ps1
```

## 输入与输出

测试使用内存 fake、临时目录、生成的 JSON/SQLite/MDX/MDD 数据和离线 fixture。正常测试不应依赖真实用户词典或公网服务。输出目录 `bin/`、`obj/`、`TestResults/` 和覆盖率文件均是生成物，不提交版本库。

部分 Windows 环境与文件锁测试依赖当前操作系统的文件系统、命名互斥体和代码页实现；结果应在 Windows 上解释。

## 错误处理

测试必须断言具体状态、错误或副作用，不能吞掉意外异常。涉及取消、竞争、损坏设置、过期提交、资源越界和缓存清理的测试应同时验证有效状态未被半更新。

## 修改注意事项

- 修改生产行为时，在最内层可验证边界增加或更新测试，再补充跨层集成测试。
- 新增测试目录时为其创建 `README.md` 并从对应测试项目文档链接。
- 测试不得读取或修改 `UserData/` 中的真实开发数据。
- 使用临时目录的测试必须清理自身产物，不得删除未经确认的路径。
- 修改 XAML 自动化名称或 WebView2 交互时，同时检查 UI 冒烟脚本。
- 本地化变更除 xUnit 模型/schema 测试外，还必须运行资源键、占位符和直接引用完整性脚本。

## 文档同步要求

测试项目、分类、命令、fixture、覆盖边界或环境要求变化时，更新本文件、对应测试项目 README 和相关生产模块文档。

## 相关文档

- [仓库开发约束](../AGENTS.md)
- [生产源代码](../src/README.md)
- [构建与冒烟脚本](../build/README.md)
- [Fixture 工具](../tools/VDict.RemediationFixture/README.md)
