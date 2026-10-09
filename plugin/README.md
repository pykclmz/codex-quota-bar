# Codex 额度条插件

此插件连接 Windows 上已经运行的额度条。安装后，在 Codex 的插件详情页中调整外观、提醒和显示状态；无需复制登录信息。

从项目根目录运行 `windows/install-plugin.ps1` 完成构建和本地插件安装。需先安装额度条，再安装插件。插件本身不会启动第二个额度轮询进程。

插件采用官方 [Structured Settings 扩展](https://github.com/openai/mcp-extensions/blob/main/docs/spec.md#structured-settings)。它位于插件详情页，不会给 Codex 的“通用”或“外观”页注入第三方控件。通过支持的本地插件深链打开详情页，未安装时继续使用独立设置窗口。

源码保留可移植清单和 Codex 兼容清单；当前桌面安装脚本部署兼容布局，并验证设置服务器已被发现。
