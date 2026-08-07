# VDict.WinUI

## 模块职责

`VDict.WinUI` 是 x64 Windows 应用、依赖注入组合根和最外层平台适配器。它负责 WinUI 3 窗口、XAML、文件/对话框/剪贴板/外部链接、窗口主题和尺寸、WebView2 生命周期、正文导航与资源拦截、诊断基线和应用关闭。

它不重新实现领域验证、Catalog 持久化或 MDict 解析，而是组合 Infrastructure 与 Presentation。

## 目录结构

- `Assets/`：应用图标、品牌图片、Dark Reader 和正文主题资源。
- `Controls/`：WebView2 阅读器宿主和候选栏分隔控件。
- `Diagnostics/`：Debug 时间线与自动化性能基线。
- `Localization/`：Core `IAppLocalizer` 的 MRT Core 实现。
- `Platform/`：Windows/Win32 平台端口实现和窗口辅助。
- `Strings/`：English/简体中文 `.resw` 资源及键命名约定。
- `Views/`：主窗口、设置窗口和设置 section。
- `App.xaml`、`App.xaml.cs`：应用资源和组合根/启动入口。
- `Package.appxmanifest`、`app.manifest`：MSIX 身份、Windows 版本、DPI 和长路径声明。
- `VDict.WinUI.csproj`：WinExe、WinUI、MSIX/Portable 配置、x64 和内容资源。

## 核心组件

- `App`：检测运行模式、注册 singleton/scoped 服务、初始化 Catalog 和释放资源。
- `MainWindow`：查询控件、候选词典选择、历史路径、响应式布局和设置入口。
- `SettingsWindow`：分区编辑、自适应导航、统一下拉框指针输入、响应式词典
  主从面板、稳定词典列表、导入导出、缓存维护、索引重建和保存/关闭协调。
- `ReaderWebViewHost`：WebView2 初始化、内部 scheme、shell/frame/资源响应、安全事件、主题、缩放和进程恢复。
- `ModalWindowService`：创建设置窗口 scope、设置原生 owner/modal 状态，并暂时隐藏 WebView2 composition surface。
- `WindowsAppEnvironment`：检测 package identity，选择打包或便携数据路径。

## 工作流程

`App` 构造函数先确定数据路径并用 `StartupLanguageReader` 读取 `ui_language`。
显式中文/English 直接生效；`System` 由 `ApplicationLanguageResolver` 根据
`CultureInfo.CurrentUICulture` 解析。解析后的受支持语言在 `InitializeComponent`
前写入 `PrimaryLanguageOverride`；随后注册依赖。`OnLaunched` 异步创建并激活
主窗口、确保数据目录、初始化 Catalog、应用设置，再处理可选启动查询或诊断参数。
WebView2 直到控件具有可用布局才初始化，随后注册 `vdict` 和 `vdict-doc` scheme
以及所有导航、资源、消息、下载、权限和进程失败事件。

XAML 静态文本通过 `x:Uid` 读取 `.resw`，代码动态消息通过 `MrtAppLocalizer`
读取同一资源图。语言设置只在“阅读与外观”中提供，持久化后提示重启；默认
`System` 并跟随 Windows 显示语言，所有 `zh-*` 使用 `zh-CN`，其他未支持语言
回退 English。MSIX 与 Portable 使用相同解析规则。

设置页 `ComboBox` 使用统一的最小宽度和 40 DIP 点击高度。指针按下时通过
`CancelDirectManipulations` 结束外层 `ScrollViewer` 尚未完成的平移，不模拟点击
或直接切换弹出状态。词典页在 820 DIP 以上使用不同系统主题表面的左右主从面板，
低于阈值时将详情面板移到词典库卡片下方。

设置页的可编辑词典名称、全局字体和词典字体在输入时立即更新设置草稿并刷新“保存”状态；
`NumberBox` 继续在数值完成解析时更新。按回车或点击当前编辑器之外的非交互空白区域会先
确认待处理输入，再将焦点移出编辑器；输入框内部、其他交互控件和只读多行路径框保持原有行为。

正文提交经过 `ReaderRenderPipeline`，先停止旧媒体，再绑定当前 `ReaderDocument` 和资源授权，最后导航到内部 shell URI。shell 按需请求 frame 与本地资源；所有真实网络导航默认被拦截，外部链接按用户设置和可信激活处理。

