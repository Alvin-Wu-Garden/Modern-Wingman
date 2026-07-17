//! Tauri command layer for the central skill library.
//! Thin: parameter translation + locking only. Business logic lives in
//! `installer` / `repository` / `sync` / `risk`. (SRP)

use serde::Serialize;
use std::path::{Path, PathBuf};
use tauri::State;

use crate::skills::github::GithubClient;
use crate::skills::state::AppState;

use super::{
    installer, model::*, repository as repo, risk,
    sync::{self, SyncTarget},
};

/// Install result returned to the UI: the stored skill + its risk report.
#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct InstallToLibraryResult {
    pub skill: LibrarySkill,
    pub risk: RiskReport,
}

/// Agent presence info for the Agents workspace page.
#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AgentPresence {
    pub agent_id: String,
    pub detected: bool,
    /// Skills present in the agent's global dir that are NOT managed by Wingman
    pub unmanaged_skills: Vec<String>,
}

// ── Library CRUD ─────────────────────────────────────────────────────────────

#[tauri::command]
pub fn library_list_skills(state: State<'_, AppState>) -> Result<Vec<LibrarySkill>, String> {
    let conn = state.db.lock().map_err(|e| e.to_string())?;
    repo::list_skills(&conn)
}

/// Scans SKILL.md content for risk without installing (P3 preview gate).
#[tauri::command]
pub fn library_scan_risk(content: String) -> RiskReport {
    risk::scan(&content)
}

#[tauri::command]
pub async fn library_preview_skill(
    params: InstallToLibraryParams,
    state: State<'_, AppState>,
) -> Result<RiskReport, String> {
    let content = match params.source_kind.as_str() {
        "github" => {
            let (source_id, skill_name) = params
                .source_ref
                .split_once('/')
                .ok_or("github source_ref 格式須為 <source_id>/<skill_name>")?;
            let source = {
                let conn = state.db.lock().map_err(|error| error.to_string())?;
                crate::skills::sources::find_source(&conn, source_id)?
                    .ok_or_else(|| format!("Unknown source: {source_id}"))?
            };
            let github = GithubClient::new(state.http.clone());
            installer::preview_from_github(
                &github,
                &source.repo,
                &source.skills_root,
                skill_name,
                params.github_pat.as_deref(),
            ).await?
        }
        "local" => installer::preview_from_local(Path::new(&params.source_ref))?,
        "zip" => installer::preview_from_zip(Path::new(&params.source_ref))?,
        other => return Err(format!("未知的 source_kind: {other}")),
    };
    Ok(risk::scan(&content))
}

/// Installs a skill into the central library from GitHub / local dir / zip.
#[tauri::command]
pub async fn library_install_skill(
    params: InstallToLibraryParams,
    state: State<'_, AppState>,
) -> Result<InstallToLibraryResult, String> {
    // ── Acquire files (network / fs) ──────────────────────────────────────
    let acquired = match params.source_kind.as_str() {
        "github" => {
            // source_ref = "<source_id>/<skill_name>"
            let (source_id, skill_name) = params
                .source_ref
                .split_once('/')
                .ok_or("github source_ref 格式須為 <source_id>/<skill_name>")?;
            let source = {
                let conn = state.db.lock().map_err(|e| e.to_string())?;
                crate::skills::sources::find_source(&conn, source_id)?
                    .ok_or_else(|| format!("Unknown source: {source_id}"))?
            };
            let gh = GithubClient::new(state.http.clone());
            installer::install_from_github(
                &gh,
                &source.repo,
                &source.skills_root,
                skill_name,
                params.github_pat.as_deref(),
            )
            .await?
        }
        "local" => installer::install_from_local(Path::new(&params.source_ref))?,
        "zip" => installer::install_from_zip(Path::new(&params.source_ref))?,
        other => return Err(format!("未知的 source_kind: {other}")),
    };

    // ── Risk scan ─────────────────────────────────────────────────────────
    let report = risk::scan(&acquired.skill_md_content);
    let risk_notes = if report.findings.is_empty() {
        None
    } else {
        Some(
            report
                .findings
                .iter()
                .map(|f| format!("[{}] {}", f.rule, f.message))
                .collect::<Vec<_>>()
                .join("\n"),
        )
    };

    // ── Persist metadata ──────────────────────────────────────────────────
    let library_path = acquired.library_path.to_string_lossy().replace('\\', "/");
    let skill = {
        let conn = state.db.lock().map_err(|e| e.to_string())?;
        let id = repo::upsert_skill(
            &conn,
            &acquired.name,
            &acquired.display_name,
            acquired.description.as_deref(),
            &params.source_kind,
            &params.source_ref,
            &library_path,
            &acquired.content_hash,
            &report.level,
            risk_notes.as_deref(),
        )?;
        repo::get_skill(&conn, id)?.ok_or("安裝後讀取失敗")?
    };

    Ok(InstallToLibraryResult { skill, risk: report })
}

