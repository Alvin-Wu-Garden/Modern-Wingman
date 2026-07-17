use serde::Serialize;
use tauri::State;

use crate::skills::{
    github::GithubClient,
    sources::{self, SkillSourceInfo},
    state::AppState,
};

// ── Response types ────────────────────────────────────────────────────────────

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SkillMeta {
    pub id: String,
    pub source_id: String,
    pub skill_name: String,
    pub display_name: String,
    pub description: Option<String>,
    pub cached_at: i64,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct InstalledSkillInfo {
    pub id: i64,
    pub source_id: String,
    pub skill_name: String,
    pub agent_id: String,
    pub scope: String,
    pub project_path: Option<String>,
    pub installed_path: String,
    pub installed_at: i64,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AgentInfo {
    pub id: String,
    pub display_name: String,
    pub global_skills_path: String,
    pub project_skills_subpath: String,
    pub is_builtin: bool,
    pub icon: Option<String>,
    pub custom_global_path: Option<String>,
    /// Resolved path: custom_global_path if set, otherwise global_skills_path
    pub effective_global_path: String,
}

// ── Helpers ───────────────────────────────────────────────────────────────────

fn title_case(skill_name: &str) -> String {
    skill_name
        .split('-')
        .map(|word| {
            let mut c = word.chars();
            match c.next() {
                None => String::new(),
                Some(first) => first.to_uppercase().collect::<String>() + c.as_str(),
            }
        })
        .collect::<Vec<_>>()
        .join(" ")
}

fn now_secs() -> i64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs() as i64
}

const CACHE_TTL_SECS: i64 = 3600; // 1 hour
const README_CACHE_TTL_SECS: i64 = 604_800; // 7 days

// ── Commands ──────────────────────────────────────────────────────────────────

/// Returns the list of skill source repositories (data-driven, from SQLite).
#[tauri::command]
pub fn list_skill_sources(state: State<'_, AppState>) -> Result<Vec<SkillSourceInfo>, String> {
    let conn = state.db.lock().map_err(|e| e.to_string())?;
    sources::all_sources(&conn)
}

/// Adds a custom skill source (marketplace repo) at runtime.
#[tauri::command]
pub fn add_skill_source(
    id: String,
    display_name: String,
    repo: String,
    skills_root: String,
    state: State<'_, AppState>,
) -> Result<(), String> {
    let conn = state.db.lock().map_err(|e| e.to_string())?;
    sources::add_custom_source(&conn, &id, &display_name, &repo, &skills_root)
}

/// Removes a custom skill source. Built-in sources can only be disabled.
#[tauri::command]
pub fn remove_skill_source(id: String, state: State<'_, AppState>) -> Result<(), String> {
    let conn = state.db.lock().map_err(|e| e.to_string())?;
    sources::remove_custom_source(&conn, &id)
}

/// Fetches skills for a given source. Returns cached results when still fresh.
#[tauri::command]
pub async fn fetch_remote_skills(
    source_id: String,
    github_pat: Option<String>,
    state: State<'_, AppState>,
) -> Result<Vec<SkillMeta>, String> {
    let now = now_secs();

    // ── Check cache ────────────────────────────────────────────────────────
    let cached: Vec<SkillMeta> = {
        let conn = state.db.lock().map_err(|e| e.to_string())?;
        let mut stmt = conn
            .prepare(
                "SELECT id, source_id, skill_name, display_name, description, cached_at
                 FROM skill_cache
                 WHERE source_id = ?1 AND cached_at > ?2",
            )
            .map_err(|e| e.to_string())?;

        let rows: Vec<SkillMeta> = stmt.query_map(
            rusqlite::params![source_id, now - CACHE_TTL_SECS],
            |row| {
                Ok(SkillMeta {
                    id: row.get(0)?,
                    source_id: row.get(1)?,
                    skill_name: row.get(2)?,
                    display_name: row.get::<_, Option<String>>(3)?
                        .unwrap_or_default(),
                    description: row.get(4)?,
                    cached_at: row.get(5)?,
                })
            },
        )
        .map_err(|e| e.to_string())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|e| e.to_string())?;
        rows
    };

    if !cached.is_empty() {
        return Ok(cached);
    }

    // ── Fetch from GitHub ──────────────────────────────────────────────────
    let source = {
        let conn = state.db.lock().map_err(|e| e.to_string())?;
        sources::find_source(&conn, &source_id)?
            .ok_or_else(|| format!("Unknown source: {source_id}"))?
    };

    let gh = GithubClient::new(state.http.clone());
    let items = gh
        .list_directory(&source.repo, &source.skills_root, github_pat.as_deref())
        .await?;

    let skills: Vec<SkillMeta> = items
        .into_iter()
        .filter(|item| item.item_type == "dir")
        .map(|item| SkillMeta {
            id: format!("{}/{}", source_id, item.name),
            source_id: source_id.clone(),
            display_name: title_case(&item.name),
            skill_name: item.name,
            description: None,
            cached_at: now,
        })
        .collect();

    // ── Persist cache ──────────────────────────────────────────────────────
    {
        let conn = state.db.lock().map_err(|e| e.to_string())?;
        conn.execute(
            "DELETE FROM skill_cache WHERE source_id = ?1",
            rusqlite::params![source_id],
        )
        .map_err(|e| e.to_string())?;

        for skill in &skills {
            conn.execute(
                "INSERT INTO skill_cache
                 (id, source_id, skill_name, display_name, description, cached_at)
                 VALUES (?1, ?2, ?3, ?4, ?5, ?6)",
                rusqlite::params![
                    skill.id,
                    skill.source_id,
                    skill.skill_name,
                    skill.display_name,
                    skill.description,
                    skill.cached_at
                ],
            )
            .map_err(|e| e.to_string())?;
        }
    }

    Ok(skills)
}

