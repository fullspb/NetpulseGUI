//! Отчёты за день / неделю / расчётный период: ряды, итоги, сравнение с прошлым периодом, приложения.

use crate::store::{RxTx, Store};
use crate::time::{civil_from_days, fmt_date, fmt_date_short, weekday, Period};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Kind {
    Day,
    Week,
    Month,
}

impl Kind {
    pub fn from_index(i: i32) -> Kind {
        match i {
            0 => Kind::Day,
            1 => Kind::Week,
            _ => Kind::Month,
        }
    }
}

#[derive(Clone, Debug)]
pub struct Bucket {
    pub label: String,
    pub full_label: String,
    pub value: RxTx,
}

#[derive(Clone, Debug)]
#[allow(dead_code)]
pub struct AppItem {
    pub id: u32,
    pub name: String,
    pub path: String,
    pub value: RxTx,
}

#[derive(Clone, Debug)]
pub struct Report {
    pub from: i32,
    pub to: i32,
    pub prev_from: i32,
    pub prev_to: i32,
    pub hourly: bool,
    pub buckets: Vec<Bucket>,
    pub totals: RxTx,
    pub prev_totals: RxTx,
    pub apps: Vec<AppItem>,
}

pub fn range(kind: Kind, anchor: i32, start_day: u32) -> (i32, i32, i32, i32) {
    match kind {
        Kind::Day => (anchor, anchor, anchor - 1, anchor - 1),
        Kind::Week => {
            let monday = anchor - weekday(anchor) as i32;
            (monday, monday + 6, monday - 7, monday - 1)
        }
        Kind::Month => {
            let p = Period::for_day(anchor, start_day);
            let prev = p.previous(start_day);
            (p.start, p.last_day(), prev.start, prev.last_day())
        }
    }
}

/// Сдвиг опорной даты на период вперёд/назад.
pub fn shift(kind: Kind, anchor: i32, dir: i32, start_day: u32) -> i32 {
    match kind {
        Kind::Day => anchor + dir,
        Kind::Week => anchor + 7 * dir,
        Kind::Month => {
            let p = Period::for_day(anchor, start_day);
            if dir < 0 { p.start - 1 } else { p.end }
        }
    }
}

pub fn build(store: &Store, kind: Kind, anchor: i32, start_day: u32) -> Report {
    let (from, to, pf, pt) = range(kind, anchor, start_day);
    let hourly = from == to;
    let buckets: Vec<Bucket> = if hourly {
        store
            .hourly_series(from)
            .iter()
            .enumerate()
            .map(|(h, v)| Bucket { label: format!("{}", h), full_label: format!("{} {:02}:00", fmt_date(from), h), value: *v })
            .collect()
    } else {
        let ru = crate::fmt::russian();
        let names_ru = ["Пн", "Вт", "Ср", "Чт", "Пт", "Сб", "Вс"];
        let names_en = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
        store
            .daily_series(from, to)
            .iter()
            .enumerate()
            .map(|(i, v)| {
                let day = from + i as i32;
                let label = if kind == Kind::Week {
                    let n = if ru { names_ru } else { names_en };
                    format!("{} {}", n[weekday(day) as usize], civil_from_days(day).d)
                } else {
                    fmt_date_short(day)
                };
                Bucket { label, full_label: fmt_date(day), value: *v }
            })
            .collect()
    };
    let apps = store
        .app_totals(from, to)
        .into_iter()
        .filter_map(|(id, v)| store.apps.get(id as usize).map(|m| AppItem { id, name: m.name.clone(), path: m.path.clone(), value: v }))
        .collect();
    Report {
        from,
        to,
        prev_from: pf,
        prev_to: pt,
        hourly,
        buckets,
        totals: store.totals_days(from, to),
        prev_totals: store.totals_days(pf, pt),
        apps,
    }
}
