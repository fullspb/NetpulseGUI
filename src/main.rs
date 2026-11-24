#![windows_subsystem = "windows"]
//! NetpulseRS — монитор сетевого трафика (Rust + Slint). Порт NetpulseGUI без .NET.

mod adapters;
mod app;
mod autostart;
mod collector;
mod etw;
mod export;
mod fmt;
mod icons;
mod log;
mod reports;
mod settings;
mod store;
mod time;
mod tray;
mod win;

slint::include_modules!();

use windows_sys::Win32::Foundation::{GetLastError, ERROR_ALREADY_EXISTS};
use windows_sys::Win32::System::Threading::CreateMutexW;

fn main() {
    let args: Vec<String> = std::env::args().collect();
    let has = |a: &str| args.iter().any(|x| x.eq_ignore_ascii_case(a));

    // Один экземпляр: второй запуск показывает окно первого (или завершает его с --shutdown).
    let name = win::wide(r"Local\NetpulseRS.Instance");
    let mutex = unsafe { CreateMutexW(std::ptr::null(), 0, name.as_ptr()) };
    if unsafe { GetLastError() } == ERROR_ALREADY_EXISTS {
        if has("--restarted") {
            // Перезапуск с повышением прав: старый экземпляр ещё завершается — подождём.
            for _ in 0..50 {
                std::thread::sleep(std::time::Duration::from_millis(100));
                if !tray::signal_running_instance(0) {
                    break;
                }
            }
        } else {
            tray::signal_running_instance(if has("--shutdown") { tray::WM_SHUTDOWN } else { tray::WM_SHOW_WINDOW });
            return;
        }
    }
    if has("--shutdown") {
        return;
    }
    let _mutex = mutex;

    log::info(&format!("Запуск NetpulseRS {}, права администратора: {}", env!("CARGO_PKG_VERSION"), win::is_elevated()));
    if let Err(e) = app::run(has("--minimized")) {
        log::warn(&format!("Ошибка UI: {}", e));
    }
    log::info("Выход");
}
