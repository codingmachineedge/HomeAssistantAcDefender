// AC Defender Desktop — Rust bridge.
//
// All HTTP happens here (not in the webview) so CORS never applies and the signed-in
// session lives in one reqwest cookie store. The login flow mirrors the website exactly:
// GET /login for the antiforgery token, then POST the same form the browser posts.

use regex::Regex;
use serde::{Deserialize, Serialize};
use std::path::PathBuf;
use std::sync::Arc;
use tauri::{Manager, State};
use tokio::sync::RwLock;

struct AppState {
    client: reqwest::Client,
    base_url: RwLock<Option<String>>,
    config_path: PathBuf,
}

#[derive(Serialize, Deserialize, Clone, Default)]
struct SavedConfig {
    base_url: String,
    username: String,
    // Stored locally in the app-data folder for convenience on a LAN-only install.
    // Leave "remember password" off in the UI to keep it out of disk entirely.
    password: Option<String>,
}

fn err(message: impl std::fmt::Display) -> String {
    message.to_string()
}

fn normalize_base_url(raw: &str) -> Result<String, String> {
    let trimmed = raw.trim().trim_end_matches('/');
    if trimmed.is_empty() {
        return Err("Enter the defender's address, e.g. http://192.168.50.242:8888".into());
    }
    if trimmed.starts_with("http://") || trimmed.starts_with("https://") {
        Ok(trimmed.to_string())
    } else {
        Ok(format!("http://{trimmed}"))
    }
}

