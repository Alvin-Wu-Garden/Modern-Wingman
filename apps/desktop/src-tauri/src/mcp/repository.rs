//! SQLite persistence for the MCP server registry.

use rusqlite::{params, Connection, OptionalExtension};
use std::collections::HashMap;

use super::model::{McpServer, UpsertMcpServerParams};

fn now_secs() -> i64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs() as i64
}

fn row_to_server(row: &rusqlite::Row<'_>) -> rusqlite::Result<McpServer> {
    let args_json: String = row.get(4)?;
    let env_json: String = row.get(6)?;
    Ok(McpServer {
        id: row.get(0)?,
        name: row.get(1)?,
        transport: row.get(2)?,
        command: row.get(3)?,
        args: serde_json::from_str(&args_json).unwrap_or_default(),
        url: row.get(5)?,
        env: serde_json::from_str(&env_json).unwrap_or_default(),
        enabled: row.get::<_, i64>(7)? == 1,
        created_at: row.get(8)?,
        updated_at: row.get(9)?,
        linked_agents: Vec::new(), // filled by caller
    })
}

const COLS: &str = "id, name, transport, command, args, url, env, enabled, created_at, updated_at";

pub fn list_servers(conn: &Connection) -> Result<Vec<McpServer>, String> {
    let mut stmt = conn
        .prepare(&format!("SELECT {COLS} FROM mcp_servers ORDER BY name"))
        .map_err(|e| e.to_string())?;
    let mut servers: Vec<McpServer> = stmt
        .query_map(params![], row_to_server)
        .map_err(|e| e.to_string())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|e| e.to_string())?;

    // Attach linked agents
    let mut link_stmt = conn
        .prepare("SELECT server_id, agent_id FROM mcp_agent_links")
        .map_err(|e| e.to_string())?;
    let links: Vec<(i64, String)> = link_stmt
        .query_map(params![], |r| Ok((r.get(0)?, r.get(1)?)))
        .map_err(|e| e.to_string())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|e| e.to_string())?;

    let mut by_server: HashMap<i64, Vec<String>> = HashMap::new();
    for (sid, aid) in links {
        by_server.entry(sid).or_default().push(aid);
    }
    for s in &mut servers {
        if let Some(agents) = by_server.remove(&s.id) {
            s.linked_agents = agents;
        }
    }
    Ok(servers)
}

pub fn get_server(conn: &Connection, id: i64) -> Result<Option<McpServer>, String> {
    let server = conn
        .query_row(
            &format!("SELECT {COLS} FROM mcp_servers WHERE id = ?1"),
            params![id],
            row_to_server,
        )
        .optional()
        .map_err(|e| e.to_string())?;

    let Some(mut server) = server else { return Ok(None) };
    let mut stmt = conn
        .prepare("SELECT agent_id FROM mcp_agent_links WHERE server_id = ?1")
        .map_err(|e| e.to_string())?;
    server.linked_agents = stmt
        .query_map(params![id], |r| r.get(0))
        .map_err(|e| e.to_string())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|e| e.to_string())?;
    Ok(Some(server))
}

pub fn upsert_server(conn: &Connection, p: &UpsertMcpServerParams) -> Result<i64, String> {
    let now = now_secs();
    let args_json = serde_json::to_string(&p.args).map_err(|e| e.to_string())?;
    let env_json = serde_json::to_string(&p.env).map_err(|e| e.to_string())?;

    match p.id {
        Some(id) => {
            conn.execute(
                "UPDATE mcp_servers SET
                   name = ?1, transport = ?2, command = ?3, args = ?4,
                   url = ?5, env = ?6, enabled = ?7, updated_at = ?8
                 WHERE id = ?9",
                params![
                    p.name, p.transport, p.command, args_json,
                    p.url, env_json, if p.enabled { 1 } else { 0 }, now, id
                ],
            )
            .map_err(|e| e.to_string())?;
            Ok(id)
        }
        None => {
            conn.execute(
                "INSERT INTO mcp_servers
                 (name, transport, command, args, url, env, enabled, created_at, updated_at)
                 VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?8)",
                params![
                    p.name, p.transport, p.command, args_json,
                    p.url, env_json, if p.enabled { 1 } else { 0 }, now
                ],
            )
            .map_err(|e| e.to_string())?;
            Ok(conn.last_insert_rowid())
        }
    }
}

pub fn delete_server(conn: &Connection, id: i64) -> Result<(), String> {
    conn.execute("DELETE FROM mcp_servers WHERE id = ?1", params![id])
        .map_err(|e| e.to_string())?;
    Ok(())
}

pub fn set_link(
    conn: &Connection,
    server_id: i64,
    agent_id: &str,
    linked: bool,
) -> Result<(), String> {
    if linked {
        conn.execute(
            "INSERT OR IGNORE INTO mcp_agent_links (server_id, agent_id, synced_at)
             VALUES (?1, ?2, ?3)",
            params![server_id, agent_id, now_secs()],
        )
    } else {
        conn.execute(
            "DELETE FROM mcp_agent_links WHERE server_id = ?1 AND agent_id = ?2",
            params![server_id, agent_id],
        )
    }
    .map_err(|e| e.to_string())?;
    Ok(())
}

/// All enabled servers linked to the given agent.
pub fn servers_for_agent(conn: &Connection, agent_id: &str) -> Result<Vec<McpServer>, String> {
    let servers = list_servers(conn)?;
    Ok(servers
        .into_iter()
        .filter(|s| s.enabled && s.linked_agents.iter().any(|a| a == agent_id))
        .collect())
}
