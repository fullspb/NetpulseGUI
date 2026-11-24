//! Время: unix-секунды UTC, локальные календарные дни (номер дня от 1970-01-01) и часовой пояс Windows.

use std::cell::Cell;
use windows_sys::Win32::Foundation::{FILETIME, SYSTEMTIME};
use windows_sys::Win32::System::Time::{SystemTimeToTzSpecificLocalTime, TzSpecificLocalTimeToSystemTime};

const EPOCH_DIFF: i64 = 11_644_473_600; // секунды между 1601-01-01 и 1970-01-01

pub fn now_unix() -> i64 {
    std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map(|d| d.as_secs() as i64).unwrap_or(0)
}

fn to_systemtime(unix: i64) -> SYSTEMTIME {
    let ticks = ((unix + EPOCH_DIFF) as u64) * 10_000_000;
    let ft = FILETIME { dwLowDateTime: ticks as u32, dwHighDateTime: (ticks >> 32) as u32 };
    let mut st: SYSTEMTIME = unsafe { std::mem::zeroed() };
    unsafe { windows_sys::Win32::System::Time::FileTimeToSystemTime(&ft, &mut st) };
    st
}

fn from_systemtime(st: &SYSTEMTIME) -> i64 {
    let mut ft = FILETIME { dwLowDateTime: 0, dwHighDateTime: 0 };
    unsafe { windows_sys::Win32::System::Time::SystemTimeToFileTime(st, &mut ft) };
    let ticks = ((ft.dwHighDateTime as u64) << 32) | ft.dwLowDateTime as u64;
    (ticks / 10_000_000) as i64 - EPOCH_DIFF
}

thread_local! {
    static CACHE: Cell<(i64, i64)> = const { Cell::new((i64::MIN, 0)) };
}

/// Смещение локального времени от UTC (секунды) в момент `unix` (с учётом летнего времени).
pub fn utc_offset(unix: i64) -> i64 {
    let hour = unix.div_euclid(3600);
    if let Some(v) = CACHE.with(|c| {
        let (h, off) = c.get();
        (h == hour).then_some(off)
    }) {
        return v;
    }
    let utc = to_systemtime(unix);
    let mut local: SYSTEMTIME = unsafe { std::mem::zeroed() };
    let off = if unsafe { SystemTimeToTzSpecificLocalTime(std::ptr::null(), &utc, &mut local) } != 0 {
        from_systemtime(&local) - unix
    } else {
        0
    };
    CACHE.with(|c| c.set((hour, off)));
    off
}

pub fn to_local(unix: i64) -> i64 {
    unix + utc_offset(unix)
}

/// Локальное «настенное» время → UTC.
pub fn local_to_utc(local: i64) -> i64 {
    let st = to_systemtime(local);
    let mut utc: SYSTEMTIME = unsafe { std::mem::zeroed() };
    if unsafe { TzSpecificLocalTimeToSystemTime(std::ptr::null(), &st, &mut utc) } != 0 {
        from_systemtime(&utc)
    } else {
        local - utc_offset(local)
    }
}

/// Номер локального дня (дней от 1970-01-01).
pub fn local_day(unix: i64) -> i32 {
    to_local(unix).div_euclid(86_400) as i32
}

pub fn today() -> i32 {
    local_day(now_unix())
}

