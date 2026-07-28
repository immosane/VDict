# VDict.Infrastructure.Tests

## 模块职责

本项目验证 Infrastructure 的文件格式、SQLite、Catalog、设置存储、缓存维护和环境行为。当前包含 67 个 `[Fact]` 或 `[Theory]` 声明，其中多项为临时目录和生成 fixture 驱动的集成测试。

## 目录结构

- `Catalog/`：Catalog 事务、Store、资源和缓存维护集成。
- `DictionaryFormats/`：MDict/MDD 路由、资源索引、编码与哈希。
- `Environment/`：便携/打包路径、日志和环境服务。
- `Fixtures/`：浏览器资源与 remediation fixture。
- `Indexing/`：确定性索引路径。
- `Settings/`：原子存储、迁移、恢复和并发。
- `GlobalUsings.cs`：xUnit 全局 using。

## 工作流程

测试引用 [`VDict.Infrastructure`](../../src/VDict.Infrastructure/README.md) 和 [`VDict.RemediationFixture`](../../tools/VDict.RemediationFixture/README.md)。测试在临时目录创建设置、SQLite、MDX/MDD 和资源，验证后自行清理；不读取 `UserData/` 或真实用户词典。

## 测试

```powershell
dotnet test tests\VDict.Infrastructure.Tests\VDict.Infrastructure.Tests.csproj --configuration Release --no-restore
```

Fixture 专项：

```powershell
.\build\Run-RemediationPhase0A.ps1
```

本地化资源完整性由独立 PowerShell 检查覆盖：

```powershell
.\build\Test-LocalizationResources.ps1
```

## 错误处理

测试需验证失败不会替换活动 Catalog、覆盖新设置或删除批准范围外文件。涉及 Windows 文件锁和命名互斥体的用例必须保持超时有界并正确释放资源。

## 修改注意事项

- 新 MDict 支持必须补充损坏/超大/边界数据测试，避免仅验证 happy path。
- Catalog 测试应检查 revision、generation、Store 租约和磁盘状态的一致性。
- 临时目录清理只能针对测试创建的确定路径。

## 文档同步要求

测试分类、fixture、环境依赖或运行命令变化时，更新本文件、[`tests/README.md`](../README.md) 和相关 Infrastructure 文档。

## 相关文档

- [仓库开发约束](../../AGENTS.md)
- [测试总览](../README.md)
- [Infrastructure 模块](../../src/VDict.Infrastructure/README.md)
- [Fixture 工具](../../tools/VDict.RemediationFixture/README.md)
