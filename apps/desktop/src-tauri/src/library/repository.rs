//! SQLite persistence for the central skill library.
//! Pure data access — no file I/O, no network. (SRP)

use rusqlite::{params, Connection, OptionalExtension};

use super::model::{LibrarySkill, SkillAgentLink, SkillPreset};

fn now_secs() -> i64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs() as i64
}

fn row_to_skill(row: &rusqlite::Row<'_>) -> rusqlite::Result<LibrarySkill> {
    Ok(LibrarySkill {
        id: row.get(0)?,
        name: row.get(1)?,
        display_name: row.get(2)?,
        description: row.get(3)?,
        source_kind: row.get(4)?,
        source_ref: row.get(5)?,
        library_path: row.get(6)?,
        content_hash: row.get(7)?,
        risk_level: row.get(8)?,
        risk_notes: row.get(9)?,
        tags: row.get(10)?,
        installed_at: row.get(11)?,
        updated_at: row.get(12)?,
    })
}

const SKILL_COLS: &str = "id, name, display_name, description, source_kind, source_ref, \
                          library_path, content_hash, risk_level, risk_notes, tags, \
                          installed_at, updated_at";

pub fn list_skills(conn: &Connection) -> Result<Vec<LibrarySkill>, String> {
    let mut stmt = conn
        .prepare(&format!(
            "SELECT {SKILL_COLS} FROM library_skills ORDER BY name"
        ))
        .map_err(|e| e.to_string())?;
    let rows = stmt
        .query_map(params![], row_to_skill)
        .map_err(|e| e.to_string())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|e| e.to_string())?;
    Ok(rows)
}

pub fn get_skill(conn: &Connection, id: i64) -> Result<Option<LibrarySkill>, String> {
    conn.query_row(
        &format!("SELECT {SKILL_COLS} FROM library_skills WHERE id = ?1"),
        params![id],
        row_to_skill,
    )
    .optional()
    .map_err(|e| e.to_string())
}

pub fn get_skill_by_name(conn: &Connection, name: &str) -> Result<Option<LibrarySkill>, String> {
    conn.query_row(
        &format!("SELECT {SKILL_COLS} FROM library_skills WHERE name = ?1"),
        params![name],
        row_to_skill,
    )
    .optional()
    .map_err(|e| e.to_string())
}

#[allow(clippy::too_many_arguments)]
pub fn upsert_skill(
    conn: &Connection,
    name: &str,
    display_name: &str,
    description: Option<&str>,
    source_kind: &str,
    source_ref: &str,
    library_path: &str,
    content_hash: &str,
    risk_level: &str,
    risk_notes: Option<&str>,
) -> Result<i64, String> {
    let now = now_secs();
    conn.execute(
        "INSERT INTO library_skills
         (name, display_name, description, source_kind, source_ref, library_path,
          content_hash, risk_level, risk_notes, tags, installed_at, updated_at)
         VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, '', ?10, ?10)
         ON CONFLICT(name) DO UPDATE SET
            display_name = excluded.display_name,
            description  = excluded.description,
            source_kind  = excluded.source_kind,
            source_ref   = excluded.source_ref,
            library_path = excluded.library_path,
            content_hash = excluded.content_hash,
            risk_level   = excluded.risk_level,
            risk_notes   = excluded.risk_notes,
            updated_at   = excluded.updated_at",
        params![
            name, display_name, description, source_kind, source_ref,
            library_path, content_hash, risk_level, risk_notes, now
        ],
    )
    .map_err(|e| e.to_string())?;

    conn.query_row(
        "SELECT id FROM library_skills WHERE name = ?1",
        params![name],
        |r| r.get(0),
    )
    .map_err(|e| e.to_string())
}

pub fn delete_skill(conn: &Connection, id: i64) -> Result<(), String> {
    conn.execute("DELETE FROM library_skills WHERE id = ?1", params![id])
        .map_err(|e| e.to_string())?;
    Ok(())
}

pub fn set_skill_tags(conn: &Connection, id: i64, tags: &str) -> Result<(), String> {
    conn.execute(
        "UPDATE library_skills SET tags = ?1, updated_at = ?2 WHERE id = ?3",
        params![tags, now_secs(), id],
    )
    .map_err(|e| e.to_string())?;
    Ok(())
}

// ── Agent links ──────────────────────────────────────────────────────────────

fn row_to_link(row: &rusqlite::Row<'_>) -> rusqlite::Result<SkillAgentLink> {
    Ok(SkillAgentLink {
        id: row.get(0)?,
        skill_id: row.get(1)?,
        agent_id: row.get(2)?,
        scope: row.get(3)?,
        project_path: row.get(4)?,
        target_path: row.get(5)?,
        sync_mode: row.get(6)?,
        synced_at: row.get(7)?,
    })
}

