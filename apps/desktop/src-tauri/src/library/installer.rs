//! Installs skills into the central library from GitHub, local folders, or zip
//! archives. File/network acquisition only — persistence is `repository`,
//! syncing to agents is `sync`. (SRP)

use std::fs;
use std::io::Read;
use std::path::{Path, PathBuf};

use crate::skills::github::GithubClient;

use super::library_skills_dir;

/// A skill acquired from any source, staged in the central library directory.
pub struct AcquiredSkill {
    pub name: String,
    pub display_name: String,
    pub description: Option<String>,
    pub library_path: PathBuf,
    pub skill_md_content: String,
    pub content_hash: String,
}

// ── Frontmatter parsing ──────────────────────────────────────────────────────

/// Parses `name` / `description` from SKILL.md YAML frontmatter.
/// Falls back to the directory name when fields are missing.
pub fn parse_frontmatter(content: &str) -> (Option<String>, Option<String>) {
    let mut lines = content.lines();
    if lines.next().map(str::trim) != Some("---") {
        return (None, None);
    }
    let mut name = None;
    let mut description = None;
    for line in lines {
        let trimmed = line.trim();
        if trimmed == "---" {
            break;
        }
        if let Some(v) = trimmed.strip_prefix("name:") {
            name = Some(v.trim().trim_matches(['"', '\'']).to_string());
        } else if let Some(v) = trimmed.strip_prefix("description:") {
            description = Some(v.trim().trim_matches(['"', '\'']).to_string());
        }
    }
    (name, description)
}

fn title_case(s: &str) -> String {
    s.split(['-', '_'])
        .map(|w| {
            let mut c = w.chars();
            match c.next() {
                None => String::new(),
                Some(f) => f.to_uppercase().collect::<String>() + c.as_str(),
            }
        })
        .collect::<Vec<_>>()
        .join(" ")
}

fn content_hash(content: &str) -> String {
    // FNV-1a 64-bit — dependency-free stable content hash for change detection.
    let mut hash: u64 = 0xcbf29ce484222325;
    for b in content.as_bytes() {
        hash ^= *b as u64;
        hash = hash.wrapping_mul(0x100000001b3);
    }
    format!("{hash:016x}")
}

/// Validates a skill name for safe filesystem use.
fn validate_name(name: &str) -> Result<(), String> {
    if name.is_empty() || name.len() > 100 {
        return Err("Skill 名稱長度必須在 1-100 字元".into());
    }
    if !name
        .chars()
        .all(|c| c.is_ascii_alphanumeric() || c == '-' || c == '_' || c == '.')
    {
        return Err(format!("Skill 名稱含有不允許的字元: {name}"));
    }
    if name.starts_with('.') || name.contains("..") {
        return Err("Skill 名稱不可以 . 開頭或包含 ..".into());
    }
    Ok(())
}

fn finalize(
    name: String,
    skill_md_content: String,
    library_path: PathBuf,
) -> AcquiredSkill {
    let (fm_name, fm_desc) = parse_frontmatter(&skill_md_content);
    let hash = content_hash(&skill_md_content);
    AcquiredSkill {
        display_name: fm_name.unwrap_or_else(|| title_case(&name)),
        description: fm_desc,
        name,
        library_path,
        content_hash: hash,
        skill_md_content,
    }
}

// ── GitHub ───────────────────────────────────────────────────────────────────

