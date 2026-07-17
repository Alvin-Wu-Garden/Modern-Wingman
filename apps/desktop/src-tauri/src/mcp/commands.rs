//! Tauri command layer for the MCP registry. Thin — logic in repository/config_writer.

use std::path::Path;
use tauri::State;

use crate::skills::state::AppState;

use super::{
    config_writer,
    model::{McpServer, UpsertMcpServerParams},
    repository as repo,
};

#[tauri::command]
pub fn mcp_list_servers(state: State<'_, AppState>) -> Result<Vec<McpServer>, String> {
    let conn = state.db.lock().map_err(|e| e.to_string())?;
    repo::list_servers(&conn)
}

#[tauri::command]
pub fn mcp_upsert_server(
    params: UpsertMcpServerParams,
    state: State<'_, AppState>,
) -> Result<McpServer, String> {
    let conn = state.db.lock().map_err(|e| e.to_string())?;
    let id = repo::upsert_server(&conn, &params)?;
    repo::get_server(&conn, id)?.ok_or("儲存後讀取失敗".into())
}

#[tauri::command]
pub fn mcp_delete_server(server_id: i64, state: State<'_, AppState>) -> Result<(), String> {
    let conn = state.db.lock().map_err(|e| e.to_string())?;
    // Re-sync affected agents after deletion.
    let affected: Vec<String> = repo::get_server(&conn, server_id)?
        .map(|s| s.linked_agents)
        .unwrap_or_default();
    repo::delete_server(&conn, server_id)?;
    for agent_id in affected {
        let _ = sync_agent_config(&conn, &agent_id);
    }
    Ok(())
}

/// Links/unlinks a server to an agent and rewrites that agent's config file.
#[tauri::command]
pub fn mcp_set_agent_link(
    server_id: i64,
    agent_id: String,
    linked: bool,
    state: State<'_, AppState>,
) -> Result<(), String> {
    let conn = state.db.lock().map_err(|e| e.to_string())?;
    repo::set_link(&conn, server_id, &agent_id, linked)?;
    sync_agent_config(&conn, &agent_id)
}

/// Rewrites the MCP config file for one agent from the registry state.
fn sync_agent_config(conn: &rusqlite::Connection, agent_id: &str) -> Result<(), String> {
    let config_path: Option<String> = conn
        .query_row(
            "SELECT mcp_config_path FROM agents WHERE id = ?1",
            rusqlite::params![agent_id],
            |r| r.get(0),
        )
        .map_err(|e| e.to_string())?;

    let Some(config_path) = config_path else {
        return Err(format!("Agent '{agent_id}' 未定義 MCP 設定檔路徑"));
    };

    let servers = repo::servers_for_agent(conn, agent_id)?;
    // Managed names = every server name in the registry (whether currently
    // linked or not) so stale entries are cleaned up.
    let managed: Vec<String> = repo::list_servers(conn)?
        .into_iter()
        .map(|s| s.name)
        .collect();

    config_writer::write_agent_config(Path::new(&config_path), &servers, &managed)
}
