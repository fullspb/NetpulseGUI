//! Минимальный журнал: %LOCALAPPDATA%\NetpulseRS\netpulse.log (обрезается при превышении 1 МБ).

use std::io::Write;

fn write(level: &str, msg: &str) {
    let dir = crate::settings::data_dir();
    let _ = std::fs::create_dir_all(&dir);
    let path = dir.join("netpulse.log");
    if std::fs::metadata(&path).map(|m| m.len() > 1 << 20).unwrap_or(false) {
        let _ = std::fs::rename(&path, dir.join("netpulse.old.log"));
    }
    if let Ok(mut f) = std::fs::OpenOptions::new().create(true).append(true).open(&path) {
        let _ = writeln!(f, "{} [{}] {}", crate::time::fmt_local_time(crate::time::now_unix()), level, msg);
    }
}

pub fn info(msg: &str) {
    write("INF", msg)
}

pub fn warn(msg: &str) {
    write("WRN", msg)
}
