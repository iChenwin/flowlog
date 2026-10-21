# 参与贡献

欢迎提交可复现问题、文档改进和小范围修复。提交问题前请搜索已有 issue。

当前开发目标为 `native/` 下的 Windows WPF 原生版。需要 Windows 与 .NET Framework 4.8，使用系统 C#5 编译器；脚本校验 AvalonEdit。原 `src/`、`src-tauri/` 是历史 WebView 原型。

提交 PR 前运行：

```powershell
./scripts/build-native.ps1 -Tests
./scripts/package-native.ps1 -SkipBuild
./scripts/smoke-native.ps1 -Installed
```

安装冒烟使用没有本应用安装记录的测试账户。说明验证系统、复现步骤和仍未测试的部分。请使用匿名样例，不上传个人笔记、真实路径或敏感信息。提交代码即表示同意以 MIT 许可证分发贡献。

优先方向：保存可靠性、中文输入、键盘操作、Markdown 保真、可达性和包体积。增加依赖或大型组件前请先开 issue 讨论成本。
