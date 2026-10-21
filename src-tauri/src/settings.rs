use std::{fs, io::Write, path::{Path, PathBuf}};

type Result<T> = std::result::Result<T, String>;

pub fn config_directory() -> Result<PathBuf> {
    Ok(dirs::config_dir().ok_or("Cannot locate your application configuration folder")?.join("FlowLog"))
}

pub fn remembered_vault(config: &Path) -> Result<Option<PathBuf>> {
    let text = match fs::read_to_string(config.join("last-vault.txt")) {
        Ok(text) => text,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(None),
        Err(error) => return Err(format!("Cannot read the remembered vault: {error}. Choose a vault folder to recover.")),
    };
    let path = PathBuf::from(text);
    if !path.is_absolute() || !path.is_dir() {
        return Err(format!("The remembered vault is unavailable: {}. Choose a vault folder to continue; your saved selection has not been replaced.", path.display()));
    }
    // Resolve an existing directory before preparation, so startup never creates a missing remembered root.
    fs::canonicalize(path).map(Some).map_err(|error| format!("Cannot open the remembered vault: {error}. Choose a vault folder to continue."))
}

pub fn remember_vault(config: &Path, root: &Path) -> Result<()> {
    let text = root.to_str().ok_or("The vault path cannot be stored as UTF-8")?;
    fs::create_dir_all(config).map_err(|e| format!("Cannot create application configuration: {e}"))?;
    let mut pending = tempfile::NamedTempFile::new_in(config).map_err(|e| e.to_string())?;
    pending.write_all(text.as_bytes()).map_err(|e| e.to_string())?;
    pending.flush().map_err(|e| e.to_string())?;
    pending.as_file().sync_all().map_err(|e| e.to_string())?;
    pending.persist(config.join("last-vault.txt")).map_err(|e| format!("Cannot remember this vault: {e}"))?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn first_start_has_no_selection_and_changed_selection_survives_reload() {
        let temp = tempfile::tempdir().unwrap();
        let config = temp.path().join("config");
        assert_eq!(remembered_vault(&config).unwrap(), None);
        let first = temp.path().join("中文 库");
        let second = temp.path().join("another vault");
        fs::create_dir(&first).unwrap();
        fs::create_dir(&second).unwrap();
        let first = fs::canonicalize(first).unwrap();
        let second = fs::canonicalize(second).unwrap();
        remember_vault(&config, &first).unwrap();
        assert_eq!(remembered_vault(&config).unwrap(), Some(first));
        remember_vault(&config, &second).unwrap();
        assert_eq!(remembered_vault(&config).unwrap(), Some(second));
        assert_eq!(fs::read_dir(&config).unwrap().count(), 1);
    }

    #[test]
    fn deleted_or_invalid_selection_is_reported_without_recreating_it() {
        let temp = tempfile::tempdir().unwrap();
        let config = temp.path().join("config");
        let root = temp.path().join("removed");
        fs::create_dir(&root).unwrap();
        remember_vault(&config, &fs::canonicalize(&root).unwrap()).unwrap();
        fs::remove_dir(&root).unwrap();
        assert!(remembered_vault(&config).unwrap_err().contains("unavailable"));
        assert!(!root.exists());
        fs::write(config.join("last-vault.txt"), "relative/path").unwrap();
        assert!(remembered_vault(&config).is_err());
    }
}
