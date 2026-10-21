#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod vault;
mod settings;

use std::{path::PathBuf, sync::Mutex};
use tauri::State;
use vault::{SaveResult, Vault};

#[derive(Default)]
struct AppState(Mutex<Option<PathBuf>>);

#[tauri::command]
fn open_default_vault(state: State<AppState>) -> Result<Vault, String> {
    let config = settings::config_directory()?;
    let root = match settings::remembered_vault(&config)? {
        Some(root) => root,
        None => dirs::document_dir().ok_or("Cannot locate your Documents folder")?.join("FlowLog"),
    };
    let mut active = state.0.lock().map_err(|_| "Vault lock unavailable")?;
    let root = vault::prepare(&root)?;
    let result = vault::list(&root)?;
    settings::remember_vault(&settings::config_directory()?, &root)?;
    *active = Some(root);
    Ok(result)
}

#[tauri::command]
fn choose_vault(state: State<AppState>) -> Result<Option<Vault>, String> {
    let Some(root) = rfd::FileDialog::new().set_title("Choose your FlowLog folder").pick_folder() else {
        return Ok(None);
    };
    let mut active = state.0.lock().map_err(|_| "Vault lock unavailable")?;
    let root = vault::prepare(&root)?;
    let result = vault::list(&root)?;
    settings::remember_vault(&settings::config_directory()?, &root)?;
    *active = Some(root);
    Ok(Some(result))
}

#[tauri::command]
fn list_vault(state: State<AppState>) -> Result<Vault, String> {
    let active = state.0.lock().map_err(|_| "Vault lock unavailable")?;
    vault::list(active.as_ref().ok_or("Open a vault first")?)
}

#[tauri::command]
fn save_note(path: String, content: String, expected_revision: String, state: State<AppState>) -> Result<SaveResult, String> {
    // Hold the same lock over checking, backup and replacement, including concurrent windows.
    let active = state.0.lock().map_err(|_| "Vault lock unavailable")?;
    vault::save(active.as_ref().ok_or("Open a vault first")?, &path, &content, &expected_revision)
}

fn main() {
    tauri::Builder::default()
        .manage(AppState::default())
        .invoke_handler(tauri::generate_handler![open_default_vault, choose_vault, list_vault, save_note])
        .run(tauri::generate_context!())
        .expect("Unable to start FlowLog");
}
