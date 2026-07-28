# VDict.Presentation

## 模块职责

`VDict.Presentation` 将 Core 的模型和 Catalog 契约组织为可绑定、可测试的 UI 状态。它负责主窗口/阅读器/设置窗口 ViewModel、latest-wins 查询协调、候选防抖、历史与查询路径、设置草稿和平台能力接口。

该模块不创建窗口、不调用 Win32、不持久化文件，也不持有 WebView2 类型。

## 目录结构

- `Collections/`：面向绑定的集合批量重置辅助。
- `Services/`：窗口/平台端口及 WebView2 资源请求适配器。
- `Settings/`：设置编辑会话和按 section 缓存。
- `ViewModels/`：主窗口、阅读器和设置窗口状态/命令。
- `VDict.Presentation.csproj`：引用 Core、CommunityToolkit.Mvvm 和 DI abstractions。

## 核心组件

- `ReaderViewModel`：查询、候选、历史、路径、缩放和 `ReaderDocument` 状态。
- `SettingsEditSession`：不可变设置草稿、dirty section、验证、prepare/commit 和冲突重试。
- `SettingsWindowViewModel`：设置窗口状态机、保存/关闭和操作进度。
- `ReaderWebResourceAdapter`：把 WebView2 请求模型转换为受 Catalog generation 与 frame 身份约束的资源解析。
- `IDialogService`、`IFilePickerService`、`IModalWindowService`、`IClipboardService`、`IExternalLinkService`、`IUiDispatcher`：外层平台端口。
- Core `IAppLocalizer`：为候选、查询、保存、关闭确认和进度状态提供资源键解析，不引入 WinUI/MRT Core 依赖。

## 工作流程

搜索文本变化时，`ReaderViewModel` 在 80 ms 默认防抖后请求候选，并使用版本、选中词典和 Catalog generation 拒绝过期响应。正式查询进入 `LatestWinsWorkQueue`，读取 Catalog、构建 `ReaderDocument`，仅最新 request 和 generation 可以提交到 UI。

Reader 的候选、查询、路径辅助和无结果页文案由注入的本地化器生成；词条、用户输入、词典名称和正文不翻译。

设置窗口创建独立 `SettingsEditSession`。编辑只更新草稿；保存先验证并调用 `PrepareUpdateAsync`，再提交。如磁盘或运行时设置已变化，则按 base/current/draft 三方合并，冲突字段明确返回用户。

## 输入与输出

- 输入：用户查询/导航/缩放意图、平台服务结果、Catalog 快照与提交事件、设置编辑动作、Web 资源请求。
- 输出：可绑定属性、命令状态、候选集合、`ReaderDocument`、设置提交结果、资源响应和窗口状态。
- 副作用：通过抽象端口请求对话框、文件选择、剪贴板、外部链接和 UI dispatcher；自身不直接访问平台。

## 依赖关系

### 上游依赖

- [`VDict.WinUI`](../VDict.WinUI/README.md) 创建 ViewModel 并实现所有平台端口。

### 下游依赖

- [`VDict.Core`](../VDict.Core/README.md)。
- CommunityToolkit.Mvvm。
- Microsoft.Extensions.DependencyInjection.Abstractions。

## 配置

- `ReaderViewModel` 默认候选防抖 80 ms、缩放持久化防抖 350 ms，可在测试构造时覆盖。
- 环境变量 `VDICT_DISABLE_BACKGROUND_RENDER_PIPELINE=1` 关闭后台 latest-wins 文档构建，走兼容路径。
- Presentation 不读取设置文件路径或 WebView2 配置。

## 错误处理

候选加载失败会保留可继续输入的 UI 状态并给出状态消息。查询取消和过期响应不会覆盖新结果。设置保存错误写入 `SaveError`；取消保留草稿，普通异常转换为失败结果。资源适配器对未授权 frame、过期 generation、维护窗口或无效范围返回受控 HTTP 风格状态。

## 测试

[`VDict.Presentation.Tests`](../../tests/VDict.Presentation.Tests/README.md) 使用 fake Catalog、dialog 和 dispatcher 覆盖查询竞态、设置会话、窗口状态与资源适配。

```powershell
dotnet test tests\VDict.Presentation.Tests\VDict.Presentation.Tests.csproj --configuration Release --no-restore
```

## 修改注意事项

- 不得引入 WinUI/WebView2 具体类型。
- 长任务必须可取消，并在提交 UI 前复核 request/generation。
- 新设置编辑字段要更新 dirty section、默认恢复、三方合并、XAML 绑定和测试。
- 平台副作用必须通过接口注入，以保留测试能力。

## 文档同步要求

ViewModel 状态、命令、平台端口、设置保存或资源授权流程变化时，更新本文件、[`src/README.md`](../README.md) 和 [`docs/ARCHITECTURE.md`](../../docs/ARCHITECTURE.md)。

## 相关文档

- [仓库开发约束](../../AGENTS.md)
- [生产源代码](../README.md)
- [Presentation 测试](../../tests/VDict.Presentation.Tests/README.md)