/// Начало локальных суток в UTC.
pub fn day_start_utc(day: i32) -> i64 {
    local_to_utc(day as i64 * 86_400)
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub struct Date {
    pub y: i32,
    pub m: u32,
    pub d: u32,
}

/// Алгоритм Говарда Хиннанта: номер дня → дата.
pub fn civil_from_days(z: i32) -> Date {
    let z = z as i64 + 719_468;
    let era = z.div_euclid(146_097);
    let doe = z - era * 146_097;
    let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365;
    let y = yoe + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = (doy - (153 * mp + 2) / 5 + 1) as u32;
    let m = if mp < 10 { mp + 3 } else { mp - 9 } as u32;
    Date { y: (if m <= 2 { y + 1 } else { y }) as i32, m, d }
}

pub fn days_from_civil(y: i32, m: u32, d: u32) -> i32 {
    let y = if m <= 2 { y as i64 - 1 } else { y as i64 };
    let era = y.div_euclid(400);
    let yoe = y - era * 400;
    let mp = if m > 2 { m - 3 } else { m + 9 } as i64;
    let doy = (153 * mp + 2) / 5 + d as i64 - 1;
    let doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    (era * 146_097 + doe - 719_468) as i32
}

pub fn days_in_month(y: i32, m: u32) -> u32 {
    match m {
        1 | 3 | 5 | 7 | 8 | 10 | 12 => 31,
        4 | 6 | 9 | 11 => 30,
        _ => {
            if (y % 4 == 0 && y % 100 != 0) || y % 400 == 0 { 29 } else { 28 }
        }
    }
}

/// День недели: 0 = понедельник … 6 = воскресенье.
pub fn weekday(day: i32) -> u32 {
    ((day as i64 + 3).rem_euclid(7)) as u32 // 1970-01-01 — четверг
}

pub fn add_months(y: i32, m: u32, delta: i32) -> (i32, u32) {
    let idx = y * 12 + (m as i32 - 1) + delta;
    (idx.div_euclid(12), (idx.rem_euclid(12) + 1) as u32)
}

/// Расчётный период «как у провайдера»: [start; end) в номерах дней.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Period {
    pub start: i32,
    pub end: i32,
}

impl Period {
    pub fn last_day(&self) -> i32 {
        self.end - 1
    }

    pub fn for_day(day: i32, start_day: u32) -> Period {
        let start_day = start_day.clamp(1, 31);
        let dt = civil_from_days(day);
        let in_month = |y: i32, m: u32| days_from_civil(y, m, start_day.min(days_in_month(y, m)));
        let this_start = in_month(dt.y, dt.m);
        let start = if day >= this_start {
            this_start
        } else {
            let (py, pm) = add_months(dt.y, dt.m, -1);
            in_month(py, pm)
        };
        let s = civil_from_days(start);
        let (ny, nm) = add_months(s.y, s.m, 1);
        Period { start, end: in_month(ny, nm) }
    }

    pub fn previous(&self, start_day: u32) -> Period {
        Period::for_day(self.start - 1, start_day)
    }

    /// Линейный прогноз расхода до конца периода.
    pub fn forecast(&self, used: u64, now: i64) -> u64 {
        let start = day_start_utc(self.start);
        let end = day_start_utc(self.end);
        let elapsed = (now - start) as f64 / 3600.0;
        let total = (end - start) as f64 / 3600.0;
        if elapsed <= 1.0 || total <= 0.0 {
            return used;
        }
        (used as f64 / elapsed * total) as u64
    }
}

pub fn fmt_date(day: i32) -> String {
    let d = civil_from_days(day);
    format!("{:02}.{:02}.{}", d.d, d.m, d.y)
}

pub fn fmt_date_short(day: i32) -> String {
    let d = civil_from_days(day);
    format!("{:02}.{:02}", d.d, d.m)
}

pub fn iso_date(day: i32) -> String {
    let d = civil_from_days(day);
    format!("{}-{:02}-{:02}", d.y, d.m, d.d)
}

pub fn fmt_local_time(unix: i64) -> String {
    let l = to_local(unix);
    let day = l.div_euclid(86_400) as i32;
    let s = l.rem_euclid(86_400);
    format!("{} {:02}:{:02}", fmt_date(day), s / 3600, s / 60 % 60)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn civil_roundtrip() {
        for z in [-1000, 0, 1, 19_000, 20_000, 25_000] {
            let d = civil_from_days(z);
            assert_eq!(days_from_civil(d.y, d.m, d.d), z);
        }
        assert_eq!(civil_from_days(0), Date { y: 1970, m: 1, d: 1 });
        assert_eq!(weekday(days_from_civil(2026, 11, 23)), 0); // понедельник
    }

    #[test]
    fn billing_period() {
        let p = Period::for_day(days_from_civil(2026, 3, 10), 31);
        assert_eq!(p.start, days_from_civil(2026, 2, 28));
        assert_eq!(p.end, days_from_civil(2026, 3, 31));
        let p = Period::for_day(days_from_civil(2026, 11, 24), 1);
        assert_eq!((p.start, p.end), (days_from_civil(2026, 11, 1), days_from_civil(2026, 12, 1)));
    }
}
