#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]
mod backend;
mod events;
mod instance;
mod options;

use backend::Backend;
use options::Options;
use serde_json::{json, Value};
use std::sync::{
    atomic::{AtomicU64, Ordering},
    Arc, OnceLock,
};
use std::time::Instant;
use tauri::{
    menu::{Menu, MenuItem},
    tray::{MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent},
    Manager, WebviewUrl, WebviewWindowBuilder,
};

// Closing the last dashboard window keeps Mission Control in the tray. Any other exit request
// without an explicit code (macOS Cmd+Q, Dock > Quit, logout or shutdown) is the person quitting,
// so it is allowed; the backend finishes its writes and stops when its pipe closes.
static STARTED: OnceLock<Instant> = OnceLock::new();
static WINDOW_CLOSED_AT: AtomicU64 = AtomicU64::new(0);
const WINDOW_CLOSE_GRACE_MS: u64 = 3000;

fn elapsed_ms() -> u64 {
    STARTED.get_or_init(Instant::now).elapsed().as_millis() as u64 + 1
}

fn note_window_closed() {
    WINDOW_CLOSED_AT.store(elapsed_ms(), Ordering::Release);
}

fn window_just_closed() -> bool {
    let at = WINDOW_CLOSED_AT.load(Ordering::Acquire);
    at != 0 && elapsed_ms().saturating_sub(at) < WINDOW_CLOSE_GRACE_MS
}

fn open_dashboard(app: &tauri::AppHandle) -> Result<(), String> {
    if let Some(window) = app.get_webview_window("dashboard") {
        window.show().map_err(|_| "Could not show dashboard")?;
        let _ = window.unminimize();
        let _ = window.set_focus();
        return Ok(());
    }
    let builder =
        WebviewWindowBuilder::new(app, "dashboard", WebviewUrl::App("index.html".into()))
            .title("Mission Control")
            .inner_size(1380.0, 920.0)
            .min_inner_size(820.0, 600.0)
            .on_navigation(trusted_dashboard)
            .on_new_window(|_, _| tauri::webview::NewWindowResponse::Deny)
            .on_download(|_, _| false);
    // WebView2 keeps its profile in the data folder and exposes automation only on request.
    #[cfg(windows)]
    let builder = {
        let options = app.state::<Options>();
        let mut builder = builder.data_directory(options.data.join("webview"));
        if let Some(port) = options.automation_port {
            builder = builder.additional_browser_args(&format!(
                "--remote-debugging-port={port} --remote-debugging-address=127.0.0.1"
            ));
        }
        builder
    };
    let window = builder
        .build()
        .map_err(|_| "Could not create dashboard WebView")?;
    let visibility_window = window.clone();
    window.on_window_event(move |event| {
        if matches!(
            event,
            tauri::WindowEvent::CloseRequested { .. } | tauri::WindowEvent::Destroyed
        ) {
            note_window_closed();
        }
        if matches!(event, tauri::WindowEvent::Resized(_) | tauri::WindowEvent::Focused(_)) {
            let active = visibility_window.is_visible().unwrap_or(false)
                && !visibility_window.is_minimized().unwrap_or(true);
            let _ = visibility_window.eval(&format!(
                "window.__MC_MINIMIZED__ = {}; window.dispatchEvent(new Event('mc-visibilitychange'));",
                !active
            ));
        }
    });
    let _ = window.set_focus();
    Ok(())
}

fn quit(app: &tauri::AppHandle, reason: &str) {
    let backend = app.state::<Arc<Backend>>().inner().clone();
    if backend.quitting.swap(true, Ordering::AcqRel) {
        return;
    }
    let data = app.state::<Options>().data.clone();
    events::record(&data, &format!("quit: {reason}"));
    for (_, window) in app.webview_windows() {
        let _ = window.destroy();
    }
    let app = app.clone();
    tauri::async_runtime::spawn(async move {
        backend.shutdown().await;
        events::record(&data, "exit: collector stopped, shell exiting");
        app.exit(0);
    });
}

#[tauri::command]
async fn workspace_rpc(
    app: tauri::AppHandle,
    window: tauri::WebviewWindow,
    method: String,
    args: Value,
) -> Result<Value, String> {
    if window.label() != "dashboard"
        || !trusted_dashboard(&window.url().map_err(|_| "Unknown dashboard URL")?)
    {
        return Err("Unknown application window".into());
    }
    if method == "host.version" {
        return Ok(json!({"version": app.package_info().version.to_string()}));
    }
    if method == "host.hide" || method == "control" && args["action"] == "hide" {
        note_window_closed();
        window.destroy().map_err(|_| "Could not close dashboard")?;
        return Ok(json!({"ok":true}));
    }
    if method == "host.quit" || method == "control" && args["action"] == "quit" {
        quit(&app, "dashboard");
        return Ok(json!({"ok":true}));
    }
    // Host actions use a small fixed surface. Agent control and arbitrary commands are absent.
    app.state::<Arc<Backend>>().call(&method, args).await
}

fn trusted_dashboard(url: &tauri::Url) -> bool {
    #[cfg(debug_assertions)]
    if url.scheme() == "http"
        && url.host_str() == Some("127.0.0.1")
        && url.port() == Some(5173)
        && url.username().is_empty()
        && url.password().is_none()
        && matches!(url.path(), "/" | "/index.html")
    {
        return true;
    }
    ((url.scheme() == "tauri" && url.host_str() == Some("localhost"))
        || (matches!(url.scheme(), "http" | "https") && url.host_str() == Some("tauri.localhost")))
        && url.username().is_empty()
        && url.password().is_none()
        && url.port().is_none()
        && matches!(url.path(), "/" | "/index.html")
}

