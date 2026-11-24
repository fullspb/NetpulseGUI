//! Форматирование объёмов и скоростей, язык интерфейса для строк из Rust.

use std::sync::atomic::{AtomicBool, Ordering};

static RUSSIAN: AtomicBool = AtomicBool::new(true);
static BITS: AtomicBool = AtomicBool::new(true);

pub fn set_russian(v: bool) {
    RUSSIAN.store(v, Ordering::Relaxed)
}
pub fn russian() -> bool {
    RUSSIAN.load(Ordering::Relaxed)
}
pub fn set_bits(v: bool) {
    BITS.store(v, Ordering::Relaxed)
}
pub fn bits() -> bool {
    BITS.load(Ordering::Relaxed)
}

/// Строка на текущем языке.
pub fn t<'a>(ru: &'a str, en: &'a str) -> &'a str {
    if russian() { ru } else { en }
}

fn number(v: f64, idx: usize) -> String {
    let s = if idx == 0 {
        format!("{:.0}", v)
    } else if v >= 100.0 {
        format!("{:.0}", v)
    } else if v >= 10.0 {
        format!("{:.1}", v)
    } else {
        format!("{:.2}", v)
    };
    if russian() { s.replace('.', ",") } else { s }
}

fn scale(mut v: f64, k: f64, count: usize) -> (f64, usize) {
    let mut i = 0;
    while v >= k && i < count - 1 {
        v /= k;
        i += 1;
    }
    (v, i)
}

pub fn bytes(b: f64) -> String {
    let units: [&str; 6] = if russian() { ["Б", "КБ", "МБ", "ГБ", "ТБ", "ПБ"] } else { ["B", "KB", "MB", "GB", "TB", "PB"] };
    let (v, i) = scale(b.max(0.0), 1024.0, units.len());
    format!("{} {}", number(v, i), units[i])
}

pub fn speed(bps: f64) -> String {
    let bps = bps.max(0.0);
    if bits() {
        let names: [&str; 5] = if russian() { ["бит/с", "Кбит/с", "Мбит/с", "Гбит/с", "Тбит/с"] } else { ["bps", "Kbps", "Mbps", "Gbps", "Tbps"] };
        let (v, i) = scale(bps * 8.0, 1000.0, names.len());
        format!("{} {}", number(v, i), names[i])
    } else {
        let names: [&str; 5] = if russian() { ["Б/с", "КБ/с", "МБ/с", "ГБ/с", "ТБ/с"] } else { ["B/s", "KB/s", "MB/s", "GB/s", "TB/s"] };
        let (v, i) = scale(bps, 1024.0, names.len());
        format!("{} {}", number(v, i), names[i])
    }
}

/// Сверхкомпактная запись для иконки трея: «12M», «1.5K», «850».
pub fn tray(bps: f64) -> String {
    let (mut v, k) = if bits() { (bps * 8.0, 1000.0) } else { (bps, 1024.0) };
    let suffix = ["", "K", "M", "G", "T"];
    let mut i = 0;
    while v >= 999.5 && i < suffix.len() - 1 {
        v /= k;
        i += 1;
    }
    let mut num = if v < 9.95 && i > 0 { format!("{:.1}", v) } else { format!("{:.0}", v) };
    if num.ends_with(".0") {
        num.truncate(num.len() - 2);
    }
    num + suffix[i]
}

pub fn percent(fraction: f64) -> String {
    let p = fraction * 100.0;
    let s = if p > 0.0 && p < 1.0 { format!("{:.1}%", p) } else { format!("{:.0}%", p) };
    if russian() { s.replace('.', ",") } else { s }
}

/// «+12%» / «−5%» относительно предыдущего значения.
pub fn change(current: u64, previous: u64) -> String {
    if previous == 0 {
        return String::new();
    }
    let d = (current as f64 - previous as f64) / previous as f64 * 100.0;
    if d.abs() < 0.5 {
        return "±0%".into();
    }
    format!("{}{:.0}%", if d > 0.0 { "+" } else { "−" }, d.abs())
}

/// Подписи оси Y: «красивый» максимум для шкалы.
pub fn nice_max(v: f64) -> f64 {
    if v <= 0.0 {
        return 1.0;
    }
    let exp = v.log10().floor();
    let base = 10f64.powf(exp);
    let f = v / base;
    let n = if f <= 1.0 {
        1.0
    } else if f <= 2.0 {
        2.0
    } else if f <= 2.5 {
        2.5
    } else if f <= 5.0 {
        5.0
    } else {
        10.0
    };
    n * base
}

/// «Красивый» максимум шкалы для объёмов в двоичных единицах (КБ, МБ…).
pub fn nice_bytes(v: f64) -> f64 {
    if v <= 0.0 {
        return 1024.0;
    }
    let mut unit = 1.0;
    while v / unit >= 1024.0 {
        unit *= 1024.0;
    }
    let m = nice_max(v / unit);
    if m >= 1000.0 { 1024.0 * unit } else { m * unit }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn formats() {
        set_russian(true);
        assert_eq!(bytes(0.0), "0 Б");
        assert_eq!(bytes(1536.0), "1,50 КБ");
        set_bits(true);
        assert_eq!(speed(125_000.0), "1,00 Мбит/с");
        assert_eq!(tray(1_500_000.0 / 8.0), "1.5M");
        assert_eq!(tray(100.0), "800");
        set_russian(false);
        assert_eq!(bytes(1024.0 * 1024.0 * 15.0), "15.0 MB");
        set_russian(true);
    }
}
