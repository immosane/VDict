# VDict.Infrastructure

## 模块职责

`VDict.Infrastructure` 实现 Core 定义的持久化、词典和环境边界。它负责 JSON 设置读写与迁移、MDX/MDD 解码、SQLite 候选索引、运行时 Catalog、词典 Store 租约、资源缓存、应用数据路径、日志和缓存维护。

该模块不包含窗口、XAML 或 WebView2 控件，也不决定具体用户交互。

## 目录结构

- `Catalog/`：Catalog 快照、词典 Store、内存缓存和侧车文件发现。
- `DictionaryFormats/`：MDict header/key/record 解析、MDD 路由和资源索引。
- `Environment/`：便携/打包数据路径、日志、进程环境和系统字体。
- `Indexing/`：从 MDX 构建和提升 SQLite 候选索引。
- `Maintenance/`：缓存统计、受限清理和索引重建协调。
- `Settings/`：设置 JSON 文档、schema 迁移和原子存储。
- `Properties/AssemblyInfo.cs`：向测试程序集开放内部成员。
- `VDict.Infrastructure.csproj`：引用 Core、Microsoft.Data.Sqlite 和 DI abstractions。

## 核心组件

- `DictionaryCatalog`：协调设置、Store、索引和 generation，向 Presentation 提供一致快照。
- `DictionaryStore`：将一个词典定义、SQLite 索引、MDX reader、MDD reader 和资源缓存绑定为可租用单元。
- `MDictReader`：解析 MDict 1.x/2.x/3.x key/record block 并按偏移读取正文或资源。
- `DictionaryIndexBuilder`：在临时目录构建 SQLite `entries`/`meta`，验证后原子提升。
- `AtomicSettingsStore`、`SettingsSchema`、`StartupLanguageReader`：带备份、schema 8 迁移、修订号与跨进程互斥的 JSON 设置，以及加载 XAML 前的轻量语言读取。
- `CacheMaintenanceService`：检查内存/磁盘缓存，只在批准路径内清理，并协调 Store 释放/恢复。

## 工作流程

启动时，`DictionaryCatalog.InitializeAsync` 加载设置，建立初始状态；对启用词典验证文件、准备缺失/过期索引、试开 Store，随后发布 generation。查询捕获目标 Store 的租约并并行执行，Store 使用只读 SQLite 定位记录偏移，再由 `MDictReader` 读取正文。

设置提交使用 prepare/commit 两阶段：prepare 规范化并验证设置、检查重复词典、暂存索引并试开 Store；commit 校验 base revision、原子保存设置、提升索引并切换不可变 Catalog 状态。旧 Store 在所有租约释放后退役。

Infrastructure 通过 Core 的 `IAppLocalizer` 生成可能展示给用户的设置、索引、Catalog 和维护消息；内部异常、日志分类、路径和协议值保持稳定且不作为翻译资源。

资源解析优先检查解码缓存和安全的同目录文件，再按 MDD profile 路由到配套资源包；缺失结果可进入负缓存。CSS 资源会重写嵌套 URL，所有本地路径均受规范化和 reparse point 检查。

## 输入与输出

- 输入：`ApplicationSettings`、本地 `.mdx`/`.mdd`/`.css`/`.js` 文件、查询词、资源路径和维护请求。
- 输出：Catalog 快照、候选词、词条正文、资源字节与 MIME、SQLite 索引、JSON 设置、日志和维护报告。
- 副作用：创建数据目录；读写设置与备份；创建/替换 SQLite 索引；维护持久资源索引和日志；按明确请求删除批准缓存目录内容。

## 依赖关系

### 上游依赖

- [`VDict.WinUI`](../VDict.WinUI/README.md) 在组合根注册并调用具体服务。
- [`VDict.Presentation`](../VDict.Presentation/README.md) 通过 `IDictionaryCatalog` 间接调用 Catalog。
- Fixture 工具和 Infrastructure 测试直接使用格式、索引与 Catalog 实现。

### 下游依赖

- [`VDict.Core`](../VDict.Core/README.md)。
- `Microsoft.Data.Sqlite` 和 `Microsoft.Extensions.DependencyInjection.Abstractions`。
- Windows 文件系统、命名互斥体、已安装字体和应用数据位置。

## 配置

数据布局由 `IAppDataPaths` 注入：

- 便携模式：可执行文件旁 `Data/Settings`、`Data/Cache`、`Data/WebView2`、`Data/Logs`、`Data/Temp`。
- MSIX 模式：设置在 LocalState，索引/资源/WebView2/日志/临时数据在 LocalCache。

设置 schema 当前为 v7。真实设置文件不属于仓库配置，不能写入文档或提交版本库。

## 错误处理

设置主文件损坏时尝试备份并保留损坏副本；主/备份均失败时回退默认值并返回恢复消息。prepare 阶段错误不会替换活动快照或设置。Catalog 捕获单词典查询异常并转为 notice，取消仍向上传播。维护操作限制清理路径，拒绝 reparse point，并在释放 Store 后尽力恢复有效 Catalog。

## 测试

[`VDict.Infrastructure.Tests`](../../tests/VDict.Infrastructure.Tests/README.md) 覆盖 MDict 块边界、MDD 路由、索引、Store 租约、设置并发/恢复、缓存清理和 fixture。

```powershell
dotnet test tests\VDict.Infrastructure.Tests\VDict.Infrastructure.Tests.csproj --configuration Release --no-restore
```

## 修改注意事项

- 不得破坏 prepare/commit 原子性、修订号比较或旧 Store 租约等待。
- 格式读取必须保持块大小、数量、偏移和物化资源上限。
- 新设置字段必须同时更新 schema 文档、迁移、Core 模型/比较/验证和测试。
- 任何删除逻辑必须保持批准目录与 reparse point 防护。
- SQLite 索引是可重建派生数据，不得将真实用户索引提交仓库。

## 文档同步要求

持久化、格式支持、数据布局、索引格式、Catalog 事务或维护范围变化时，更新本文件、[`src/README.md`](../README.md) 和 [`docs/ARCHITECTURE.md`](../../docs/ARCHITECTURE.md)。

## 相关文档

- [仓库开发约束](../../AGENTS.md)
- [生产源代码](../README.md)
- [Infrastructure 测试](../../tests/VDict.Infrastructure.Tests/README.md)