/// Removes a skill from the library: unsyncs all agent links then deletes files + row.
#[tauri::command]
pub fn library_remove_skill(skill_id: i64, state: State<'_, AppState>) -> Result<(), String> {
    let conn = state.db.lock().map_err(|e| e.to_string())?;
    let skill = repo::get_skill(&conn, skill_id)?.ok_or("Skill 不存在")?;

    for link in repo::links_for_skill(&conn, skill_id)? {
        let _ = sync::unsync_skill(Path::new(&link.target_path));
    }
    installer::remove_from_library(&skill.library_path)?;
    repo::delete_skill(&conn, skill_id)
}

#[tauri::command]
pub fn library_set_tags(
    skill_id: i64,
    tags: String,
    state: State<'_, AppState>,
) -> Result<(), String> {
    let conn = state.db.lock().map_err(|e| e.to_string())?;
    repo::set_skill_tags(&conn, skill_id, &tags)
}

/// Reads the SKILL.md of a library skill (for detail/preview page).
#[tauri::command]
pub fn library_read_skill_md(skill_id: i64, state: State<'_, AppState>) -> Result<String, String> {
    let library_path = {
        let conn = state.db.lock().map_err(|e| e.to_string())?;
        repo::get_skill(&conn, skill_id)?.ok_or("Skill 不存在")?.library_path
    };
    std::fs::read_to_string(Path::new(&library_path).join("SKILL.md")).map_err(|e| e.to_string())
}

// ── Sync to agents ───────────────────────────────────────────────────────────

/// Resolves the skills dir for (agent, scope). Global honours custom path.
fn resolve_agent_skills_dir(
    conn: &rusqlite::Connection,
    agent_id: &str,
    scope: &str,
    project_path: Option<&str>,
) -> Result<PathBuf, String> {
    if scope == "global" {
        let (global, custom): (String, Option<String>) = conn
            .query_row(
                "SELECT global_skills_path, custom_global_path FROM agents WHERE id = ?1",
                rusqlite::params![agent_id],
                |r| Ok((r.get(0)?, r.get(1)?)),
            )
            .map_err(|e| e.to_string())?;
        Ok(PathBuf::from(custom.unwrap_or(global)))
    } else {
        let proj = project_path.ok_or("project scope 需要 project_path")?;
        let subpath: String = conn
            .query_row(
                "SELECT project_skills_subpath FROM agents WHERE id = ?1",
                rusqlite::params![agent_id],
                |r| r.get(0),
            )
            .map_err(|e| e.to_string())?;
        Ok(PathBuf::from(proj).join(subpath))
    }
}

