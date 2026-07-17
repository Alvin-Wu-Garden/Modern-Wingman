pub mod commands;
pub mod db;
pub mod library;
pub mod mcp;
pub mod skills;

use tauri::Manager;
use skills::state::AppState;

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_shell::init())
        .plugin(tauri_plugin_dialog::init())
        .setup(|app| {
            let conn = db::open_db()?;
            app.manage(AppState::new(conn));
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            // ── Existing ──────────────────────────────────────────────────
            commands::greet,
            // ── Skills marketplace ────────────────────────────────────────
            skills::commands::list_skill_sources,
            skills::commands::add_skill_source,
            skills::commands::remove_skill_source,
            skills::commands::fetch_remote_skills,
            skills::commands::get_skill_readme,
            skills::commands::install_skill,
            skills::commands::uninstall_skill,
            skills::commands::list_installed_skills,
            skills::commands::list_agents,
            skills::commands::update_agent_path,
            // ── Central skill library (WS1) ───────────────────────────────
            library::commands::library_list_skills,
            library::commands::library_scan_risk,
            library::commands::library_preview_skill,
            library::commands::library_install_skill,
            library::commands::library_remove_skill,
            library::commands::library_set_tags,
            library::commands::library_read_skill_md,
            library::commands::library_sync_skill,
            library::commands::library_unsync_skill,
            library::commands::library_list_links,
            library::commands::library_detect_agents,
            library::commands::library_adopt_skill,
            library::commands::library_list_presets,
            library::commands::library_create_preset,
            library::commands::library_delete_preset,
            library::commands::library_set_preset_member,
            library::commands::library_apply_preset,
            // ── MCP registry (WS1) ────────────────────────────────────────
            mcp::commands::mcp_list_servers,
            mcp::commands::mcp_upsert_server,
            mcp::commands::mcp_delete_server,
            mcp::commands::mcp_set_agent_link,
        ])
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
