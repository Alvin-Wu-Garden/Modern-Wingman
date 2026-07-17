//! Central Skill Library (WS1).
//!
//! Layout on disk: `~/.wingman/library/skills/<skill-name>/SKILL.md` (+ assets).
//! Metadata lives in SQLite (`library_skills`, `skill_agent_links`, presets).
//! Sync engine links/copies library skills into each agent's skills directory.

pub mod commands;
pub mod installer;
pub mod model;
pub mod repository;
pub mod risk;
pub mod sync;

use std::path::PathBuf;

/// Root of the central library: `~/.wingman/library/skills`
pub fn library_skills_dir() -> PathBuf {
    dirs::home_dir()
        .unwrap_or_default()
        .join(".wingman")
        .join("library")
        .join("skills")
}