/// Syncs a library skill to an agent (junction → symlink → copy).
#[tauri::command]
pub fn library_sync_skill(
    skill_id: i64,
    agent_id: String,
    scope: String,
    project_path: Option<String>,
    state: State<'_, AppState>,
) -> Result<SkillAgentLink, String> {
    let conn = state.db.lock().map_err(|e| e.to_string())?;
    let skill = repo::get_skill(&conn, skill_id)?.ok_or("Skill 不存在")?;
    let agent_dir = resolve_agent_skills_dir(&conn, &agent_id, &scope, project_path.as_deref())?;

    let outcome = sync::sync_skill(
        Path::new(&skill.library_path),
        &SyncTarget {
            agent_skills_dir: agent_dir,
            skill_name: skill.name.clone(),
        },
    )?;

    let target_path = outcome.target_path.to_string_lossy().replace('\\', "/");
    let link_id = repo::insert_link(
        &conn,
        skill_id,
        &agent_id,
        &scope,
        project_path.as_deref(),
        &target_path,
        outcome.mode,
    )?;
    repo::get_link(&conn, link_id)?.ok_or("同步後讀取失敗".into())
}

/// Removes a sync link (files + DB row).
#[tauri::command]
pub fn library_unsync_skill(link_id: i64, state: State<'_, AppState>) -> Result<(), String> {
    let conn = state.db.lock().map_err(|e| e.to_string())?;
    let link = repo::get_link(&conn, link_id)?.ok_or("Link 不存在")?;
    sync::unsync_skill(Path::new(&link.target_path))?;
    repo::delete_link(&conn, link_id)
}

#[tauri::command]
pub fn library_list_links(state: State<'_, AppState>) -> Result<Vec<SkillAgentLink>, String> {
    let conn = state.db.lock().map_err(|e| e.to_string())?;
    repo::list_links(&conn)
}

// ── Agent detection & adoption ───────────────────────────────────────────────

/// Detects which agents are installed and lists unmanaged skills in their dirs.
#[tauri::command]
pub fn library_detect_agents(state: State<'_, AppState>) -> Result<Vec<AgentPresence>, String> {
    let conn = state.db.lock().map_err(|e| e.to_string())?;

    let mut stmt = conn
        .prepare(
            "SELECT id, global_skills_path, custom_global_path, detect_path FROM agents",
        )
        .map_err(|e| e.to_string())?;
    let agents: Vec<(String, String, Option<String>, Option<String>)> = stmt
        .query_map(rusqlite::params![], |r| {
            Ok((r.get(0)?, r.get(1)?, r.get(2)?, r.get(3)?))
        })
        .map_err(|e| e.to_string())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|e| e.to_string())?;

    // Managed target dir names per agent
    let links = repo::list_links(&conn)?;

    let mut result = Vec::with_capacity(agents.len());
    for (id, global, custom, detect) in agents {
        let detected = detect
            .as_deref()
            .map(|p| Path::new(p).exists())
            .unwrap_or(false);

        let skills_dir = PathBuf::from(custom.unwrap_or(global));
        let managed: Vec<String> = links
            .iter()
            .filter(|l| l.agent_id == id && l.scope == "global")
            .filter_map(|l| {
                Path::new(&l.target_path)
                    .file_name()
                    .map(|n| n.to_string_lossy().to_string())
            })
            .collect();

        let mut unmanaged = Vec::new();
        if let Ok(entries) = std::fs::read_dir(&skills_dir) {
            for entry in entries.flatten() {
                let name = entry.file_name().to_string_lossy().to_string();
                let is_skill_dir = entry.path().join("SKILL.md").exists();
                if is_skill_dir && !managed.contains(&name) {
                    unmanaged.push(name);
                }
            }
        }

        result.push(AgentPresence {
            agent_id: id,
            detected,
            unmanaged_skills: unmanaged,
        });
    }
    Ok(result)
}

