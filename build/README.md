# 构建、发布与冒烟脚本

## 模块职责

`build/` 保存源代码控制下的 PowerShell 自动化脚本，负责发布制品、生成测试设置与词典基座、运行 Windows UI 冒烟检查，以及汇总性能和资源读取消耗。虽然目录名为 `build`，这里不是编译输出目录；实际生成物写入被忽略的 `artifacts/` 或调用者指定目录。

本模块不实现生产业务逻辑，也不作为应用启动时依赖。

## 目录结构

| 脚本 | 用途 |
|---|---|
| `Publish.ps1` | restore、Release 测试、自包含便携 ZIP 和 SHA-256 清单 |
| `Test-LocalizationResources.ps1` | 校验中英文资源键、格式化占位符及代码/XAML 直接资源引用 |
| `Run-RemediationPhase0A.ps1` | 生成/验证确定性 MDX/MDD 基座并运行相关集成测试 |
| `CreateSmokeFixture.ps1` | 为便携版创建多场景模拟词典和设置 |
| `CreateOxfordSmokeSettings.ps1` | 从现有设置选取真实词典，生成 Oxford 冒烟设置 |
| `RuntimeSmoke.ps1` | 启动便携版并通过 Windows UI Automation 验证查询、布局、设置、下拉框首次指针单击、词典双面板/列表容器稳定性和截图 |
| `Run-UiSmoke.ps1` | 对已运行进程执行窗口、WebView 和输入命中测试 |
| `Run-SettingsPhase4Smoke.ps1` | 对已运行进程验证设置窗口交互 |
| `Probe-WebViewInput.ps1` | 检查 WebView 子窗口、前台状态和输入命中 |
| `Measure-RemediationPhase0B.ps1` | 发布隔离便携版并采集冷/热查询与 UI 性能基线 |
| `Measure-RemediationPhase5B.ps1` | 聚合资源范围读取、内存和延迟基线并应用阈值 |

## 工作流程

发布流程由 `Publish.ps1` 固定为：

1. 校验所有清理目标位于 `artifacts/`。
2. `dotnet restore VDict.sln`。
3. `dotnet test VDict.sln --configuration Release --no-restore`。
4. 以 `Portable` 配置发布自包含 `win-x64` 目录。
5. 将便携说明、VDict 许可证和第三方声明复制到便携目录，再压缩为 ZIP。
6. 为便携 ZIP 生成 `SHA256SUMS.txt`。

Phase 0A 流程先通过 `VDict.RemediationFixture` 生成并自校验离线词典，再筛选运行 `RemediationFixtureTests`。其他 UI 脚本通常要求已有便携发布目录或正在运行的 VDict 进程，并会调用 UI Automation、Win32 API、WebView2 诊断或截图。

## 输入与输出

- `Publish.ps1 -Version <major.minor.patch>` 输出
  `artifacts/VDict-<version>-win-x64-portable.zip` 和 `artifacts/SHA256SUMS.txt`。
- 受限离线环境可显式传入 `-SkipAudit`，但该输出不得作为正式发行验证结果；CI 和
  正式发布前仍必须完成 NuGet 漏洞审计。
- `Run-RemediationPhase0A.ps1` 默认输出 `artifacts/remediation-fixture`；受限离线环境可
  显式传入 `-SkipAudit`，但正式验证仍须完成联网漏洞审计。
- 性能脚本默认输出到 `artifacts/remediation-*`。
- `RuntimeSmoke.ps1` 接受便携目录、查询、预期正文、坐标、截图目录、窗口参数和
  `ExpectedUiLanguage`（`zh-CN` 或 `en-US`），并可通过 `SettingsWidthDip` 与
  `SettingsHeightDip` 在支持范围内选择设置窗口验证尺寸。界面语言参数用于匹配本地化后的
  Automation 名称；它不修改应用设置，调用前应让测试设置中的 `ui_language`
  与预期语言一致。设置检查还会确认 Add MDX 主操作可见；存在至少两个词典时，
  会切换选择并验证 ListView 项容器未被重建。脚本还使用真实鼠标单击验证语言、
  主题、外部链接和词典高级暗色模式下拉框一次打开，检查词典库/详情面板在宽窗口
  左右分离、窄窗口上下分离，并输出对应尺寸的词典设置页截图。