fn tray(app: &tauri::AppHandle) -> Result<(), Box<dyn std::error::Error>> {
    let open = MenuItem::with_id(app, "open", "Open Mission Control", true, None::<&str>)?;
    let pause = MenuItem::with_id(app, "pause", "Pause all", true, None::<&str>)?;
    let resume = MenuItem::with_id(app, "resume", "Resume collection", true, None::<&str>)?;
    let exit = MenuItem::with_id(app, "quit", "Quit completely", true, None::<&str>)?;
    let menu = Menu::with_items(app, &[&open, &pause, &resume, &exit])?;
    // The macOS menu bar expects a monochrome template image that the system tints for light
    // and dark mode; Windows shows the full-colour app icon in the notification area.
    #[cfg(target_os = "macos")]
    let builder = TrayIconBuilder::new()
        .icon(tauri::image::Image::from_bytes(include_bytes!(
            "../icons/tray-template.png"
        ))?)
        .icon_as_template(true);
    #[cfg(not(target_os = "macos"))]
    let builder = TrayIconBuilder::new().icon(tauri::image::Image::from_bytes(include_bytes!(
        "../icons/icon.png"
    ))?);
    builder
        .tooltip("Mission Control")
        .menu(&menu)
        .show_menu_on_left_click(false)
        .on_tray_icon_event(|tray, event| {
            if matches!(
                event,
                TrayIconEvent::Click {
                    button: MouseButton::Left,
                    button_state: MouseButtonState::Up,
                    ..
                }
            ) {
                let _ = open_dashboard(tray.app_handle());
            }
        })
        .on_menu_event(|app, event| match event.id.as_ref() {
            "open" => {
                let _ = open_dashboard(app);
            }
            "quit" => quit(app, "tray menu"),
            action @ ("pause" | "resume") => {
                let backend = app.state::<Arc<Backend>>().inner().clone();
                let method = if action == "pause" {
                    "monitoring.pause"
                } else {
                    "monitoring.resume"
                };
                tauri::async_runtime::spawn(async move {
                    let _ = backend.call(method, json!({})).await;
                });
            }
            _ => {}
        })
        .build(app)?;
    Ok(())
}

fn run() -> Result<(), Box<dyn std::error::Error>> {
    let options = Options::parse()?;
    #[cfg(windows)]
    let Some(mut instance) = instance::Instance::acquire(&options.data)?
    else {
        return Ok(());
    };
    events::record(
        &options.data,
        &format!(
            "started version={} background={}",
            env!("CARGO_PKG_VERSION"),
            options.background
        ),
    );
    let builder = tauri::Builder::default();
    #[cfg(target_os = "macos")]
    let builder = builder.plugin(tauri_plugin_single_instance::init(|app, _, _| {
        let _ = open_dashboard(app);
    }));
    let app = builder
        .manage(options.clone())
        .invoke_handler(tauri::generate_handler![workspace_rpc])
        .setup(move |app| {
            let backend =
                tauri::async_runtime::block_on(Backend::spawn(app.handle().clone(), &options))?;
            app.manage(backend);
            tray(app.handle())?;
            if !options.background {
                open_dashboard(app.handle())?;
            }
            Ok(())
        })
        .build(tauri::generate_context!())?;
    #[cfg(windows)]
    instance.attach(app.handle().clone());
    app.run(|app, event| {
        #[cfg(target_os = "macos")]
        if let tauri::RunEvent::Reopen {
            has_visible_windows: false,
            ..
        } = event
        {
            let _ = open_dashboard(app);
        }
        if let tauri::RunEvent::ExitRequested { api, code, .. } = event {
            let Some(backend) = app.try_state::<Arc<Backend>>() else {
                return;
            };
            let quitting = backend.quitting.load(Ordering::Acquire);
            if code.is_none() {
                // Stay alive after the final WebView closes, and during Quit until collector
                // shutdown completes and explicitly calls app.exit(0).
                if quitting || window_just_closed() {
                    api.prevent_exit();
                    return;
                }
                // Cmd+Q, Dock > Quit, logout or shutdown: let the system exit now rather than
                // cancelling it. The backend sees its pipe close and stops cleanly by itself.
                backend.quitting.store(true, Ordering::Release);
                let closed = WINDOW_CLOSED_AT.load(Ordering::Acquire);
                let since = if closed == 0 {
                    "no window closed".to_owned()
                } else {
                    format!(
                        "last window closed {} ms earlier",
                        elapsed_ms().saturating_sub(closed)
                    )
                };
                events::record(
                    &app.state::<Options>().data,
                    &format!("exit: system request without exit code ({since})"),
                );
                return;
            }
            if !quitting {
                api.prevent_exit();
                quit(
                    app,
                    &format!("exit requested with code {}", code.unwrap_or_default()),
                );
            }
        }
    });
    Ok(())
}

fn main() {
    if let Err(error) = run() {
        // Startup failures contain configuration/toolchain details only, never transcript content.
        eprintln!("Mission Control could not start: {error}");
        #[cfg(windows)]
        unsafe {
            use windows_sys::Win32::UI::WindowsAndMessaging::{MessageBoxW, MB_ICONERROR, MB_OK};
            let text: Vec<u16> = "Mission Control could not start. Check that the complete application and Microsoft Edge WebView2 Runtime are installed. Your saved workspace has been preserved.\0".encode_utf16().collect();
            let title: Vec<u16> = "Mission Control\0".encode_utf16().collect();
            MessageBoxW(
                std::ptr::null_mut(),
                text.as_ptr(),
                title.as_ptr(),
                MB_OK | MB_ICONERROR,
            );
        }
        std::process::exit(1);
    }
}
