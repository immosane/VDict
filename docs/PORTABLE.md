# VDict 便携版

这是 x64 自包含便携版，不需要安装 .NET Desktop Runtime。系统仍需 Microsoft Edge
WebView2 Evergreen Runtime；未检测到时，应用会显示安装说明和重试入口。

请将压缩包完整解压到可写目录后运行 `VDict.exe`。设置、索引、缓存、日志和
WebView2 用户数据都保存在程序旁的 `Data` 目录中。若安装目录不可写，VDict 会明确
报错，不会静默改用 `%LocalAppData%`。

便携目录中的 `resources.pri` 包含中文和 English 界面资源，请勿单独删除或移动。
首次启动及旧设置默认跟随系统语言；系统语言无法匹配中文或 English 时使用
English。语言只可在 VDict 的“设置 → 阅读与外观”中更改，保存后重启生效。

升级时请保留 `Data` 目录。不要在应用运行期间移动或覆盖该目录。

VDict 的许可证见包内 `LICENSE.txt`，第三方组件与许可信息见
`THIRD-PARTY-NOTICES.md`。