关闭时主窗口保存候选栏宽度、Reader 刷新缩放、事件解绑，DI 容器异步释放 Catalog、Store、日志和 WebView2 资源。

## 输入与输出

- 输入：WinUI 事件、键盘/鼠标、文件选择、命令行、WebView2 导航/资源/Web 消息、Catalog 提交事件。
- 输出：窗口和正文 UI、系统文件/链接操作、设置/维护调用、日志、诊断报告、截图和状态消息。
- 副作用：创建窗口、调用 Win32/Windows App SDK、启动系统浏览器、访问剪贴板、读写 WebView2 profile，并在明确授权时下载文件。

## 依赖关系

### 上游依赖

- Windows 应用激活、用户输入和 build/smoke 脚本。

### 下游依赖

- [`VDict.Infrastructure`](../VDict.Infrastructure/README.md) 和 [`VDict.Presentation`](../VDict.Presentation/README.md)。
- Microsoft.WindowsAppSDK、Microsoft.Web.WebView2、Microsoft.Extensions.DependencyInjection。
- Windows 10+、Win32、Edge WebView2 Runtime。

## 配置

- 目标平台：`net10.0-windows10.0.17763.0`、x64、`win-x64`。
- `Debug`/`Release` 使用 MSIX；`Portable` 使用 unpackaged、自包含 Windows App SDK/.NET。
- 支持资源语言：`en-US`、`zh-CN`；默认资源语言为 `en-US`。
- WebView2 最低运行时版本：`120.0.2210.0`。
- 命令行：`--lookup`、`--remediation-baseline-output`、`--remediation-baseline-samples`、`--diagnostic-crash-browser`。
- 环境变量：`VDICT_DISABLE_BACKGROUND_RENDER_PIPELINE`、`VDICT_DISABLE_DYNAMIC_THEME`、`VDICT_READER_CAPTURE`、`VDICT_WINDOW_TRACE`，以及 Core 的 `VDICT_DEBUG_BREAK_STAGE`。这些变量主要用于诊断/兼容性，不是普通用户设置。

## 错误处理

启动期间不可写的便携数据目录显示致命错误并关闭；其他启动错误写日志并将 Reader 标记为 failed。WebView2 缺失或过旧时显示安装和重试入口。GPU 进程失败可降级继续，浏览器/渲染进程失败会重建或重载最新文档。设置窗口异常恢复 owner 可用状态并记录日志。

## 测试

WinUI 没有独立 xUnit 项目。其纯规则由 Core/Presentation 测试覆盖；窗口、输入、设置和 WebView2 行为由 [`build/`](../../build/README.md) 的 UI Automation 与 runtime smoke 脚本验证。

构建：

```powershell
dotnet build VDict.sln --configuration Debug --no-restore
.\build\Test-LocalizationResources.ps1
```

## 修改注意事项

- DI 生命周期变化要检查 scoped 设置窗口和 singleton 主窗口/WebView2 宿主。
- WebView2 scheme、导航、消息、下载、权限和资源处理属于安全边界，需同步 Core/Presentation 测试和 UI 冒烟。
- XAML 自动化名称和布局变化可能破坏 PowerShell UI Automation。
- 打包身份、资源复制、manifest、图标或最低 WebView2 版本变化时同步发布/便携文档。
- UI 线程操作必须保持 dispatcher 和 `ConfigureAwait` 语义。
- 新增用户可见文本时必须同时增加 English/中文资源，并运行资源完整性脚本；专有名词、路径、URL、用户输入和词典正文不翻译。

## 文档同步要求

入口、DI、窗口、平台适配、命令行、环境变量、WebView2 或打包配置变化时，更新本文件、[`src/README.md`](../README.md)、[`build/README.md`](../../build/README.md) 和 [`docs/ARCHITECTURE.md`](../../docs/ARCHITECTURE.md)。

## 相关文档

- [仓库开发约束](../../AGENTS.md)
- [生产源代码](../README.md)
- [构建与冒烟脚本](../../build/README.md)
- [便携版说明](../../docs/PORTABLE.md)
