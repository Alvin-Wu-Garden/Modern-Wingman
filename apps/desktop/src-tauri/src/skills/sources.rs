use rusqlite::{params, Connection};
use serde::Serialize;

/// Metadata about a skill source repository.
///
/// Sources are **data-driven**: stored in the `skill_sources` SQLite table
/// (seeded in `db::seed_skill_sources`). Adding a new source is a DB insert —
/// no Rust code change required.
#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SkillSourceInfo {
    pub id: String,
    pub display_name: String,
    /// GitHub "owner/repo" slug
    pub repo: String,
    /// Path inside the repo where skill sub-directories live (e.g. "skills")
    pub skills_root: String,
    pub is_builtin: bool,
    pub enabled: bool,
}

fn row_to_source(row: &rusqlite::Row<'_>) -> rusqlite::Result<SkillSourceInfo> {
    Ok(SkillSourceInfo {
        id: row.get(0)?,
        display_name: row.get(1)?,
        repo: row.get(2)?,
        skills_root: row.get(3)?,
        is_builtin: row.get::<_, i64>(4)? == 1,
        enabled: row.get::<_, i64>(5)? == 1,
    })
}

/// Returns all enabled skill sources from the database.
pub fn all_sources(conn: &Connection) -> Result<Vec<SkillSourceInfo>, String> {
    let mut stmt = conn
        .prepare(
            "SELECT id, display_name, repo, skills_root, is_builtin, enabled
             FROM skill_sources WHERE enabled = 1
             ORDER BY is_builtin DESC, display_name",
        )
        .map_err(|e| e.to_string())?;
    let rows = stmt
        .query_map(params![], row_to_source)
        .map_err(|e| e.to_string())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|e| e.to_string())?;
    Ok(rows)
}

/// Look up a source by its ID.
pub fn find_source(conn: &Connection, id: &str) -> Result<Option<SkillSourceInfo>, String> {
    let mut stmt = conn
        .prepare(
            "SELECT id, display_name, repo, skills_root, is_builtin, enabled
             FROM skill_sources WHERE id = ?1",
        )
        .map_err(|e| e.to_string())?;
    let mut rows = stmt
        .query_map(params![id], row_to_source)
        .map_err(|e| e.to_string())?;
    match rows.next() {
        Some(Ok(s)) => Ok(Some(s)),
        Some(Err(e)) => Err(e.to_string()),
        None => Ok(None),
    }
}

/// Adds a custom (non-builtin) source. Fails when the ID already exists.
pub fn add_custom_source(
    conn: &Connection,
    id: &str,
    display_name: &str,
    repo: &str,
    skills_root: &str,
) -> Result<(), String> {
    conn.execute(
        "INSERT INTO skill_sources (id, display_name, repo, skills_root, is_builtin, enabled)
         VALUES (?1, ?2, ?3, ?4, 0, 1)",
        params![id, display_name, repo, skills_root],
    )
    .map_err(|e| e.to_string())?;
    Ok(())
}

/// Removes a custom source. Built-in sources cannot be removed (only disabled).
pub fn remove_custom_source(conn: &Connection, id: &str) -> Result<(), String> {
    let affected = conn
        .execute(
            "DELETE FROM skill_sources WHERE id = ?1 AND is_builtin = 0",
            params![id],
        )
        .map_err(|e| e.to_string())?;
    if affected == 0 {
        return Err(format!("Source '{id}' not found or is builtin"));
    }
    Ok(())
}

/// Enables/disables any source.
pub fn set_source_enabled(conn: &Connection, id: &str, enabled: bool) -> Result<(), String> {
    conn.execute(
        "UPDATE skill_sources SET enabled = ?1 WHERE id = ?2",
        params![if enabled { 1 } else { 0 }, id],
    )
    .map_err(|e| e.to_string())?;
    Ok(())
}
