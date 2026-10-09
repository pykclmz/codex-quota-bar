在 Codex 输入框附近显示剩余额度、重置倒计时和重置日期。独立运行，不修改 Codex。

Windows 增强版 1.2.2 基于 [Thinkdiff-lw/codex-quota-bar](https://github.com/Thinkdiff-lw/codex-quota-bar)，保留 MIT 许可证。

- 输入框底部定位与窗口移动即时跟随；中文输入法候选框位于额度条上方。
- 独立右键设置窗口：额度概览、外观显示、额度提醒和运行设置。
- 修复长说明、重置详情、双额度预览和错误提示的截断；支持小窗口滚动与高 DPI。
- 刷新、启动状态和自动启动设置异步处理，主题与开关同步实际状态。
- 定位和数据读取失败自动恢复；偏好原子写入与备份恢复，更新支持回退。

macOS 代码保留上游测试版，未加入上述 Windows 改进，也未进行本次真机验证。

- **Windows**：下载 `CodexQuotaBar-Windows.zip`，解压后双击“启动额度条”。首次开启“自动跟随”后，系统登录时后台等待 Codex。
- **macOS 测试版**：下载 `CodexQuotaBar-macOS.zip`，将 `.app` 放入“应用程序”。需要 macOS 13+ 和辅助功能权限；支持菜单栏操作、手动校准以及登录启动。
- 使用本机 Codex CLI 的额度接口，CLI 需登录自己的 ChatGPT 账户。
- 源码、详细安装说明和构建脚本均在仓库中；提供 SHA256 校验文件。

Mac 使用 Apple Silicon / Intel 通用二进制与 ad-hoc 本地签名，尚未经 Apple Developer ID 签名 / 公证。首次打开可能需要在系统设置中允许。自动定位依赖 Codex 的辅助功能控件，真机定位、多屏与登录启动仍需验证；不保证所有 Codex 版本结构兼容。

这是个人开发的辅助工具，与 OpenAI 无隶属关系。示例数值不是额外赠送额度。
