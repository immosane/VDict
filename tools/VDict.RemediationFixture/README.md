# VDict.RemediationFixture

## 模块职责

本工具生成并验证离线、确定性的模拟 MDX/MDD 词典，用于回归测试 MDict 解析、词条正文、重定向、资源、CSS、脚本、安全策略和性能基线。工具产物不进入生产启动路径、用户设置或版本库。

## 目录结构

- `Program.cs`：命令行解析与退出码。
- `RemediationFixtureGenerator.cs`：生成词条、资源、MDX/MDD、manifest 和语义哈希。
- `RemediationFixtureValidator.cs`：回读文件并校验 manifest、哈希和期望。
- `MDictFixtureWriter.cs`：写入受控 MDict 容器。
- `FixtureAssets.cs`：内置的小型测试资源。
- `FixtureManifest.cs`：manifest record 模型。
- `fixture-manifest.schema.json`：JSON Schema 2020-12 结构约束。
- `VDict.RemediationFixture.csproj`：可执行项目，引用 Infrastructure。

## 核心组件

- `FixtureCommand` 支持 `generate`/`all`、`verify` 和 `--output`。
- `RemediationFixtureGenerator.GenerateAndVerify` 创建产物后立即验证。
- `RemediationFixtureValidator.Validate` 检查语义 SHA-256、文件哈希、词条与资源数量。
- `MDictFixtureWriter` 写入测试需要的 key/record block，不是通用词典编辑 API。

## 工作流程

```text
定义固定词条和资源
→ 写入 MDX/MDD
→ 生成 manifest 与文件哈希
→ 使用验证器回读
→ 输出计数和 semantic SHA-256
```

直接运行：

```powershell
dotnet run --project tools\VDict.RemediationFixture --configuration Release -- generate --output artifacts\remediation-fixture
```

验证已有目录：

```powershell
dotnet run --project tools\VDict.RemediationFixture --configuration Release -- verify --output artifacts\remediation-fixture
```

仓库推荐的生成加测试入口：

```powershell
.\build\Run-RemediationPhase0A.ps1
```

## 输入与输出

输入仅为 action 和输出目录。输出包括模拟 `.mdx`、`.mdd`、资源、`fixture-manifest.json` 及可核对哈希。默认目录位于 `artifacts/`，可安全删除并重新生成。

## 依赖关系

### 上游依赖

- [`Run-RemediationPhase0A.ps1`](../../build/README.md)。
- `VDict.Infrastructure.Tests/Fixtures` 测试目录。

### 下游依赖

- [`VDict.Infrastructure`](../../src/VDict.Infrastructure/README.md)，以生产 reader 回读和校验格式。

## 错误处理

任何未知参数、写入失败、schema/哈希/语义不匹配都会输出错误并返回退出码 1。帮助参数输出用法并退出 0。输出目录应由调用者隔离；工具不会把生成物加入 Git。

## 测试

Infrastructure fixture 测试验证生成可重复性、manifest 结构、资源和生产 reader 回读。

## 修改注意事项

- 保持固定顺序和确定性；随机值、时间戳或机器路径不得进入语义哈希。
- 更新 fixture 行为时同步提升生成器版本、schema、validator 和测试期望。
- 不复制真实词典内容、密钥、用户路径或网络资源。

## 文档同步要求

命令、manifest、生成器版本、覆盖能力或输出布局变化时，更新本文件、[`tools/README.md`](../README.md)、[`build/README.md`](../../build/README.md) 和 fixture 测试文档。

## 相关文档

- [仓库开发约束](../../AGENTS.md)
- [开发工具](../README.md)
- [Infrastructure 测试](../../tests/VDict.Infrastructure.Tests/README.md)
