# 开发工具

## 模块职责

`tools/` 保存不进入生产启动路径的开发与测试辅助程序。当前只有离线、确定性的 MDX/MDD remediation fixture 生成器。

## 目录结构

- [`VDict.RemediationFixture/`](VDict.RemediationFixture/README.md)：生成、验证并描述用于基础设施回归测试的模拟词典。

## 工作流程

工具项目包含在 `VDict.sln` 中，因此参与 solution build。`Run-RemediationPhase0A.ps1` 通过 .NET CLI 运行生成器，再执行使用该 fixture 的 Infrastructure 测试。

## 输入与输出

工具默认向 `artifacts/remediation-fixture` 写入生成的 MDX、MDD、资源、manifest 和哈希。`artifacts/` 被 `.gitignore` 排除；生成物不是源代码，也不应添加目录文档。

## 依赖关系

### 上游依赖

- [`build/Run-RemediationPhase0A.ps1`](../build/README.md)。
- [`VDict.Infrastructure.Tests`](../tests/VDict.Infrastructure.Tests/README.md)。

### 下游依赖

- [`VDict.Infrastructure`](../src/VDict.Infrastructure/README.md)，用于按生产格式规则回读和验证 fixture。

## 测试

```powershell
.\build\Run-RemediationPhase0A.ps1
```

## 修改注意事项

- 生成必须可重复，语义 manifest 与文件 SHA-256 必须同时更新和验证。
- fixture 特性应覆盖真实格式边界，但不得包含受版权保护的真实词典内容或用户路径。
- 改变 manifest schema、生成器版本或输出布局时同步更新 Infrastructure 测试和文档。

## 文档同步要求

工具清单、入口、输出、依赖或用途变化时，更新本文件、工具自身文档、[`tests/README.md`](../tests/README.md) 和 [`build/README.md`](../build/README.md)。

## 相关文档

- [仓库开发约束](../AGENTS.md)
- [文档中心](../docs/README.md)
- [测试总览](../tests/README.md)
