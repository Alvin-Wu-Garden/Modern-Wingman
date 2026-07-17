use rusqlite::Connection;
use std::sync::Mutex;

pub struct AppState {
    pub db: Mutex<Connection>,
    pub http: reqwest::Client,
}

impl AppState {
    pub fn new(conn: Connection) -> Self {
        Self {
            db: Mutex::new(conn),
            http: reqwest::Client::builder()
                .user_agent("modern-wingman/1.0")
                .build()
                .expect("failed to create HTTP client"),
        }
    }
}