- `Run-UiSmoke.ps1`、`Run-SettingsPhase4Smoke.ps1`、`Probe-WebViewInput.ps1` 接收目标进程 ID。
  `Run-SettingsPhase4Smoke.ps1` 还会验证设置字体与词典名称的即时脏状态检测，以及可编辑
  TextBox、AutoSuggestBox 和 NumberBox 在回车或点击非交互空白区域后的提交与焦点结束行为；
  测试最后通过取消关闭设置窗口，不持久化草稿。
- `CreateOxfordSmokeSettings.ps1` 会读取调用者提供的设置文件中的真实词典路径；不得将这些路径或生成设置提交到仓库。

脚本可能创建或删除 `artifacts/` 内的文件、启动/关闭便携版进程、移动窗口、注入测试输入和采集截图。运行 UI 冒烟前应确认不会干扰当前桌面会话。

## 依赖关系

### 上游依赖

- 开发人员、发布流程或手工验证调用这些脚本。
- 根目录 `AGENTS.md` 和 `docs/README.md` 引用标准发布与基座命令。

### 下游依赖

- .NET CLI、PowerShell、Windows UI Automation、Win32 API。
- [`VDict.sln`](../VDict.sln)、[`VDict.WinUI`](../src/VDict.WinUI/README.md)、[`VDict.RemediationFixture`](../tools/VDict.RemediationFixture/README.md) 和测试项目。
- UI 脚本依赖可交互 Windows 桌面与已安装的 WebView2 Runtime。

## 错误处理

脚本普遍启用 `$ErrorActionPreference = 'Stop'`，关键外部进程退出码非零时抛出异常。清理脚本先把目标规范化并限制在允许的制品目录；性能脚本对路径、超时、样本数和阈值做显式校验。

UI Automation 受窗口焦点、缩放、桌面锁定、WebView2 版本和其他前台应用影响。失败结果必须结合脚本输出、截图和应用日志判断，不能直接等同于业务单元测试失败。

## 测试

快速确定性基座回归：

```powershell
.\build\Run-RemediationPhase0A.ps1
```

本地化资源完整性：

```powershell
.\build\Test-LocalizationResources.ps1
```

完整发布验证：

```powershell
.\build\Publish.ps1 -Version 0.1.0
```

GitHub Release 由 `.github/workflows/release.yml` 自动发布。推送
`v<major>.<minor>.<patch>` 标签后，工作流会确认标签提交位于 `main`、标签版本与
`Directory.Build.props` 及 `CHANGELOG.md` 一致，再执行依赖审计、Release
构建与测试、本地化检查、确定性词典基座检查和便携版打包。工作流还会拒绝 ZIP
中的用户数据、真实词典、日志、测试结果、调试符号和本地 AI 工作目录，验证
SHA-256 后创建 GitHub Release。

仅用于无法联网的本地结构验证：

```powershell
.\build\Publish.ps1 -Version 0.1.0 -SkipAudit
```

其他脚本参数较多，应先阅读对应 `param(...)` 块并使用隔离的 `artifacts/` 目录。仓库没有统一的一键 UI 冒烟入口，适用场景和顺序待确认。

## 修改注意事项

- 保留路径边界检查，不得扩大递归删除范围。
- 新参数应具有类型、范围或集合校验，并更新本文件和调用脚本。
- UI Automation 名称与 XAML `AutomationProperties.Name` 相互依赖；修改界面文本或结构时同步更新冒烟脚本。
- 发布配置、Runtime Identifier、包签名或输出命名变化时同步更新根文档、`docs/PORTABLE.md` 和本文件。
- 不提交脚本生成的词典、设置、日志、截图、ZIP 或性能报告。

## 文档同步要求

脚本清单、参数、输出、清理范围、验证顺序或外部依赖发生变化时，更新本文件、[`AGENTS.md`](../AGENTS.md) 和 [`docs/README.md`](../docs/README.md)。

## 相关文档

- [仓库开发约束](../AGENTS.md)
- [文档中心](../docs/README.md)
- [测试总览](../tests/README.md)
- [Fixture 工具](../tools/VDict.RemediationFixture/README.md)
