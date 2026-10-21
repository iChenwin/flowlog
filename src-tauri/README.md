# Desktop backend

The NSIS installer uses Tauri 2 and the system Microsoft Edge WebView2 runtime.
`embedBootstrapper` includes the small runtime bootstrapper; a machine without
WebView2 needs an internet connection during first installation. The 10–19 MB
package target excludes the separately installed shared WebView2 runtime and
must be measured from a real Windows release build. This repository does not
claim a measured package size before CI produces one.

Commands are restricted to the currently selected vault. Notes are UTF-8 files
in `journals/YYYY_MM_DD.md` or `pages/name.md`. Each successful replacement of an
existing note preserves its prior bytes in `.flowlog/backups`. Revision conflicts
preserve the attempted text in `.flowlog/conflicts` and return a `CONFLICT:` error
containing the recovery path. Each note retains its latest 20 backup copies.
Cleanup runs after a successful save and is best effort: a deletion failure can
leave extra copies but never prevents saving or discards the current original.
Conflict copies are never automatically pruned.

Recovery filenames begin with the SHA-256 of the exact vault-relative path
(for example `pages/Plan.md`), followed by up to 24 decoded readable stem
characters, a zero-padded Unix nanosecond timestamp, and a random suffix. This
keeps notes with similar titles separate, and the readable stem helps locate
copies. To verify a match, compute the SHA-256 of the UTF-8 relative path, with
forward slashes and no trailing newline; it matches the first 64 filename
characters. Existing recovery copies from older versions remain untouched.
Open a recovery `.md` in a text editor to inspect it, then copy its contents into
the corresponding note after preserving any newer text. Recovery copies contain
private local note data. Page titles decode percent-encoded filenames; journal
titles display ISO dates while original file paths stay unchanged.

The global application mutex serializes internal operations. Revision hashes are
checked twice before replacement. This detects normal external editor changes;
it is not an operating-system lock against arbitrary external applications. An
external process can still write in the narrow interval between the final check
and the atomic replacement. Symlinks, Windows junctions/reparse points and
traversal paths inside the vault are rejected, but a concurrently malicious
filesystem process is outside this desktop application's threat model.

Validation on a Rust/MSVC-equipped Windows host:

```text
cargo test --manifest-path src-tauri/Cargo.toml
npm run tauri build
```

The source includes tests for traversal and reserved paths, leap dates, exact
Chinese/CRLF round trips, revisions, backup bytes, conflict recovery, missing vs
empty files, encoded Chinese page-title reloads, per-note backup retention and
separation, and symlink escape rejection on Unix.