/// Adopts an unmanaged skill from an agent dir into the central library,
/// then replaces the original with a sync link.
#[tauri::command]
pub fn library_adopt_skill(
    agent_id: String,
    skill_name: String,
    state: State<'_, AppState>,
) -> Result<LibrarySkill, String> {
    let conn = state.db.lock().map_err(|e| e.to_string())?;
    let agent_dir = resolve_agent_skills_dir(&conn, &agent_id, "global", None)?;
    let source_dir = agent_dir.join(&skill_name);

    let acquired = installer::install_from_local(&source_dir)?;
    let report = risk::scan(&acquired.skill_md_content);

    let library_path = acquired.library_path.to_string_lossy().replace('\\', "/");
    let skill_id = repo::upsert_skill(
        &conn,
        &acquired.name,
        &acquired.display_name,
        acquired.description.as_deref(),
        "local",
        &source_dir.to_string_lossy().replace('\\', "/"),
        &library_path,
        &acquired.content_hash,
        &report.level,
        None,
    )?;

    // Replace original with a managed link.
    sync::unsync_skill(&source_dir)?;
    let outcome = sync::sync_skill(
        Path::new(&library_path),
        &SyncTarget {
            agent_skills_dir: agent_dir,
            skill_name: skill_name.clone(),
        },
    )?;
    repo::insert_link(
        &conn,
        skill_id,
        &agent_id,
        "global",
        None,
        &outcome.target_path.to_string_lossy().replace('\\', "/"),
        outcome.mode,
    )?;

    repo::get_skill(&conn, skill_id)?.ok_or("認養後讀取失敗".into())
}

// ── Presets ──────────────────────────────────────────────────────────────────

#[tauri::command]
pub fn library_list_presets(state: State<'_, AppState>) -> Result<Vec<SkillPreset>, String> {
    let conn = state.db.lock().map_err(|e| e.to_string())?;
    repo::list_presets(&conn)
}

#[tauri::command]
pub fn library_create_preset(name: String, state: State<'_, AppState>) -> Result<i64, String> {
    let conn = state.db.lock().map_err(|e| e.to_string())?;
    repo::create_preset(&conn, &name)
}

#[tauri::command]
pub fn library_delete_preset(preset_id: i64, state: State<'_, AppState>) -> Result<(), String> {
    let conn = state.db.lock().map_err(|e| e.to_string())?;
    repo::delete_preset(&conn, preset_id)
}

#[tauri::command]
pub fn library_set_preset_member(
    preset_id: i64,
    skill_id: i64,
    member: bool,
    state: State<'_, AppState>,
) -> Result<(), String> {
    let conn = state.db.lock().map_err(|e| e.to_string())?;
    repo::set_preset_member(&conn, preset_id, skill_id, member)
}

/// Applies a preset: syncs every member skill to the given agent+scope.
#[tauri::command]
pub fn library_apply_preset(
    preset_id: i64,
    agent_id: String,
    scope: String,
    project_path: Option<String>,
    state: State<'_, AppState>,
) -> Result<Vec<SkillAgentLink>, String> {
    let skill_ids: Vec<i64> = {
        let conn = state.db.lock().map_err(|e| e.to_string())?;
        repo::list_presets(&conn)?
            .into_iter()
            .find(|p| p.id == preset_id)
            .ok_or("Preset 不存在")?
            .skill_ids
    };

    let mut links = Vec::with_capacity(skill_ids.len());
    for skill_id in skill_ids {
        let conn = state.db.lock().map_err(|e| e.to_string())?;
        let skill = repo::get_skill(&conn, skill_id)?.ok_or("Skill 不存在")?;
        let agent_dir =
            resolve_agent_skills_dir(&conn, &agent_id, &scope, project_path.as_deref())?;
        let outcome = sync::sync_skill(
            Path::new(&skill.library_path),
            &SyncTarget {
                agent_skills_dir: agent_dir,
                skill_name: skill.name.clone(),
            },
        )?;
        let link_id = repo::insert_link(
            &conn,
            skill_id,
            &agent_id,
            &scope,
            project_path.as_deref(),
            &outcome.target_path.to_string_lossy().replace('\\', "/"),
            outcome.mode,
        )?;
        if let Some(link) = repo::get_link(&conn, link_id)? {
            links.push(link);
        }
    }
    Ok(links)
}
