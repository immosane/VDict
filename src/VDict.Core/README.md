# VDict.Core

## 模块职责

`VDict.Core` 是最内层的领域与策略程序集。它定义 VDict 在不依赖 WinUI、WebView2、SQLite 或具体文件布局时仍成立的模型、契约和纯逻辑。

该模块负责词典与设置模型、Catalog 操作模型、资源/网络安全规则、查询并发状态、历史与导航状态、设置比较/合并/验证，以及词典正文 HTML/CSS 转换。它不负责文件 I/O、MDX/MDD 解压、SQLite、窗口或 WebView2 实例。

## 目录结构

- `Catalog/`：`IDictionaryCatalog` 契约和快照/提交事件。
- `Environment/`：应用环境、数据路径、日志和字体服务接口及纯诊断辅助。
- `Localization/`：不依赖 UI 框架的用户文案本地化契约。
- `Models/`：词典、设置、查询、正文和提交模型。
- `Policies/`：并发、历史、路径、网络、Web 消息、缓存与缩放策略。
- `Settings/`：设置规范化、比较、变更分类和三方合并。
- `Text/`：HTML/CSS 扫描、URL 重写、正文描述和渲染模板。
- `Properties/AssemblyInfo.cs`：向测试程序集开放内部成员；不构成独立业务模块。
- `VDict.Core.csproj`：平台无关领域程序集项目文件。

## 核心组件

- `ApplicationSettings`、`DictionaryDefinition`、`ReaderDocument`：跨层不可变数据模型。
- `ApplicationLanguage`、`ApplicationLanguageResolver`、`IAppLocalizer`：界面语言
  设置、Windows UI 语言到受支持资源的解析和跨层动态文案边界。
- `IDictionaryCatalog`：查询、建议、资源、设置准备/提交和轻量外观补丁的端口。
- `ReaderDocumentBuilder`、`ReaderHtmlRenderer`：把查询结果转换为受控的 shell 与词典 frame。
- `ResourcePathPolicy`、`DictionaryNetworkPolicy`、`DictionaryWebMessageValidator`：正文资源和 WebView2 安全边界。
- `LatestWinsWorkQueue<T>`、`ReaderRenderPipeline`、`ReaderNavigationState`：异步取消与最新结果提交规则。
- `ApplicationSettingsNormalizer`、`SettingsThreeWayMerger`：设置验证、默认化与冲突处理。

## 工作流程

查询结果由 Infrastructure 以 `LookupBatchResult` 返回后，Presentation 使用 `ReaderDocumentBuilder.Describe` 绑定设置、Catalog generation 和资源 capability，再使用 `Build` 重写词典 HTML/CSS 并生成 `ReaderDocument`。WinUI 随后使用 `ReaderHtmlRenderer` 构造 shell 与 frame 文档。

设置草稿先经 `ApplicationSettingsNormalizer` 验证，`SettingsChangeClassifier` 判定是否需要 Catalog reload；并发保存冲突由 `SettingsThreeWayMerger` 按字段和词典 ID 合并或报告冲突。

Core 不读取 `.resw`。需要向用户展示的验证、正文空状态或路径辅助文本接收 `IAppLocalizer` 或本地化文本模型；未注入时使用稳定资源键/English 安全默认，生产组合根始终注入 MRT Core 实现。

## 输入与输出

- 输入：用户查询文本、词典/设置快照、原始词条 HTML、Web 消息 JSON、资源引用、导航与缩放事件。
- 输出：不可变查询/正文/设置模型、规范化资源引用、安全判定、验证错误、设置变更集和渲染 HTML。
- 副作用：绝大多数类型无 I/O 副作用；诊断探针可写入注入的 `IAppLogger`，并发队列管理任务与取消令牌。

## 依赖关系

### 上游依赖

- [`VDict.Infrastructure`](../VDict.Infrastructure/README.md) 实现 Catalog、环境和格式边界。
- [`VDict.Presentation`](../VDict.Presentation/README.md) 使用模型、策略和 Catalog 契约。
- [`VDict.WinUI`](../VDict.WinUI/README.md) 使用网络、渲染和窗口无关策略。

### 下游依赖

仅依赖 .NET BCL；项目文件没有 `PackageReference` 或其他 `ProjectReference`。

## 配置

公共目标框架、nullable、隐式 using、warnings-as-errors 和确定性构建来自根目录 [`Directory.Build.props`](../../Directory.Build.props)。`DebugLookupProbe` 可读取 `VDICT_DEBUG_BREAK_STAGE`；`ReaderViewModel` 使用的后台渲染开关定义在 Presentation。

## 错误处理

纯策略优先返回 `bool`、枚举、结果 record 或 `SettingsValidationResult`，避免用异常表达普通无效输入。构造函数和明确的编程错误使用参数异常；异步策略通过 `OperationCanceledException` 传播取消。安全路径解析对越界、物理盘符和非法字符直接拒绝。

## 测试

[`VDict.Core.Tests`](../../tests/VDict.Core.Tests/README.md) 覆盖模型、设置、并发队列、导航、网络与资源安全、HTML/CSS 转换和渲染模板。

```powershell
dotnet test tests\VDict.Core.Tests\VDict.Core.Tests.csproj --configuration Release --no-restore
```

## 修改注意事项

- 保持无 WinUI/WebView2/SQLite 依赖。
- 增加模型字段时同步比较器、规范化器、变更分类、设置 schema 与测试。
- 改变 URL、capability、Web 消息或激活规则属于安全边界变更，必须同时检查 Core、Presentation、WinUI 和相关测试。
- 所有长循环应保留有界输入或取消检查，避免词典内容触发无界内存/CPU。

## 文档同步要求

公共模型、契约、策略、配置开关或正文处理流程变化时，更新本文件、[`src/README.md`](../README.md) 和 [`docs/ARCHITECTURE.md`](../../docs/ARCHITECTURE.md)。

## 相关文档

- [仓库开发约束](../../AGENTS.md)
- [生产源代码](../README.md)
- [Core 测试](../../tests/VDict.Core.Tests/README.md)
