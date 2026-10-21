use serde::Serialize;
use percent_encoding::percent_decode_str;
use sha2::{Digest, Sha256};
use std::{fs, io::Write, path::{Path, PathBuf}};
use tempfile::NamedTempFile;

type Result<T> = std::result::Result<T, String>;

#[derive(Serialize)]
pub struct Note {
    path: String,
    title: String,
    kind: &'static str,
    content: String,
    revision: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    date: Option<String>,
}

#[derive(Serialize)]
pub struct Vault { root: String, notes: Vec<Note> }

#[derive(Debug, Serialize)]
pub struct SaveResult { pub revision: String }

fn err(error: impl std::fmt::Display) -> String { error.to_string() }

fn revision(content: &[u8]) -> String { format!("{:x}", Sha256::digest(content)) }

fn journal_date(stem: &str) -> bool {
    let bytes = stem.as_bytes();
    if bytes.len() != 10 || bytes[4] != b'_' || bytes[7] != b'_' ||
        bytes.iter().enumerate().any(|(i, b)| i != 4 && i != 7 && !b.is_ascii_digit()) { return false; }
    let year: u32 = stem[..4].parse().unwrap_or(0);
    let month: usize = stem[5..7].parse().unwrap_or(0);
    let day: u32 = stem[8..].parse().unwrap_or(0);
    let leap = year % 4 == 0 && (year % 100 != 0 || year % 400 == 0);
    let days = [31, if leap {29} else {28}, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];
    year > 0 && (1..=12).contains(&month) && day > 0 && day <= days[month - 1]
}

fn valid_relative(relative: &str) -> Result<(&str, &str)> {
    let parts: Vec<_> = relative.split('/').collect();
    if parts.len() != 2 || !matches!(parts[0], "journals" | "pages") { return Err("Invalid note path".into()); }
    let file = parts[1];
    let stem = file.strip_suffix(".md").ok_or("Only Markdown notes are allowed")?;
    if stem.is_empty() || stem == "." || stem == ".." || stem.ends_with('.') || stem.ends_with(' ') ||
        stem.chars().any(|c| c.is_control() || "\\/:*?\"<>|".contains(c)) {
        return Err("Invalid note filename".into());
    }
    let device = stem.split('.').next().unwrap_or("").to_ascii_uppercase();
    if matches!(device.as_str(), "CON" | "PRN" | "AUX" | "NUL") ||
        (device.len() == 4 && (device.starts_with("COM") || device.starts_with("LPT")) &&
        device.as_bytes()[3].is_ascii_digit()) { return Err("Reserved filename".into()); }
    if parts[0] == "journals" && !journal_date(stem) { return Err("Journal filename must be YYYY_MM_DD.md".into()); }
    Ok((parts[0], stem))
}

// Reject symlinks/junctions and verify every directory remains in the canonical vault.
fn safe_dir(root: &Path, relative: &str) -> Result<PathBuf> {
    let mut current = root.to_path_buf();
    for segment in relative.split('/') {
        current.push(segment);
        match fs::symlink_metadata(&current) {
            Ok(meta) => {
                if meta.file_type().is_symlink() || !meta.is_dir() { return Err("Unsafe vault directory".into()); }
                #[cfg(windows)]
                {
                    use std::os::windows::fs::MetadataExt;
                    if meta.file_attributes() & 0x400 != 0 { return Err("Reparse points are not allowed inside the vault".into()); }
                }
            },
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => fs::create_dir(&current).map_err(err)?,
            Err(e) => return Err(err(e)),
        }
        let canonical = fs::canonicalize(&current).map_err(err)?;
        if !canonical.starts_with(root) { return Err("Directory escapes the vault".into()); }
    }
    Ok(current)
}

fn safe_file(root: &Path, relative: &str) -> Result<PathBuf> {
    let (directory, _) = valid_relative(relative)?;
    let parent = safe_dir(root, directory)?;
    let target = parent.join(relative.split('/').nth(1).unwrap());
    match fs::symlink_metadata(&target) {
        Ok(meta) => {
            if meta.file_type().is_symlink() || !meta.is_file() { return Err("Unsafe note file".into()); }
            #[cfg(windows)]
            {
                use std::os::windows::fs::MetadataExt;
                if meta.file_attributes() & 0x400 != 0 { return Err("Reparse note files are not allowed".into()); }
            }
            if !fs::canonicalize(&target).map_err(err)?.starts_with(root) { return Err("Note escapes the vault".into()); }
        },
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => (),
        Err(e) => return Err(err(e)),
    }
    Ok(target)
}

pub fn prepare(path: &Path) -> Result<PathBuf> {
    fs::create_dir_all(path).map_err(err)?;
    let root = fs::canonicalize(path).map_err(err)?;
    safe_dir(&root, "journals")?;
    safe_dir(&root, "pages")?;
    Ok(root)
}

