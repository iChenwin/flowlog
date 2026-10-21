# FlowLog

A small local outliner for everyday journals, inspired by Logseq's flowing journal view. Independent project; not affiliated with Logseq.

**0.1.0 is a preview. Back up your notes and try a copy of your vault first.** The Windows installer target is **under 20,000,000 bytes**, aiming for about 15 MB. Consult the release's `package-report.json` for measured bytes and SHA-256; a target is not a measured result.

[中文](README.md) · [AI role review](docs/EXPERT-REVIEW.md) · [Release checklist](docs/RELEASE-CHECKLIST.md) · [Contributing](CONTRIBUTING.md) · [Security](SECURITY.md)

## Implemented scope

- Today-first journal stream with earlier entries and date navigation.
- Outline editing, indentation, subtree movement, folding, undo and redo.
- TODO / DOING / DONE tasks and task overview.
- Page links, tags, suggestions, backlinks and text search.
- Basic bold, inline code and URL rendering; source editing for complex Markdown.
- Local UTF-8 Markdown on desktop, autosave, revision checks, pre-save snapshots and draft/error feedback.
- JSON backup export/import and light/dark themes.

Implementation does not imply all manual acceptance checks have passed. The browser demo stores data in the current browser and does not access a native vault.

## Install and start

Use the Windows NSIS `.exe` preview in this repository's Releases and verify its SHA-256 against `package-report.json`. If no installer is published, build from source below.

FlowLog uses Tauri 2, React, TypeScript and Rust, without Electron. It requires the system Microsoft Edge WebView2 runtime. The installer may include a bootstrapper, not the full runtime; a missing runtime may require an additional online download, excluded from the application installer size. Check release evidence for offline scenarios.

Write in today's journal, press Enter for another block and Tab to indent. Use Ctrl + Enter for tasks, `[[Page]]` for connections and Ctrl + K to search. The bottom-left vault control opens a different folder and shows the data location. No account is required.

The default vault is `FlowLog` in the system Documents folder. Notes use `journals/YYYY_MM_DD.md` and `pages/*.md`. Previous contents are kept in `.flowlog/backups`; conflicting drafts may be stored in `.flowlog/conflicts`. Backups do not currently rotate automatically. Manage storage and keep independent backups.

## Compatibility and limitations

Only supported `.md` files directly inside `journals` and `pages` are read. Journals require `YYYY_MM_DD.md` names. Basic outlines, tasks and page links are supported; edited outlines may normalize indentation and line endings. This is not byte-for-byte or full semantic Logseq compatibility.

Properties, block IDs and complex Markdown do not receive full Logseq semantics. Source editing preserves access to unsupported syntax. Block references, embeds, namespaces and queries are not fully supported. Graph visualization, whiteboards, PDF annotation, plugins, cloud sync, database graphs and Org mode are outside this release.

Copy an existing vault before trying it. External changes are checked when saving; export your draft and reopen the application after a conflict. There is no live merge of external changes. Avoid simultaneous writers and confirm saved status before exiting.

There is no automatic updater, signing guarantee or large-vault performance guarantee. Unsigned installers may trigger Windows reputation prompts. IME input, clean-machine installation, offline first launch and long editing sessions need separate manual verification; a passing build does not prove them.

## Build

Windows requires Node.js 22, Rust stable, Microsoft C++ Build Tools and WebView2.

```sh
npm ci
npm run tauri dev
```

```sh
npm test
npm run build
cargo test --manifest-path src-tauri/Cargo.toml
npx tauri build
```

Installers are emitted to `src-tauri/target/release/bundle/nsis/`. Run `pwsh -File scripts/check-bundle.ps1` for the size gate and checksum report. Windows CI includes installation, brief process startup and uninstallation smoke checks; these do not verify the editor or a clean virtual machine. `npm run dev` runs the browser demo only.

Keyboard shortcuts: Enter / Shift + Enter for blocks / line breaks; Tab / Shift + Tab for indentation; Alt + arrows for subtree movement; Ctrl + Enter for task state; Ctrl + K for search; Ctrl + Z / Ctrl + Shift + Z for undo / redo.

MIT © 2026 iChenwin. Please report reproducible issues with anonymous examples, never private notes.