/// Downloads and returns the raw SKILL.md content for a given skill.
#[tauri::command]
pub async fn get_skill_readme(
    source_id: String,
    skill_name: String,
    github_pat: Option<String>,
    state: State<'_, AppState>,
) -> Result<String, String> {
    let skill_id = format!("{}/{}", source_id, skill_name);
    let now = now_secs();

    // ── Check SQLite cache first (7-day TTL) ──────────────────────────────
    {
        let conn = state.db.lock().map_err(|e| e.to_string())?;
        let cached: Option<String> = conn
            .query_row(
                "SELECT content FROM readme_cache WHERE skill_id = ?1 AND cached_at > ?2",
                rusqlite::params![skill_id, now - README_CACHE_TTL_SECS],
                |r| r.get(0),
            )
            .ok();
        if let Some(content) = cached {
            return Ok(content);
        }
    }

    // ── Fetch from GitHub ─────────────────────────────────────────────────
    let source = {
        let conn = state.db.lock().map_err(|e| e.to_string())?;
        sources::find_source(&conn, &source_id)?
            .ok_or_else(|| format!("Unknown source: {source_id}"))?
    };

    let gh = GithubClient::new(state.http.clone());
    let dir_path = format!("{}/{}", source.skills_root, skill_name);
    let items = gh
        .list_directory(&source.repo, &dir_path, github_pat.as_deref())
        .await?;

    let skill_file = items
        .iter()
        .find(|item| item.item_type == "file" && item.name == "SKILL.md")
        .ok_or_else(|| format!("SKILL.md not found in {source_id}/{skill_name}"))?;

    let url = skill_file
        .download_url
        .as_deref()
        .ok_or("No download_url for SKILL.md")?;

    let content = gh.get_file_content(url, github_pat.as_deref()).await?;

    // ── Persist to SQLite cache ───────────────────────────────────────────
    {
        let conn = state.db.lock().map_err(|e| e.to_string())?;
        conn.execute(
            "INSERT OR REPLACE INTO readme_cache (skill_id, content, cached_at) VALUES (?1, ?2, ?3)",
            rusqlite::params![skill_id, content, now],
        )
        .map_err(|e| e.to_string())?;
    }

    Ok(content)
}