/// Downloads a skill directory (SKILL.md + first-level files) from GitHub into
/// the central library.
pub async fn install_from_github(
    gh: &GithubClient,
    repo: &str,
    skills_root: &str,
    skill_name: &str,
    pat: Option<&str>,
) -> Result<AcquiredSkill, String> {
    validate_name(skill_name)?;

    let dir_path = format!("{skills_root}/{skill_name}");
    let items = gh.list_directory(repo, &dir_path, pat).await?;

    let skill_file = items
        .iter()
        .find(|i| i.item_type == "file" && i.name == "SKILL.md")
        .ok_or_else(|| format!("SKILL.md not found in {repo}/{dir_path}"))?;
    let url = skill_file
        .download_url
        .as_deref()
        .ok_or("No download_url for SKILL.md")?;
    let content = gh.get_file_content(url, pat).await?;

    let target = library_skills_dir().join(skill_name);
    fs::create_dir_all(&target).map_err(|e| e.to_string())?;
    fs::write(target.join("SKILL.md"), content.as_bytes()).map_err(|e| e.to_string())?;

    // Best-effort: bring along small sibling files (references, scripts).
    for item in items.iter().filter(|i| i.item_type == "file" && i.name != "SKILL.md") {
        if let Some(url) = item.download_url.as_deref() {
            if let Ok(body) = gh.get_file_content(url, pat).await {
                let _ = fs::write(target.join(&item.name), body.as_bytes());
            }
        }
    }

    Ok(finalize(skill_name.to_string(), content, target))
}

pub async fn preview_from_github(
    gh: &GithubClient,
    repo: &str,
    skills_root: &str,
    skill_name: &str,
    pat: Option<&str>,
) -> Result<String, String> {
    validate_name(skill_name)?;
    let dir_path = format!("{skills_root}/{skill_name}");
    let items = gh.list_directory(repo, &dir_path, pat).await?;
    let skill_file = items
        .iter()
        .find(|item| item.item_type == "file" && item.name == "SKILL.md")
        .ok_or_else(|| format!("SKILL.md not found in {repo}/{dir_path}"))?;
    let url = skill_file.download_url.as_deref().ok_or("No download_url for SKILL.md")?;
    gh.get_file_content(url, pat).await
}

// ── Local folder ─────────────────────────────────────────────────────────────

/// Imports a local folder containing SKILL.md into the central library.
pub fn install_from_local(source_dir: &Path) -> Result<AcquiredSkill, String> {
    let skill_md = source_dir.join("SKILL.md");
    if !skill_md.exists() {
        return Err(format!("{} 中找不到 SKILL.md", source_dir.display()));
    }
    let name = source_dir
        .file_name()
        .and_then(|n| n.to_str())
        .ok_or("無效的資料夾名稱")?
        .to_string();
    validate_name(&name)?;

    let content = fs::read_to_string(&skill_md).map_err(|e| e.to_string())?;
    let target = library_skills_dir().join(&name);
    copy_dir_recursive(source_dir, &target)?;

    Ok(finalize(name, content, target))
}

pub fn preview_from_local(source_dir: &Path) -> Result<String, String> {
    let skill_md = source_dir.join("SKILL.md");
    if !skill_md.exists() {
        return Err(format!("{} 中找不到 SKILL.md", source_dir.display()));
    }
    fs::read_to_string(skill_md).map_err(|error| error.to_string())
}

