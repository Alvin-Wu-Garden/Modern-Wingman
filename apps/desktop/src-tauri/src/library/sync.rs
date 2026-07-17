//! Sync engine: links or copies a library skill into an agent's skills
//! directory. Strategy order on Windows: directory junction → symlink → copy.
//! The mode actually used is recorded so unsync can clean up correctly.

use std::fs;
use std::path::{Path, PathBuf};

/// Resolved sync target for one (skill, agent, scope).
pub struct SyncTarget {
    /// e.g. `~/.claude/skills` or `<project>/.claude/skills`
    pub agent_skills_dir: PathBuf,
    pub skill_name: String,
}

pub struct SyncOutcome {
    pub target_path: PathBuf,
    /// "junction" | "symlink" | "copy"
    pub mode: &'static str,
}

/// Links/copies `library_dir` into the agent skills dir.
/// Tries junction (Windows, no admin required) → symlink → copy.
pub fn sync_skill(library_dir: &Path, target: &SyncTarget) -> Result<SyncOutcome, String> {
    if !library_dir.exists() {
        return Err(format!("中央庫路徑不存在: {}", library_dir.display()));
    }
    fs::create_dir_all(&target.agent_skills_dir).map_err(|e| e.to_string())?;
    let dest = target.agent_skills_dir.join(&target.skill_name);

    // Remove any existing target first (idempotent re-sync).
    remove_target(&dest)?;

    // 1) Directory junction (Windows; works without developer mode/admin)
    #[cfg(windows)]
    {
        if junction::create(library_dir, &dest).is_ok() {
            return Ok(SyncOutcome { target_path: dest, mode: "junction" });
        }
    }

    // 2) Symlink
    #[cfg(windows)]
    let symlink_result = std::os::windows::fs::symlink_dir(library_dir, &dest);
    #[cfg(not(windows))]
    let symlink_result = std::os::unix::fs::symlink(library_dir, &dest);

    if symlink_result.is_ok() {
        return Ok(SyncOutcome { target_path: dest, mode: "symlink" });
    }

    // 3) Copy fallback
    copy_dir_recursive(library_dir, &dest)?;
    Ok(SyncOutcome { target_path: dest, mode: "copy" })
}

/// Removes a previously synced skill from an agent directory.
/// Handles junction/symlink (remove_dir) and copy (remove_dir_all).
pub fn unsync_skill(target_path: &Path) -> Result<(), String> {
    remove_target(target_path)
}

fn remove_target(dest: &Path) -> Result<(), String> {
    if !dest.exists() && fs::symlink_metadata(dest).is_err() {
        return Ok(());
    }
    let meta = fs::symlink_metadata(dest).map_err(|e| e.to_string())?;
    if meta.file_type().is_symlink() {
        // Symlinked dir: on Windows remove_dir; on Unix remove_file.
        #[cfg(windows)]
        let r = fs::remove_dir(dest);
        #[cfg(not(windows))]
        let r = fs::remove_file(dest);
        r.map_err(|e| e.to_string())?;
    } else if meta.is_dir() {
        // Junction reports as dir; remove_dir works for junctions and
        // remove_dir_all for real copies. Try cheap removal first.
        if fs::remove_dir(dest).is_err() {
            fs::remove_dir_all(dest).map_err(|e| e.to_string())?;
        }
    } else {
        fs::remove_file(dest).map_err(|e| e.to_string())?;
    }
    Ok(())
}

fn copy_dir_recursive(src: &Path, dst: &Path) -> Result<(), String> {
    fs::create_dir_all(dst).map_err(|e| e.to_string())?;
    for entry in walkdir::WalkDir::new(src).max_depth(6) {
        let entry = entry.map_err(|e| e.to_string())?;
        let rel = entry.path().strip_prefix(src).map_err(|e| e.to_string())?;
        if rel.as_os_str().is_empty() {
            continue;
        }
        let target = dst.join(rel);
        if entry.file_type().is_dir() {
            fs::create_dir_all(&target).map_err(|e| e.to_string())?;
        } else {
            fs::copy(entry.path(), &target).map_err(|e| e.to_string())?;
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn sync_and_unsync_roundtrip() {
        let tmp = std::env::temp_dir().join(format!("mw-sync-test-{}", std::process::id()));
        let lib = tmp.join("library").join("test-skill");
        let agent_dir = tmp.join("agent-skills");
        fs::create_dir_all(&lib).unwrap();
        fs::write(lib.join("SKILL.md"), "# test").unwrap();

        let target = SyncTarget {
            agent_skills_dir: agent_dir.clone(),
            skill_name: "test-skill".into(),
        };
        let outcome = sync_skill(&lib, &target).expect("sync should succeed");
        assert!(outcome.target_path.join("SKILL.md").exists());

        unsync_skill(&outcome.target_path).expect("unsync should succeed");
        assert!(!outcome.target_path.exists());

        let _ = fs::remove_dir_all(&tmp);
    }

    #[test]
    fn resync_is_idempotent() {
        let tmp = std::env::temp_dir().join(format!("mw-resync-test-{}", std::process::id()));
        let lib = tmp.join("library").join("s1");
        fs::create_dir_all(&lib).unwrap();
        fs::write(lib.join("SKILL.md"), "# v1").unwrap();

        let target = SyncTarget {
            agent_skills_dir: tmp.join("agent"),
            skill_name: "s1".into(),
        };
        sync_skill(&lib, &target).unwrap();
        let second = sync_skill(&lib, &target).expect("re-sync should not fail");
        assert!(second.target_path.join("SKILL.md").exists());

        let _ = fs::remove_dir_all(&tmp);
    }
}