const LINK_COLS: &str =
    "id, skill_id, agent_id, scope, project_path, target_path, sync_mode, synced_at";

pub fn list_links(conn: &Connection) -> Result<Vec<SkillAgentLink>, String> {
    let mut stmt = conn
        .prepare(&format!(
            "SELECT {LINK_COLS} FROM skill_agent_links ORDER BY agent_id, skill_id"
        ))
        .map_err(|e| e.to_string())?;
    let rows = stmt
        .query_map(params![], row_to_link)
        .map_err(|e| e.to_string())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|e| e.to_string())?;
    Ok(rows)
}

pub fn get_link(conn: &Connection, link_id: i64) -> Result<Option<SkillAgentLink>, String> {
    conn.query_row(
        &format!("SELECT {LINK_COLS} FROM skill_agent_links WHERE id = ?1"),
        params![link_id],
        row_to_link,
    )
    .optional()
    .map_err(|e| e.to_string())
}

pub fn links_for_skill(conn: &Connection, skill_id: i64) -> Result<Vec<SkillAgentLink>, String> {
    let mut stmt = conn
        .prepare(&format!(
            "SELECT {LINK_COLS} FROM skill_agent_links WHERE skill_id = ?1"
        ))
        .map_err(|e| e.to_string())?;
    let rows = stmt
        .query_map(params![skill_id], row_to_link)
        .map_err(|e| e.to_string())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|e| e.to_string())?;
    Ok(rows)
}

pub fn insert_link(
    conn: &Connection,
    skill_id: i64,
    agent_id: &str,
    scope: &str,
    project_path: Option<&str>,
    target_path: &str,
    sync_mode: &str,
) -> Result<i64, String> {
    conn.execute(
        "INSERT INTO skill_agent_links
         (skill_id, agent_id, scope, project_path, target_path, sync_mode, synced_at)
         VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7)
         ON CONFLICT(skill_id, agent_id, scope, project_path) DO UPDATE SET
            target_path = excluded.target_path,
            sync_mode   = excluded.sync_mode,
            synced_at   = excluded.synced_at",
        params![skill_id, agent_id, scope, project_path, target_path, sync_mode, now_secs()],
    )
    .map_err(|e| e.to_string())?;
    Ok(conn.last_insert_rowid())
}

pub fn delete_link(conn: &Connection, link_id: i64) -> Result<(), String> {
    conn.execute(
        "DELETE FROM skill_agent_links WHERE id = ?1",
        params![link_id],
    )
    .map_err(|e| e.to_string())?;
    Ok(())
}

// ── Presets ──────────────────────────────────────────────────────────────────

pub fn list_presets(conn: &Connection) -> Result<Vec<SkillPreset>, String> {
    let mut stmt = conn
        .prepare("SELECT id, name FROM skill_presets ORDER BY name")
        .map_err(|e| e.to_string())?;
    let base: Vec<(i64, String)> = stmt
        .query_map(params![], |r| Ok((r.get(0)?, r.get(1)?)))
        .map_err(|e| e.to_string())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|e| e.to_string())?;

    let mut presets = Vec::with_capacity(base.len());
    for (id, name) in base {
        let mut m = conn
            .prepare("SELECT skill_id FROM skill_preset_members WHERE preset_id = ?1")
            .map_err(|e| e.to_string())?;
        let skill_ids: Vec<i64> = m
            .query_map(params![id], |r| r.get(0))
            .map_err(|e| e.to_string())?
            .collect::<Result<Vec<_>, _>>()
            .map_err(|e| e.to_string())?;
        presets.push(SkillPreset { id, name, skill_ids });
    }
    Ok(presets)
}

pub fn create_preset(conn: &Connection, name: &str) -> Result<i64, String> {
    conn.execute(
        "INSERT INTO skill_presets (name) VALUES (?1)",
        params![name],
    )
    .map_err(|e| e.to_string())?;
    Ok(conn.last_insert_rowid())
}

pub fn delete_preset(conn: &Connection, preset_id: i64) -> Result<(), String> {
    conn.execute("DELETE FROM skill_presets WHERE id = ?1", params![preset_id])
        .map_err(|e| e.to_string())?;
    Ok(())
}

pub fn set_preset_member(
    conn: &Connection,
    preset_id: i64,
    skill_id: i64,
    member: bool,
) -> Result<(), String> {
    if member {
        conn.execute(
            "INSERT OR IGNORE INTO skill_preset_members (preset_id, skill_id) VALUES (?1, ?2)",
            params![preset_id, skill_id],
        )
    } else {
        conn.execute(
            "DELETE FROM skill_preset_members WHERE preset_id = ?1 AND skill_id = ?2",
            params![preset_id, skill_id],
        )
    }
    .map_err(|e| e.to_string())?;
    Ok(())
}