fn copy_dir_recursive(src: &Path, dst: &Path) -> Result<(), String> {
    fs::create_dir_all(dst).map_err(|e| e.to_string())?;
    for entry in walkdir::WalkDir::new(src).max_depth(5) {
        let entry = entry.map_err(|e| e.to_string())?;
        let rel = entry
            .path()
            .strip_prefix(src)
            .map_err(|e| e.to_string())?;
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

// ── Zip / .skill archive ─────────────────────────────────────────────────────

/// Imports a `.zip` / `.skill` archive containing SKILL.md (at root or one
/// level deep) into the central library.
pub fn install_from_zip(zip_path: &Path) -> Result<AcquiredSkill, String> {
    let file = fs::File::open(zip_path).map_err(|e| e.to_string())?;
    let mut archive = zip::ZipArchive::new(file).map_err(|e| e.to_string())?;

    // Locate SKILL.md — at root ("SKILL.md") or inside one folder ("x/SKILL.md")
    let skill_md_index = (0..archive.len())
        .find(|&i| {
            archive
                .by_index(i)
                .map(|f| {
                    let n = f.name().replace('\\', "/");
                    n == "SKILL.md" || (n.ends_with("/SKILL.md") && n.matches('/').count() == 1)
                })
                .unwrap_or(false)
        })
        .ok_or("壓縮檔中找不到 SKILL.md（根目錄或第一層資料夾）")?;

    let (prefix, content) = {
        let mut f = archive.by_index(skill_md_index).map_err(|e| e.to_string())?;
        let name = f.name().replace('\\', "/");
        let prefix = name.strip_suffix("SKILL.md").unwrap_or("").to_string();
        let mut content = String::new();
        f.read_to_string(&mut content).map_err(|e| e.to_string())?;
        (prefix, content)
    };

    // Skill name = folder name inside zip, else the zip file stem.
    let name = if prefix.is_empty() {
        zip_path
            .file_stem()
            .and_then(|s| s.to_str())
            .ok_or("無效的壓縮檔名稱")?
            .trim_end_matches(".skill")
            .to_string()
    } else {
        prefix.trim_end_matches('/').to_string()
    };
    validate_name(&name)?;

    let target = library_skills_dir().join(&name);
    fs::create_dir_all(&target).map_err(|e| e.to_string())?;

    // Extract all entries under the prefix (zip-slip safe).
    for i in 0..archive.len() {
        let mut f = archive.by_index(i).map_err(|e| e.to_string())?;
        let raw = f.name().replace('\\', "/");
        let Some(rel) = raw.strip_prefix(&prefix) else { continue };
        if rel.is_empty() || rel.contains("..") {
            continue;
        }
        let out = target.join(rel);
        if f.is_dir() {
            fs::create_dir_all(&out).map_err(|e| e.to_string())?;
        } else {
            if let Some(parent) = out.parent() {
                fs::create_dir_all(parent).map_err(|e| e.to_string())?;
            }
            let mut buf = Vec::new();
            f.read_to_end(&mut buf).map_err(|e| e.to_string())?;
            fs::write(&out, buf).map_err(|e| e.to_string())?;
        }
    }

    Ok(finalize(name, content, target))
}

pub fn preview_from_zip(zip_path: &Path) -> Result<String, String> {
    let file = fs::File::open(zip_path).map_err(|error| error.to_string())?;
    let mut archive = zip::ZipArchive::new(file).map_err(|error| error.to_string())?;
    let index = (0..archive.len())
        .find(|&index| {
            archive.by_index(index).map(|entry| {
                let name = entry.name().replace('\\', "/");
                name == "SKILL.md" ||
                    (name.ends_with("/SKILL.md") && name.matches('/').count() == 1)
            }).unwrap_or(false)
        })
        .ok_or("壓縮檔中找不到 SKILL.md（根目錄或第一層資料夾）")?;
    let mut entry = archive.by_index(index).map_err(|error| error.to_string())?;
    let mut content = String::new();
    entry.read_to_string(&mut content).map_err(|error| error.to_string())?;
    Ok(content)
}

/// Removes a skill's directory from the central library.
pub fn remove_from_library(library_path: &str) -> Result<(), String> {
    let path = Path::new(library_path);
    // Safety: only delete inside the library root.
    let root = library_skills_dir();
    if !path.starts_with(&root) {
        return Err("拒絕刪除中央庫以外的路徑".into());
    }
    if path.exists() {
        fs::remove_dir_all(path).map_err(|e| e.to_string())?;
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn frontmatter_parses_name_description() {
        let md = "---\nname: my-skill\ndescription: Does things.\n---\n# Body";
        let (n, d) = parse_frontmatter(md);
        assert_eq!(n.as_deref(), Some("my-skill"));
        assert_eq!(d.as_deref(), Some("Does things."));
    }

    #[test]
    fn frontmatter_missing_returns_none() {
        let (n, d) = parse_frontmatter("# Just markdown");
        assert!(n.is_none() && d.is_none());
    }

    #[test]
    fn name_validation_rejects_traversal() {
        assert!(validate_name("../evil").is_err());
        assert!(validate_name(".hidden").is_err());
        assert!(validate_name("good-skill_2").is_ok());
    }

    #[test]
    fn hash_is_stable() {
        assert_eq!(content_hash("abc"), content_hash("abc"));
        assert_ne!(content_hash("abc"), content_hash("abd"));
    }
}