pub fn list(root: &Path) -> Result<Vault> {
    let mut notes = Vec::new();
    for directory in ["journals", "pages"] {
        for entry in fs::read_dir(safe_dir(root, directory)?).map_err(err)? {
            let entry = entry.map_err(err)?;
            let Some(filename) = entry.file_name().to_str().map(str::to_owned) else { continue };
            let relative = format!("{directory}/{filename}");
            if valid_relative(&relative).is_err() { continue; }
            let target = safe_file(root, &relative)?;
            let bytes = fs::read(target).map_err(err)?;
            let content = String::from_utf8(bytes.clone()).map_err(|_| format!("Note is not UTF-8: {relative}"))?;
            let stem = filename.trim_end_matches(".md");
            let title = if directory == "journals" { stem.replace('_', "-") } else { percent_decode_str(stem).decode_utf8_lossy().into_owned() };
            notes.push(Note { path: relative, title, kind: if directory == "journals" {"journal"} else {"page"},
                content, revision: revision(&bytes), date: if directory == "journals" {Some(stem.replace('_', "-"))} else {None} });
        }
    }
    notes.sort_by(|a, b| b.path.cmp(&a.path));
    Ok(Vault { root: root.to_string_lossy().into_owned(), notes })
}

fn backup_prefix(relative: &str) -> String {
    let stem = relative.rsplit('/').next().unwrap_or("note").trim_end_matches(".md");
    let readable: String = percent_decode_str(stem).decode_utf8_lossy().chars()
        .take(24).map(|c| if c.is_alphanumeric() || c == '-' || c == '_' { c } else { '_' }).collect();
    // The complete hash identifies the exact relative path, including directory and encoding.
    format!("{}-{}-", revision(relative.as_bytes()), readable)
}

fn recovery_copy(root: &Path, folder: &str, relative: &str, content: &[u8]) -> Result<PathBuf> {
    let directory = safe_dir(root, &format!(".flowlog/{folder}"))?;
    let timestamp = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map_err(err)?.as_nanos();
    let prefix = format!("{}{:039}-", backup_prefix(relative), timestamp);
    let mut copy = tempfile::Builder::new().prefix(&prefix).suffix(".md").tempfile_in(&directory).map_err(err)?;
    copy.write_all(content).map_err(err)?;
    copy.as_file().sync_all().map_err(err)?;
    let (_, path) = copy.keep().map_err(err)?;
    Ok(path)
}

fn prune_backups(root: &Path, relative: &str, newest: &Path) {
    // Best effort only: cleanup failure must never turn a completed save into an error.
    let Ok(directory) = safe_dir(root, ".flowlog/backups") else { return };
    let Ok(entries) = fs::read_dir(directory) else { return };
    let prefix = backup_prefix(relative);
    let mut copies: Vec<_> = entries.flatten().filter_map(|entry| {
        let name = entry.file_name();
        let name = name.to_str()?;
        if !name.starts_with(&prefix) || !name.ends_with(".md") { return None; }
        let metadata = fs::symlink_metadata(entry.path()).ok()?;
        if !metadata.is_file() || metadata.file_type().is_symlink() { return None; }
        Some((entry.path(), metadata.modified().ok()?))
    }).collect();
    copies.sort_by(|a, b| {
        // Protect the just-created backup even on filesystems with coarse timestamps.
        (b.0 == newest).cmp(&(a.0 == newest))
            .then_with(|| b.1.cmp(&a.1)).then_with(|| b.0.cmp(&a.0))
    });
    for (path, _) in copies.into_iter().skip(20) { let _ = fs::remove_file(path); }
}

