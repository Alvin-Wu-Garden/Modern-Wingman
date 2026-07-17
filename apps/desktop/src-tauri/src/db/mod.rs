use rusqlite::{Connection, Result, params};
use std::path::{Path, PathBuf};

pub fn open_db() -> std::result::Result<Connection, Box<dyn std::error::Error>> {
    let db_path = resolve_db_path()?;
    if let Some(parent) = db_path.parent() {
        std::fs::create_dir_all(parent)?;
    }
    let conn = Connection::open(&db_path)?;
    run_migrations(&conn)?;
    seed_agents(&conn)?;
    seed_skill_sources(&conn)?;
    Ok(conn)
}

fn resolve_db_path() -> std::result::Result<PathBuf, Box<dyn std::error::Error>> {
    if let Ok(path) = std::env::var("WINGMAN_SQLITE_PATH") {
        if !path.trim().is_empty() {
            return Ok(PathBuf::from(path));
        }
    }

    if cfg!(debug_assertions) {
        let manifest_dir = PathBuf::from(env!("CARGO_MANIFEST_DIR"));
        let apps_dir = manifest_dir
            .parent()
            .and_then(Path::parent)
            .ok_or_else(|| std::io::Error::new(
                std::io::ErrorKind::NotFound,
                "failed to resolve apps directory from CARGO_MANIFEST_DIR",
            ))?;
        return Ok(apps_dir.join("wingman_dev.db"));
    }

    let home = dirs::home_dir().ok_or_else(|| std::io::Error::new(
        std::io::ErrorKind::NotFound,
        "failed to resolve user home directory",
    ))?;
    Ok(home.join(".Wingman").join("sqlite").join("wingman.db"))
}

