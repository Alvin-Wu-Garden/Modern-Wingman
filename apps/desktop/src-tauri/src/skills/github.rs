use serde::Deserialize;

const GITHUB_API_BASE: &str = "https://api.github.com";

#[derive(Debug, Deserialize)]
pub struct GithubItem {
    pub name: String,
    #[serde(rename = "type")]
    pub item_type: String,
    pub path: String,
    pub download_url: Option<String>,
}

pub struct GithubClient {
    client: reqwest::Client,
}

impl GithubClient {
    pub fn new(client: reqwest::Client) -> Self {
        Self { client }
    }

    /// List the contents of a directory in a GitHub repository.
    pub async fn list_directory(
        &self,
        repo: &str,
        path: &str,
        pat: Option<&str>,
    ) -> Result<Vec<GithubItem>, String> {
        let url = format!("{}/repos/{}/contents/{}", GITHUB_API_BASE, repo, path);
        let mut req = self
            .client
            .get(&url)
            .header("Accept", "application/vnd.github.v3+json");

        // Only attach token if non-empty to avoid sending "Bearer " which causes 401
        if let Some(token) = pat.filter(|t| !t.is_empty()) {
            req = req.header("Authorization", format!("Bearer {token}"));
        }

        let resp = req.send().await.map_err(|e| e.to_string())?;

        if !resp.status().is_success() {
            let status = resp.status();
            let body = resp.text().await.unwrap_or_default();
            return Err(format!("GitHub API {status}: {body}"));
        }

        resp.json::<Vec<GithubItem>>().await.map_err(|e| e.to_string())
    }

    /// Download the raw content of a single file via its download_url.
    pub async fn get_file_content(
        &self,
        download_url: &str,
        pat: Option<&str>,
    ) -> Result<String, String> {
        let mut req = self.client.get(download_url);

        if let Some(token) = pat.filter(|t| !t.is_empty()) {
            req = req.header("Authorization", format!("Bearer {token}"));
        }

        let resp = req.send().await.map_err(|e| e.to_string())?;

        if !resp.status().is_success() {
            let status = resp.status();
            return Err(format!("Download failed with HTTP {status}"));
        }

        resp.text().await.map_err(|e| e.to_string())
    }
}