pub fn save(root: &Path, relative: &str, content: &str, expected: &str) -> Result<SaveResult> {
    let target = safe_file(root, relative)?;
    let old = match fs::read(&target) {
        Ok(bytes) => Some(bytes),
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => None,
        Err(e) => return Err(err(e)),
    };
    let current = old.as_ref().map(|bytes| revision(bytes)).unwrap_or_default();
    if current != expected {
        let copy = recovery_copy(root, "conflicts", relative, content.as_bytes())?;
        return Err(format!("CONFLICT: Note changed outside FlowLog. Your draft was preserved at {}. Reload the vault before editing again.", copy.display()));
    }
    let backup = if let Some(bytes) = &old { Some(recovery_copy(root, "backups", relative, bytes)?) } else { None };
    let mut pending = NamedTempFile::new_in(target.parent().unwrap()).map_err(err)?;
    pending.write_all(content.as_bytes()).map_err(err)?;
    pending.flush().map_err(err)?;
    pending.as_file().sync_all().map_err(err)?;
    // A second check catches external edits made while the backup/temp file was prepared.
    let latest = match fs::read(&target) {
        Ok(bytes) => revision(&bytes),
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => String::new(),
        Err(e) => return Err(err(e)),
    };
    if latest != expected {
        let copy = recovery_copy(root, "conflicts", relative, content.as_bytes())?;
        return Err(format!("CONFLICT: Note changed during save. Draft preserved at {}", copy.display()));
    }
    safe_file(root, relative)?;
    pending.persist(&target).map_err(err)?;
    if let Some(path) = backup { prune_backups(root, relative, &path); }
    Ok(SaveResult { revision: revision(content.as_bytes()) })
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn paths_cannot_escape_or_use_invalid_dates() {
        for path in ["../secret.md", "pages/../secret.md", "pages/a\\b.md", "pages/C:/x.md", "pages/CON.md", "journals/2025_02_29.md", "journals/2026_13_01.md"] {
            assert!(valid_relative(path).is_err(), "{path}");
        }
        assert!(valid_relative("journals/2024_02_29.md").is_ok());
        assert!(valid_relative("pages/中文笔记.md").is_ok());
    }
    #[test]
    fn chinese_round_trip_backup_and_conflict_preserve_both_versions() {
        let temp = tempfile::tempdir().unwrap();
        let root = prepare(temp.path()).unwrap();
        let original = "- 你好，世界\r\n  - [[项目]] #标签\r\n";
        let first = save(&root, "pages/中文.md", original, "").unwrap();
        assert_eq!(list(&root).unwrap().notes[0].content, original);
        let second = save(&root, "pages/中文.md", "- 第二版\n", &first.revision).unwrap();
        let backups: Vec<_> = fs::read_dir(root.join(".flowlog/backups")).unwrap().collect();
        assert_eq!(backups.len(), 1);
        assert_eq!(fs::read_to_string(backups[0].as_ref().unwrap().path()).unwrap(), original);
        fs::write(root.join("pages/中文.md"), "external").unwrap();
        assert!(save(&root, "pages/中文.md", "my draft", &second.revision).unwrap_err().starts_with("CONFLICT:"));
        assert_eq!(fs::read_to_string(root.join("pages/中文.md")).unwrap(), "external");
        let conflict = fs::read_dir(root.join(".flowlog/conflicts")).unwrap().next().unwrap().unwrap();
        assert_eq!(fs::read_to_string(conflict.path()).unwrap(), "my draft");
    }
    #[test]
    fn empty_file_is_distinct_from_a_missing_note() {
        let temp = tempfile::tempdir().unwrap();
        let root = prepare(temp.path()).unwrap();
        let first = save(&root, "pages/empty.md", "", "").unwrap();
        assert!(!first.revision.is_empty());
        assert!(save(&root, "pages/empty.md", "accidental overwrite", "").is_err());
        save(&root, "pages/empty.md", "intentional update", &first.revision).unwrap();
    }
    #[test]
    fn encoded_page_titles_survive_reloading() {
        let temp = tempfile::tempdir().unwrap();
        let root = prepare(temp.path()).unwrap();
        save(&root, "pages/%E4%B8%AD%E6%96%87%20%E8%AE%A1%E5%88%92.md", "- [[中文 计划]]", "").unwrap();
        save(&root, "journals/2026_10_21.md", "- today", "").unwrap();
        let reloaded = list(&root).unwrap();
        let page = reloaded.notes.iter().find(|note| note.kind == "page").unwrap();
        assert_eq!(page.title, "中文 计划");
        assert_eq!(page.path, "pages/%E4%B8%AD%E6%96%87%20%E8%AE%A1%E5%88%92.md");
        assert_eq!(reloaded.notes.iter().find(|note| note.kind == "journal").unwrap().title, "2026-10-21");
    }
    #[test]
    fn backup_retention_is_bounded_separately_for_each_note() {
        let temp = tempfile::tempdir().unwrap();
        let root = prepare(temp.path()).unwrap();
        let mut first = save(&root, "pages/a.md", "0", "").unwrap().revision;
        let second = save(&root, "pages/b.md", "b original", "").unwrap().revision;
        save(&root, "pages/b.md", "b updated", &second).unwrap();
        for version in 1..=25 {
            first = save(&root, "pages/a.md", &version.to_string(), &first).unwrap().revision;
        }
        let paths: Vec<_> = fs::read_dir(root.join(".flowlog/backups")).unwrap().map(|e| e.unwrap().path()).collect();
        let a_prefix = backup_prefix("pages/a.md");
        let b_prefix = backup_prefix("pages/b.md");
        let a: Vec<_> = paths.iter().filter(|path| path.file_name().unwrap().to_string_lossy().starts_with(&a_prefix)).collect();
        let b: Vec<_> = paths.iter().filter(|path| path.file_name().unwrap().to_string_lossy().starts_with(&b_prefix)).collect();
        assert_eq!(a.len(), 20);
        assert_eq!(b.len(), 1);
        assert!(a.iter().any(|path| fs::read_to_string(path).unwrap() == "24"));
        assert_eq!(fs::read_to_string(b[0]).unwrap(), "b original");
        assert_ne!(backup_prefix("pages/same.md"), backup_prefix("journals/same.md"));
    }
    #[cfg(unix)]
    #[test]
    fn symlink_cannot_escape_vault() {
        let temp = tempfile::tempdir().unwrap();
        let root = prepare(temp.path()).unwrap();
        let outside = tempfile::tempdir().unwrap();
        fs::write(outside.path().join("secret.md"), "secret").unwrap();
        std::os::unix::fs::symlink(outside.path().join("secret.md"), root.join("pages/link.md")).unwrap();
        assert!(save(&root, "pages/link.md", "overwrite", "").is_err());
        assert_eq!(fs::read_to_string(outside.path().join("secret.md")).unwrap(), "secret");
    }
}
