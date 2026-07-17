use serde::{Deserialize, Serialize};

/// A skill stored in the central library.
#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct LibrarySkill {
    pub id: i64,
    pub name: String,
    pub display_name: String,
    pub description: Option<String>,
    /// "github" | "local" | "zip"
    pub source_kind: String,
    /// e.g. "anthropics/skills@pdf" or a filesystem path
    pub source_ref: String,
    pub library_path: String,
    pub content_hash: String,
    /// "low" | "medium" | "high"
    pub risk_level: String,
    pub risk_notes: Option<String>,
    /// Comma-separated tags
    pub tags: String,
    pub installed_at: i64,
    pub updated_at: i64,
}

/// A sync link between a library skill and an agent's skills directory.
#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SkillAgentLink {
    pub id: i64,
    pub skill_id: i64,
    pub agent_id: String,
    /// "global" | "project"
    pub scope: String,
    pub project_path: Option<String>,
    pub target_path: String,
    /// "junction" | "symlink" | "copy" — the mode actually used
    pub sync_mode: String,
    pub synced_at: i64,
}

/// Result of the pre-install risk scan (P3 skill quality gate).
#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RiskReport {
    /// "low" | "medium" | "high"
    pub level: String,
    pub findings: Vec<RiskFinding>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RiskFinding {
    /// "low" | "medium" | "high"
    pub severity: String,
    pub rule: String,
    pub message: String,
    /// The matched excerpt from SKILL.md
    pub excerpt: String,
}

/// A named preset (group of skills applied in one click).
#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SkillPreset {
    pub id: i64,
    pub name: String,
    pub skill_ids: Vec<i64>,
}

/// Parameters for installing a skill into the central library.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct InstallToLibraryParams {
    /// "github" | "local" | "zip"
    pub source_kind: String,
    /// github: "<source_id>/<skill_name>" ; local: dir path ; zip: zip path
    pub source_ref: String,
    pub github_pat: Option<String>,
}