/// Downloads SKILL.md and writes it to the target agent directory.
#[tauri::command]
pub async fn install_skill(
    source_id: String,
    skill_name: String,
    agent_id: String,
    scope: String,
    project_path: Option<String>,
    github_pat: Option<String>,
    state: State<'_, AppState>,
) -> Result<InstalledSkillInfo, String> {
    // ── Resolve target directory ───────────────────────────────────────────
    let target_base: String = {
        let conn = state.db.lock().map_err(|e| e.to_string())?;

        if scope == "global" {
            let custom: Option<String> = conn
                .query_row(
                    "SELECT custom_global_path FROM agents WHERE id = ?1",
                    rusqlite::params![agent_id],
                    |r| r.get(0),
                )
                .map_err(|e| e.to_string())?;
            let global: String = conn
                .query_row(
                    "SELECT global_skills_path FROM agents WHERE id = ?1",
                    rusqlite::params![agent_id],
                    |r| r.get(0),
                )
                .map_err(|e| e.to_string())?;
            custom.unwrap_or(global)
        } else {
            let proj = project_path
                .as_deref()
                .ok_or("project_path is required for project scope")?;
            let subpath: String = conn
                .query_row(
                    "SELECT project_skills_subpath FROM agents WHERE id = ?1",
                    rusqlite::params![agent_id],
                    |r| r.get(0),
                )
                .map_err(|e| e.to_string())?;
            format!("{}/{}", proj, subpath)
        }
    };

    let skill_dir = std::path::PathBuf::from(&target_base).join(&skill_name);

    // ── Fetch SKILL.md ────────────────────────────────────────────────────
    let source = {
        let conn = state.db.lock().map_err(|e| e.to_string())?;
        sources::find_source(&conn, &source_id)?
            .ok_or_else(|| format!("Unknown source: {source_id}"))?
    };

    let gh = GithubClient::new(state.http.clone());
    let dir_path = format!("{}/{}", source.skills_root, skill_name);
    let items = gh
        .list_directory(&source.repo, &dir_path, github_pat.as_deref())
        .await?;

    let skill_file = items
        .iter()
        .find(|item| item.item_type == "file" && item.name == "SKILL.md")
        .ok_or_else(|| format!("SKILL.md not found in {source_id}/{skill_name}"))?;

    let url = skill_file
        .download_url
        .as_deref()
        .ok_or("No download_url for SKILL.md")?;

    let content = gh.get_file_content(url, github_pat.as_deref()).await?;

    // ── Write file ────────────────────────────────────────────────────────
    std::fs::create_dir_all(&skill_dir).map_err(|e| e.to_string())?;
    let skill_md_path = skill_dir.join("SKILL.md");
    std::fs::write(&skill_md_path, content.as_bytes()).map_err(|e| e.to_string())?;

    let installed_path = skill_md_path.to_string_lossy().replace('\\', "/");

    // ── Record in DB ──────────────────────────────────────────────────────
    let installed_at = now_secs();
    let id = {
        let conn = state.db.lock().map_err(|e| e.to_string())?;
        conn.execute(
            "INSERT INTO installed_skills
             (source_id, skill_name, agent_id, scope, project_path, installed_path, installed_at)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7)",
            rusqlite::params![
                source_id,
                skill_name,
                agent_id,
                scope,
                project_path,
                installed_path,
                installed_at
            ],
        )
        .map_err(|e| e.to_string())?;
        conn.last_insert_rowid()
    };

    Ok(InstalledSkillInfo {
        id,
        source_id,
        skill_name,
        agent_id,
        scope,
        project_path,
        installed_path,
        installed_at,
    })
}

