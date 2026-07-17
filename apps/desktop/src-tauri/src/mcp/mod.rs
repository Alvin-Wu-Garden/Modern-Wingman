//! MCP server registry (WS1.5).
//!
//! Stores MCP server definitions in SQLite and syncs them into each agent's
//! MCP config file (JSON formats vary per agent; the writer merges under the
//! standard `mcpServers` key and never touches other keys).

pub mod commands;
pub mod config_writer;
pub mod model;
pub mod repository;
