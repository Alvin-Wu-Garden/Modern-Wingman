use serde::{Deserialize, Serialize};

/// An MCP server definition in the registry.
#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct McpServer {
    pub id: i64,
    pub name: String,
    /// "stdio" | "sse" | "http"
    pub transport: String,
    pub command: Option<String>,
    /// JSON array of strings
    pub args: Vec<String>,
    pub url: Option<String>,
    /// Environment variables
    pub env: std::collections::HashMap<String, String>,
    pub enabled: bool,
    pub created_at: i64,
    pub updated_at: i64,
    /// Agent IDs this server is synced to
    pub linked_agents: Vec<String>,
}

/// Parameters for creating/updating an MCP server.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct UpsertMcpServerParams {
    pub id: Option<i64>,
    pub name: String,
    pub transport: String,
    pub command: Option<String>,
    #[serde(default)]
    pub args: Vec<String>,
    pub url: Option<String>,
    #[serde(default)]
    pub env: std::collections::HashMap<String, String>,
    #[serde(default = "default_true")]
    pub enabled: bool,
}

fn default_true() -> bool {
    true
}
