# 参与贡献

欢迎提交可复现问题、文档改进和小范围修复。提交问题前请搜索已有 issue。

开发需要 Node.js 22、Rust stable，以及 Windows Tauri 所需的 Microsoft C++ Build Tools 和 WebView2。执行 `npm ci`、`npm run tauri dev` 启动桌面开发；`npm run dev` 仅提供浏览器演示，不能验证原生文件保存。

提交 PR 前运行：

```sh
npm test
npm run build
cargo test --manifest-path src-tauri/Cargo.toml
```

涉及桌面行为时还应运行 `npx tauri build`，并说明验证系统、复现步骤和仍未测试的部分。请使用匿名样例，不上传个人笔记、真实路径或日志中的敏感内容。提交代码即表示同意以 MIT 许可证分发你的贡献。

优先方向：保存可靠性、中文输入、键盘操作、Markdown 保真、可达性和包体积。增加依赖或大型组件前请先开 issue 讨论成本。