fn run_migrations(conn: &Connection) -> Result<()> {
    conn.execute_batch(
        "PRAGMA journal_mode=WAL;
         PRAGMA foreign_keys=ON;

         CREATE TABLE IF NOT EXISTS skill_cache (
             id          TEXT    PRIMARY KEY,
             source_id   TEXT    NOT NULL,
             skill_name  TEXT    NOT NULL,
             display_name TEXT,
             description TEXT,
             cached_at   INTEGER NOT NULL
         );

         CREATE TABLE IF NOT EXISTS agents (
             id                     TEXT PRIMARY KEY,
             display_name           TEXT NOT NULL,
             global_skills_path     TEXT NOT NULL,
             project_skills_subpath TEXT NOT NULL,
             is_builtin             INTEGER NOT NULL DEFAULT 1,
             icon                   TEXT,
             custom_global_path     TEXT,
             mcp_config_path        TEXT,
             detect_path            TEXT
         );

         CREATE TABLE IF NOT EXISTS installed_skills (
             id             INTEGER PRIMARY KEY AUTOINCREMENT,
             source_id      TEXT    NOT NULL,
             skill_name     TEXT    NOT NULL,
             agent_id       TEXT    NOT NULL,
             scope          TEXT    NOT NULL CHECK (scope IN ('global','project')),
             project_path   TEXT,
             installed_path TEXT    NOT NULL,
             installed_at   INTEGER NOT NULL
         );

         -- README content cache (7-day TTL) — prevents repeated GitHub API calls
         CREATE TABLE IF NOT EXISTS readme_cache (
             skill_id    TEXT    PRIMARY KEY,
             content     TEXT    NOT NULL,
             cached_at   INTEGER NOT NULL
         );

         -- ── WS1: data-driven skill sources (replaces hardcoded Rust trait impls) ──
         CREATE TABLE IF NOT EXISTS skill_sources (
             id           TEXT    PRIMARY KEY,
             display_name TEXT    NOT NULL,
             repo         TEXT    NOT NULL,
             skills_root  TEXT    NOT NULL,
             is_builtin   INTEGER NOT NULL DEFAULT 0,
             enabled      INTEGER NOT NULL DEFAULT 1
         );

         -- ── WS1: central skill library (~/.wingman/library/skills/<name>) ──
         CREATE TABLE IF NOT EXISTS library_skills (
             id            INTEGER PRIMARY KEY AUTOINCREMENT,
             name          TEXT    NOT NULL UNIQUE,
             display_name  TEXT    NOT NULL,
             description   TEXT,
             source_kind   TEXT    NOT NULL CHECK (source_kind IN ('github','local','zip')),
             source_ref    TEXT    NOT NULL,
             library_path  TEXT    NOT NULL,
             content_hash  TEXT    NOT NULL,
             risk_level    TEXT    NOT NULL DEFAULT 'low' CHECK (risk_level IN ('low','medium','high')),
             risk_notes    TEXT,
             tags          TEXT    NOT NULL DEFAULT '',
             installed_at  INTEGER NOT NULL,
             updated_at    INTEGER NOT NULL
         );

         -- ── WS1: which library skill is synced to which agent ──
         CREATE TABLE IF NOT EXISTS skill_agent_links (
             id            INTEGER PRIMARY KEY AUTOINCREMENT,
             skill_id      INTEGER NOT NULL REFERENCES library_skills(id) ON DELETE CASCADE,
             agent_id      TEXT    NOT NULL REFERENCES agents(id),
             scope         TEXT    NOT NULL CHECK (scope IN ('global','project')),
             project_path  TEXT,
             target_path   TEXT    NOT NULL,
             sync_mode     TEXT    NOT NULL CHECK (sync_mode IN ('junction','symlink','copy')),
             synced_at     INTEGER NOT NULL,
             UNIQUE (skill_id, agent_id, scope, project_path)
         );

         -- ── WS1: skill presets (named groups applied in one click) ──
         CREATE TABLE IF NOT EXISTS skill_presets (
             id    INTEGER PRIMARY KEY AUTOINCREMENT,
             name  TEXT NOT NULL UNIQUE
         );

         CREATE TABLE IF NOT EXISTS skill_preset_members (
             preset_id INTEGER NOT NULL REFERENCES skill_presets(id) ON DELETE CASCADE,
             skill_id  INTEGER NOT NULL REFERENCES library_skills(id) ON DELETE CASCADE,
             PRIMARY KEY (preset_id, skill_id)
         );

         -- ── WS1: MCP server registry ──
         CREATE TABLE IF NOT EXISTS mcp_servers (
             id         INTEGER PRIMARY KEY AUTOINCREMENT,
             name       TEXT    NOT NULL UNIQUE,
             transport  TEXT    NOT NULL CHECK (transport IN ('stdio','sse','http')),
             command    TEXT,
             args       TEXT    NOT NULL DEFAULT '[]',
             url        TEXT,
             env        TEXT    NOT NULL DEFAULT '{}',
             enabled    INTEGER NOT NULL DEFAULT 1,
             created_at INTEGER NOT NULL,
             updated_at INTEGER NOT NULL
         );

         -- ── WS1: which MCP server is synced to which agent config ──
         CREATE TABLE IF NOT EXISTS mcp_agent_links (
             id         INTEGER PRIMARY KEY AUTOINCREMENT,
             server_id  INTEGER NOT NULL REFERENCES mcp_servers(id) ON DELETE CASCADE,
             agent_id   TEXT    NOT NULL REFERENCES agents(id),
             synced_at  INTEGER NOT NULL,
             UNIQUE (server_id, agent_id)
         );",
    )?;

    // Column upgrades for pre-existing installs (ignore duplicate-column errors)
    let _ = conn.execute("ALTER TABLE agents ADD COLUMN mcp_config_path TEXT", params![]);
    let _ = conn.execute("ALTER TABLE agents ADD COLUMN detect_path TEXT", params![]);
    Ok(())
}

