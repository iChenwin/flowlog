# FlowLog

A lightweight Windows native daily outliner inspired by Logseq's journal stream.

[Download 0.2.0 native preview](https://github.com/iChenwin/flowlog/releases/tag/v0.2.0) · [中文](README.md) · [Validation](docs/NATIVE-VALIDATION.md)

**C# / WPF / AvalonEdit**, without Electron, WebView, Chromium or a local web server. Requires system **.NET Framework 4.8**, excluded from package size. Installer under 1 MB; exact bytes and SHA-256 are release attachments.

Continuous journals, native cross-block text selection and undo, indentation, subtree movement/folding, same-level block dragging, tasks, page links/completion, backlinks, persistent UUID block references and search. Notes are local UTF-8 Markdown. Synchronous recovery drafts, delayed atomic saves, backups and conflict preservation protect edits.

A native preview, not full Logseq compatibility. Full rich Markdown rendering, queries, embeds, plugins, graph view, whiteboards, PDF, sync, database graphs and Org mode are excluded. Real IME candidate composition, clean-machine/offline setup, long sessions and large-vault performance still need manual validation. Open a copy of an existing vault first.

```powershell
./scripts/build-native.ps1 -Tests
./scripts/package-native.ps1 -SkipBuild
./scripts/smoke-native.ps1 -Installed
```

Build uses the Windows .NET Framework C# compiler and checksum-pinned dependencies. Native code is under `native/`. The old `src/` and `src-tauri/` WebView prototype is retained as legacy source and excluded from native builds.

MIT © 2026 iChenwin. Includes AvalonEdit under MIT. No Logseq AGPL source has been copied; workflows are reimplemented using native Windows controls.