/// Signs in exactly like the browser: fetch /login, lift the antiforgery token out of the
/// form, post the login handler, then prove the session works by loading /api/status.
#[tauri::command]
async fn connect(
    state: State<'_, AppState>,
    base_url: String,
    username: String,
    password: String,
    remember: bool,
) -> Result<serde_json::Value, String> {
    let base = normalize_base_url(&base_url)?;

    let login_page = state
        .client
        .get(format!("{base}/login"))
        .send()
        .await
        .map_err(|e| format!("Cannot reach {base}: {e}"))?
        .text()
        .await
        .map_err(err)?;

    let token = Regex::new(r#"name="__RequestVerificationToken"[^>]*value="([^"]+)""#)
        .map_err(err)?
        .captures(&login_page)
        .and_then(|c| c.get(1))
        .map(|m| m.as_str().to_string())
        .ok_or("The login page did not include the expected sign-in form.")?;

    let response = state
        .client
        .post(format!("{base}/login"))
        .form(&[
            ("_handler", "login"),
            ("__RequestVerificationToken", token.as_str()),
            ("action", "login"),
            ("username", username.trim()),
            ("password", password.as_str()),
            ("keepSignedIn", "true"),
        ])
        .send()
        .await
        .map_err(err)?;
    if !response.status().is_success() && !response.status().is_redirection() {
        return Err(format!("Sign-in failed (HTTP {}).", response.status()));
    }

    // The real proof: the API answers with JSON instead of bouncing to /login.
    let status = fetch_status(&state.client, &base).await?;

    *state.base_url.write().await = Some(base.clone());
    let config = SavedConfig {
        base_url: base,
        username: username.trim().to_string(),
        password: remember.then_some(password),
    };
    if let Ok(json) = serde_json::to_string_pretty(&config) {
        let _ = std::fs::create_dir_all(state.config_path.parent().unwrap_or(&state.config_path));
        let _ = std::fs::write(&state.config_path, json);
    }

    Ok(status)
}

async fn fetch_status(client: &reqwest::Client, base: &str) -> Result<serde_json::Value, String> {
    let response = client
        .get(format!("{base}/api/status"))
        .header("Accept", "application/json")
        .send()
        .await
        .map_err(err)?;
    if response.url().path().contains("login") {
        return Err("Signed out — check the username and password.".into());
    }
    if !response.status().is_success() {
        return Err(format!("Defender API answered HTTP {}.", response.status()));
    }
    response.json::<serde_json::Value>().await.map_err(|e| format!("Bad status JSON: {e}"))
}

async fn require_base(state: &State<'_, AppState>) -> Result<String, String> {
    state
        .base_url
        .read()
        .await
        .clone()
        .ok_or_else(|| "Not connected yet — sign in first.".to_string())
}

#[tauri::command]
async fn get_status(state: State<'_, AppState>) -> Result<serde_json::Value, String> {
    let base = require_base(&state).await?;
    fetch_status(&state.client, &base).await
}

async fn post_json(
    state: &State<'_, AppState>,
    path: &str,
    body: serde_json::Value,
) -> Result<serde_json::Value, String> {
    let base = require_base(state).await?;
    let response = state
        .client
        .post(format!("{base}{path}"))
        .json(&body)
        .send()
        .await
        .map_err(err)?;
    if response.url().path().contains("login") {
        return Err("Session expired — sign in again.".into());
    }
    let status = response.status();
    let text = response.text().await.unwrap_or_default();
    if !status.is_success() {
        return Err(format!("HTTP {status}: {}", text.chars().take(300).collect::<String>()));
    }
    serde_json::from_str(&text).map_err(|e| format!("Bad response JSON: {e}"))
}

#[tauri::command]
async fn set_target(state: State<'_, AppState>, temperature: f64) -> Result<serde_json::Value, String> {
    post_json(&state, "/api/target", serde_json::json!({ "temperatureCelsius": temperature })).await
}

#[tauri::command]
async fn set_defender(state: State<'_, AppState>, enabled: bool) -> Result<serde_json::Value, String> {
    post_json(&state, "/api/defender", serde_json::json!({ "enabled": enabled })).await
}

#[tauri::command]
async fn force_target(state: State<'_, AppState>) -> Result<serde_json::Value, String> {
    post_json(&state, "/api/thermostat/force-target", serde_json::json!({})).await
}

#[tauri::command]
async fn force_boost(state: State<'_, AppState>) -> Result<serde_json::Value, String> {
    post_json(&state, "/api/thermostat/force-boost", serde_json::json!({})).await
}

#[tauri::command]
async fn refresh_thermostat(state: State<'_, AppState>) -> Result<serde_json::Value, String> {
    post_json(&state, "/api/thermostat/refresh", serde_json::json!({})).await
}

#[tauri::command]
async fn thermostat_off(state: State<'_, AppState>) -> Result<serde_json::Value, String> {
    post_json(&state, "/api/thermostat/off", serde_json::json!({})).await
}

#[tauri::command]
async fn set_fan(state: State<'_, AppState>, fan_mode: String) -> Result<serde_json::Value, String> {
    post_json(&state, "/api/thermostat/fan", serde_json::json!({ "fanMode": fan_mode })).await
}

#[tauri::command]
async fn emergency(state: State<'_, AppState>, protocol: String) -> Result<serde_json::Value, String> {
    post_json(&state, "/api/emergency", serde_json::json!({ "protocol": protocol })).await
}

#[tauri::command]
fn load_config(state: State<'_, AppState>) -> SavedConfig {
    std::fs::read_to_string(&state.config_path)
        .ok()
        .and_then(|text| serde_json::from_str(&text).ok())
        .unwrap_or_default()
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_opener::init())
        .setup(|app| {
            let config_path = app
                .path()
                .app_config_dir()
                .unwrap_or_else(|_| PathBuf::from("."))
                .join("connection.json");
            let client = reqwest::Client::builder()
                .cookie_provider(Arc::new(reqwest::cookie::Jar::default()))
                .timeout(std::time::Duration::from_secs(10))
                .build()
                .expect("http client");
            app.manage(AppState {
                client,
                base_url: RwLock::new(None),
                config_path,
            });
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            connect,
            get_status,
            set_target,
            set_defender,
            force_target,
            force_boost,
            refresh_thermostat,
            thermostat_off,
            set_fan,
            emergency,
            load_config
        ])
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