/// Declarative agent adapter definitions — adding a new tool is data, not code.
/// (id, display_name, global_skills_subpath, project_skills_subpath, mcp_config_subpath, detect_subpath)
/// Subpaths are relative to the user home directory.
const BUILTIN_AGENTS: &[(&str, &str, &str, &str, Option<&str>, Option<&str>)] = &[
    ("wingman",      "Modern Wingman",  ".wingman/agents/wingman/skills", ".wingman/skills",     Some(".wingman/agents/wingman/mcp.json"),  None),
    ("claude-code",  "Claude Code",     ".claude/skills",                 ".claude/skills",      Some(".claude.json"),                      Some(".claude")),
    ("codex",        "Codex CLI",       ".codex/skills",                  ".codex/skills",       Some(".codex/config.toml"),                Some(".codex")),
    ("copilot",      "GitHub Copilot",  ".copilot/skills",                ".github/skills",      Some(".copilot/mcp.json"),                 Some(".copilot")),
    ("cursor",       "Cursor",          ".cursor/skills",                 ".cursor/skills",      Some(".cursor/mcp.json"),                  Some(".cursor")),
    ("windsurf",     "Windsurf",        ".windsurf/skills",               ".windsurf/skills",    Some(".codeium/windsurf/mcp_config.json"), Some(".codeium/windsurf")),
    ("cline",        "Cline",           ".cline/skills",                  ".cline/skills",       None,                                      Some(".cline")),
    ("roo-code",     "Roo Code",        ".roo/skills",                    ".roo/skills",         None,                                      Some(".roo")),
    ("kilo-code",    "Kilo Code",       ".kilocode/skills",               ".kilocode/skills",    None,                                      Some(".kilocode")),
    ("goose",        "Goose",           ".config/goose/skills",           ".goose/skills",       None,                                      Some(".config/goose")),
    ("gemini-cli",   "Gemini CLI",      ".gemini/skills",                 ".gemini/skills",      Some(".gemini/settings.json"),             Some(".gemini")),
    ("amp",          "Amp",             ".amp/skills",                    ".amp/skills",         None,                                      Some(".amp")),
    ("opencode",     "OpenCode",        ".config/opencode/skills",        ".opencode/skills",    Some(".config/opencode/opencode.json"),    Some(".config/opencode")),
    ("trae",         "TRAE IDE",        ".trae/skills",                   ".trae/skills",        None,                                      Some(".trae")),
    ("antigravity",  "Antigravity",     ".antigravity/skills",            ".antigravity/skills", None,                                      Some(".antigravity")),
    ("grok",         "Grok",            ".grok/skills",                   ".grok/skills",        None,                                      Some(".grok")),
];

fn seed_agents(conn: &Connection) -> Result<()> {
    let home = dirs::home_dir().unwrap_or_default();
    let norm = |sub: &str| home.join(sub).to_string_lossy().replace('\\', "/");

    for (id, name, global_sub, project_sub, mcp_sub, detect_sub) in BUILTIN_AGENTS {
        let global_path = norm(global_sub);
        let mcp_path = mcp_sub.map(norm);
        let detect_path = detect_sub.map(norm);
        conn.execute(
            "INSERT INTO agents
             (id, display_name, global_skills_path, project_skills_subpath, is_builtin, icon, custom_global_path, mcp_config_path, detect_path)
             VALUES (?1, ?2, ?3, ?4, 1, NULL, NULL, ?5, ?6)
             ON CONFLICT(id) DO UPDATE SET
                display_name = excluded.display_name,
                mcp_config_path = COALESCE(excluded.mcp_config_path, agents.mcp_config_path),
                detect_path = COALESCE(excluded.detect_path, agents.detect_path)",
            params![id, name, global_path, project_sub, mcp_path, detect_path],
        )?;
    }
    Ok(())
}

/// Built-in marketplace sources. Users can add rows at runtime — no code change needed.
const BUILTIN_SOURCES: &[(&str, &str, &str, &str)] = &[
    ("vercel-labs",     "Vercel Labs",     "vercel-labs/agent-skills", "skills"),
    ("anthropics",      "Anthropic",       "anthropics/skills",        "skills"),
    ("remotion",        "Remotion",        "remotion-dev/skills",      "skills"),
    ("microsoft-azure", "Microsoft Azure", "microsoft/azure-skills",   "skills"),
    ("superpowers",     "Superpowers",     "obra/superpowers",         "skills"),
];

fn seed_skill_sources(conn: &Connection) -> Result<()> {
    for (id, name, repo, root) in BUILTIN_SOURCES {
        conn.execute(
            "INSERT OR IGNORE INTO skill_sources (id, display_name, repo, skills_root, is_builtin, enabled)
             VALUES (?1, ?2, ?3, ?4, 1, 1)",
            params![id, name, repo, root],
        )?;
    }
    Ok(())
}
