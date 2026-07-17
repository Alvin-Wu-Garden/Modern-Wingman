/// Temporary greeting command — will be replaced by agent commands in Phase 2
#[tauri::command]
pub async fn greet(name: String) -> String {
    format!("Hello, {}! Modern Wingman is running.", name)
}
