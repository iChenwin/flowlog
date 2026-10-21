# 0.1.0 验证记录

日期：2026-10-21。此记录区分已执行测试与待验证场景，不把实现清单视为验收证据。

## 本机已执行

- `npm test`：23 项通过，包含 15 项 Markdown / 块树 / 路径测试，8 项保存队列与崩溃恢复测试。
- `npm run test:e2e`：11 项通过，使用本机无头 Chrome。覆盖中文编辑后重载、输入法组合事件保护、双链与反链、日记流分页、原文编辑、安全文本渲染、撤销重做焦点、多行粘贴子树、原文文档任务保护、新建未落盘草稿恢复、冲突恢复入口。
- `npm run build`：类型检查与生产资源构建通过。
- 上述界面测试是浏览器演示，保存单元测试使用受控桌面桥接模拟；它们不等同于真实 WebView2 中文输入法人工验收。

## 大库浏览器测量

环境：Windows、Intel Core i5-10500、32 GiB 内存、Chrome 155.0.8059.82、Vite 开发服务、无头浏览器。

固定样本为 365 天、10,000 块。首次显示约 590ms，同时挂载 7 天 / 196 块；15 次自动化按键操作的墙钟耗时中位数约 39ms、最大约 114ms。断开浏览器网络后记录并写入演示存储成功。

这是一次开发环境诊断，包含浏览器自动化开销，不代表原生安装包冷启动、输入延迟或跨机器承诺。复测：先 `npm run dev`，再 `node scripts/benchmark-browser.mjs`，结果写入 `artifacts/performance-browser.json`。

## Windows 发布门禁

GitHub Actions 执行 Rust 测试、Tauri / NSIS 构建、安装包小于 20,000,000 字节检查、SHA-256、静默安装、短暂启动与卸载。最终安装包大小与对应运行链接以发布附件及版本说明为准。

### 新增真实桌面桥接门禁（等待 CI 执行）

`scripts/smoke-install.ps1` 仅允许在 GitHub Actions 运行。它创建独立临时笔记库与 WebView2 用户数据目录，暂存并恢复 CI 账户的笔记库选择，通过安装后的真实应用完成两次启动：

1. 使用 Playwright 连接真实 WebView2 CDP，确认桌面保存标识与所选目录，编辑中文并直接读取 Markdown 文件核对落盘。
2. 正常关闭并重开应用，确认内容重载；模拟外部文件修改后继续编辑，断言磁盘保留外部版本且 `.flowlog/conflicts` 存在中文草稿，再显式放弃待保存更改、读取磁盘版本并正常关闭。

脚本不模拟 Tauri 桥接。调试端口只经 CI 子进程环境开启，测试后恢复，不改变产品配置。结果写入 `artifacts/native-smoke.json`，连接页面后的失败截图写入 `artifacts/native-smoke.png`。本机仅检查脚本语法；新增门禁是否通过以实际 CI 结果为准。此验证仍不等同于真实中文输入法候选操作、干净虚拟机或故障断电测试。

方法依据：[Playwright WebView2](https://playwright.dev/docs/next/webview2)、[Microsoft WebView2 调试文档](https://learn.microsoft.com/en-us/microsoft-edge/webview2/how-to/debug-visual-studio-code)。

## 仍需人工验证

- 微软拼音 / 第三方输入法真实候选窗口、撤销及连续长时间输入。
- 不含开发工具的干净 Windows 系统，以及 WebView2 缺失时的额外下载安装。
- 原生桌面断网使用、真实磁盘满 / 断电 / 强杀中断写入。
- 不同 Logseq 笔记库的复杂语法迁移；请使用副本，不承诺完整兼容。

因此 0.1.0 作为预览版发布。
