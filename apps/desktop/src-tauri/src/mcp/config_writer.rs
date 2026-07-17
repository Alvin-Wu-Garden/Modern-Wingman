//! Writes MCP server definitions into agent config files.
//!
//! Most agents use the de-facto standard JSON shape:
//! `{ "mcpServers": { "<name>": { "command", "args", "env" } | { "url" } } }`
//! The writer merges only the `mcpServers` key managed by Wingman and never
//! touches unrelated keys in the target file.

use serde_json::{json, Map, Value};
use std::fs;
use std::path::Path;

use super::model::McpServer;

/// Converts a registry server to the standard mcpServers entry value.
fn to_entry(server: &McpServer) -> Value {
    match server.transport.as_str() {
        "stdio" => {
            let mut obj = Map::new();
            if let Some(cmd) = &server.command {
                obj.insert("command".into(), json!(cmd));
            }
            if !server.args.is_empty() {
                obj.insert("args".into(), json!(server.args));
            }
            if !server.env.is_empty() {
                obj.insert("env".into(), json!(server.env));
            }
            Value::Object(obj)
        }
        // sse / http
        _ => {
            let mut obj = Map::new();
            if let Some(url) = &server.url {
                obj.insert("url".into(), json!(url));
            }
            obj.insert("type".into(), json!(server.transport));
            Value::Object(obj)
        }
    }
}

/// Merges the given servers into the JSON config at `config_path`.
///
/// `managed_names` is the full set of Wingman-managed server names for this
/// agent — entries with those names that are no longer in `servers` get
/// removed; user-added entries with other names are preserved.
pub fn write_agent_config(
    config_path: &Path,
    servers: &[McpServer],
    managed_names: &[String],
) -> Result<(), String> {
    // TOML configs (e.g. Codex) are not yet supported — skip gracefully.
    if config_path.extension().and_then(|e| e.to_str()) == Some("toml") {
        return Err(format!(
            "暫不支援 TOML 格式的 MCP 設定檔: {}",
            config_path.display()
        ));
    }

    let mut root: Value = if config_path.exists() {
        let text = fs::read_to_string(config_path).map_err(|e| e.to_string())?;
        serde_json::from_str(&text).unwrap_or_else(|_| json!({}))
    } else {
        json!({})
    };

    let obj = root
        .as_object_mut()
        .ok_or("設定檔根節點不是 JSON 物件")?;
    let mcp = obj
        .entry("mcpServers")
        .or_insert_with(|| json!({}))
        .as_object_mut()
        .ok_or("mcpServers 不是 JSON 物件")?;

    // Remove managed entries no longer present.
    let keep: Vec<String> = servers.iter().map(|s| s.name.clone()).collect();
    for name in managed_names {
        if !keep.contains(name) {
            mcp.remove(name);
        }
    }
    // Upsert current entries.
    for server in servers {
        mcp.insert(server.name.clone(), to_entry(server));
    }

    if let Some(parent) = config_path.parent() {
        fs::create_dir_all(parent).map_err(|e| e.to_string())?;
    }
    let text = serde_json::to_string_pretty(&root).map_err(|e| e.to_string())?;
    fs::write(config_path, text).map_err(|e| e.to_string())?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::HashMap;

    fn server(name: &str) -> McpServer {
        McpServer {
            id: 1,
            name: name.into(),
            transport: "stdio".into(),
            command: Some("npx".into()),
            args: vec!["-y".into(), "some-mcp".into()],
            url: None,
            env: HashMap::new(),
            enabled: true,
            created_at: 0,
            updated_at: 0,
            linked_agents: vec![],
        }
    }

    #[test]
    fn writes_and_preserves_unrelated_keys() {
        let tmp = std::env::temp_dir().join(format!("mw-mcp-test-{}.json", std::process::id()));
        fs::write(&tmp, r#"{"otherSetting": true, "mcpServers": {"user-added": {"command": "x"}}}"#).unwrap();

        write_agent_config(&tmp, &[server("wingman-fs")], &["wingman-fs".to_string()]).unwrap();

        let text = fs::read_to_string(&tmp).unwrap();
        let v: Value = serde_json::from_str(&text).unwrap();
        assert_eq!(v["otherSetting"], json!(true));
        assert!(v["mcpServers"]["user-added"].is_object(), "user entry must survive");
        assert_eq!(v["mcpServers"]["wingman-fs"]["command"], json!("npx"));

        let _ = fs::remove_file(&tmp);
    }

    #[test]
    fn removes_stale_managed_entries() {
        let tmp = std::env::temp_dir().join(format!("mw-mcp-stale-{}.json", std::process::id()));
        fs::write(&tmp, r#"{"mcpServers": {"old-managed": {"command": "y"}}}"#).unwrap();

        write_agent_config(&tmp, &[], &["old-managed".to_string()]).unwrap();

        let v: Value = serde_json::from_str(&fs::read_to_string(&tmp).unwrap()).unwrap();
        assert!(v["mcpServers"].get("old-managed").is_none());

        let _ = fs::remove_file(&tmp);
    }

    #[test]
    fn rejects_toml() {
        let result = write_agent_config(Path::new("config.toml"), &[], &[]);
        assert!(result.is_err());
    }
}