/// Removes an installed skill's files and its database record.
#[tauri::command]
pub async fn uninstall_skill(
    install_id: i64,
    state: State<'_, AppState>,
) -> Result<(), String> {
    // Fetch path first (release lock before file I/O)
    let installed_path: String = {
        let conn = state.db.lock().map_err(|e| e.to_string())?;
        conn.query_row(
            "SELECT installed_path FROM installed_skills WHERE id = ?1",
            rusqlite::params![install_id],
            |r| r.get(0),
        )
        .map_err(|e| e.to_string())?
    };

    let skill_md = std::path::Path::new(&installed_path);
    if skill_md.exists() {
        std::fs::remove_file(skill_md).map_err(|e| e.to_string())?;
    }
    // Remove parent dir if now empty
    if let Some(parent) = skill_md.parent() {
        let _ = std::fs::remove_dir(parent); // ignore error — may not be empty
    }

    // Remove DB record
    {
        let conn = state.db.lock().map_err(|e| e.to_string())?;
        conn.execute(
            "DELETE FROM installed_skills WHERE id = ?1",
            rusqlite::params![install_id],
        )
        .map_err(|e| e.to_string())?;
    }

    Ok(())
}

/// Returns all skills currently tracked as installed.
#[tauri::command]
pub fn list_installed_skills(
    state: State<'_, AppState>,
) -> Result<Vec<InstalledSkillInfo>, String> {
    let conn = state.db.lock().map_err(|e| e.to_string())?;
    let mut stmt = conn
        .prepare(
            "SELECT id, source_id, skill_name, agent_id, scope,
                    project_path, installed_path, installed_at
             FROM installed_skills
             ORDER BY installed_at DESC",
        )
        .map_err(|e| e.to_string())?;

    let result: Vec<InstalledSkillInfo> = stmt
        .query_map(rusqlite::params![], |row| {
            Ok(InstalledSkillInfo {
                id: row.get(0)?,
                source_id: row.get(1)?,
                skill_name: row.get(2)?,
                agent_id: row.get(3)?,
                scope: row.get(4)?,
                project_path: row.get(5)?,
                installed_path: row.get(6)?,
                installed_at: row.get(7)?,
            })
        })
        .map_err(|e| e.to_string())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|e| e.to_string())?;
    Ok(result)
}

/// Returns all configured agent targets (built-in + custom).
#[tauri::command]
pub fn list_agents(state: State<'_, AppState>) -> Result<Vec<AgentInfo>, String> {
    let conn = state.db.lock().map_err(|e| e.to_string())?;
    let mut stmt = conn
        .prepare(
            "SELECT id, display_name, global_skills_path, project_skills_subpath,
                    is_builtin, icon, custom_global_path
             FROM agents
             ORDER BY is_builtin DESC, display_name",
        )
        .map_err(|e| e.to_string())?;

    let result: Vec<AgentInfo> = stmt
        .query_map(rusqlite::params![], |row| {
            let global_path: String = row.get(2)?;
            let custom: Option<String> = row.get(6)?;
            let effective = custom.clone().unwrap_or_else(|| global_path.clone());
            Ok(AgentInfo {
                id: row.get(0)?,
                display_name: row.get(1)?,
                global_skills_path: global_path,
                project_skills_subpath: row.get(3)?,
                is_builtin: row.get::<_, i64>(4)? == 1,
                icon: row.get(5)?,
                custom_global_path: custom,
                effective_global_path: effective,
            })
        })
        .map_err(|e| e.to_string())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|e| e.to_string())?;
    Ok(result)
}

/// Persists a custom global-skills-path override for an agent.
/// Pass `None` for `custom_global_path` to reset to default.
#[tauri::command]
pub fn update_agent_path(
    agent_id: String,
    custom_global_path: Option<String>,
    state: State<'_, AppState>,
) -> Result<(), String> {
    let conn = state.db.lock().map_err(|e| e.to_string())?;
    conn.execute(
        "UPDATE agents SET custom_global_path = ?1 WHERE id = ?2",
        rusqlite::params![custom_global_path, agent_id],
    )
    .map_err(|e| e.to_string())?;
    Ok(())
}
