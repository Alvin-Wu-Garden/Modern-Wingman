/// 啟動 Windows 桌面殼層。
/// 業務資料、Marketplace 與 GraphRAG 全部由單一 .NET Agent Service 負責；
/// Rust 層只保留 Tauri 視窗與原生檔案選擇器，不再維護第二套資料庫或命令。
#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_dialog::init())
        .run(tauri::generate_context!())
        .expect("Modern Wingman 桌面應用程式啟動失敗");
}
