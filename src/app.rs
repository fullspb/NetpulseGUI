//! Связка интерфейса Slint с фоновым сбором, хранилищем и треем.

use crate::collector::{self, Shared, Snapshot};
use crate::fmt::{self, t};
use crate::reports::{self, Report};
use crate::settings::{data_dir, Settings};
use crate::store::{app_key, RxTx, Store};
use crate::time::{self, Period};
use crate::tray::{Tray, TrayEvent};
use crate::{AdapterRow, AlertRow, AppState, AppWindow, Bar, ProcRow, Slice, Theme};
use slint::{ComponentHandle, ModelRc, SharedString, VecModel};
use std::cell::RefCell;
use std::collections::HashMap;
use std::rc::Rc;
use std::sync::atomic::Ordering;
use std::sync::Arc;
use std::time::Duration;

const GITHUB: &str = "https://github.com/fullspb/NetpulseGUI";
const PALETTE: [(u8, u8, u8); 8] = [(0, 120, 212), (224, 112, 0), (46, 160, 67), (142, 68, 173), (209, 52, 56), (0, 162, 173), (193, 156, 0), (122, 117, 116)];
const GB: f64 = 1024.0 * 1024.0 * 1024.0;

/// Строка таблицы до форматирования (для сортировки по числам).
struct Row {
    name: String,
    path: String,
    v: [f64; 3],
    share: f64,
}

struct App {
    ui: slint::Weak<AppWindow>,
    shared: Arc<Shared>,
    settings: Settings,
    tray: Tray,
    visible: bool,
    prev_page: i32,
    ticks: u64,
    top_sort: (i32, bool),
    procs_sort: (i32, bool),
    rep_sort: (i32, bool),
    rep_kind: reports::Kind,
    rep_anchor: i32,
    report: Option<Report>,
    det: Option<(String, String)>,
    det_day: i32,
    today: RxTx,
    period: RxTx,
    all: RxTx,
    first_day: Option<i32>,
    top_model: Rc<VecModel<ProcRow>>,
    procs_model: Rc<VecModel<ProcRow>>,
}

thread_local! {
    static APP: RefCell<Option<Rc<RefCell<App>>>> = const { RefCell::new(None) };
    static CHART: RefCell<(Vec<(f32, f32)>, usize)> = const { RefCell::new((Vec::new(), 1)) };
}

/// Выполнить действие над состоянием приложения (пропускается при повторном входе).
fn with_app(f: impl FnOnce(&mut App)) {
    let app = APP.with(|a| a.borrow().clone());
    if let Some(app) = app {
        if let Ok(mut a) = app.try_borrow_mut() {
            f(&mut a);
        }
    }
}

/// Отложить действие до следующей итерации цикла событий.
fn later(f: impl FnOnce(&mut App) + 'static) {
    slint::Timer::single_shot(Duration::ZERO, move || with_app(f));
}

fn ss(s: impl AsRef<str>) -> SharedString {
    SharedString::from(s.as_ref())
}

fn strings(v: Vec<String>) -> ModelRc<SharedString> {
    ModelRc::new(VecModel::from(v.into_iter().map(SharedString::from).collect::<Vec<_>>()))
}

fn sort_rows(rows: &mut [Row], col: i32, asc: bool) {
    rows.sort_by(|a, b| {
        let o = if col <= 0 {
            a.name.to_lowercase().cmp(&b.name.to_lowercase())
        } else {
            let i = (col - 1).min(2) as usize;
            a.v[i].total_cmp(&b.v[i]).then_with(|| b.name.to_lowercase().cmp(&a.name.to_lowercase()))
        };
        if asc { o } else { o.reverse() }
    });
}

fn next_sort(cur: (i32, bool), col: i32) -> (i32, bool) {
    if cur.0 == col { (col, !cur.1) } else { (col, col == 0) }
}

fn proc_row(r: &Row, f: [&dyn Fn(f64) -> String; 3], pct: bool) -> ProcRow {
    ProcRow {
        key: ss(app_key(&r.name, &r.path)),
        name: ss(&r.name),
        path: ss(&r.path),
        icon: crate::icons::for_path(&r.path),
        c1: ss(f[0](r.v[0])),
        c2: ss(f[1](r.v[1])),
        c3: ss(f[2](r.v[2])),
        pct: if pct { ss(fmt::percent(r.share)) } else { ss("") },
        share: r.share as f32,
    }
}

fn speed_or_dash(v: f64) -> String {
    if v > 0.0 { fmt::speed(v) } else { "—".into() }
}

fn bytes_or_dash(v: f64) -> String {
    if v > 0.0 { fmt::bytes(v) } else { "—".into() }
}

/// Столбцы и подписи шкалы для ряда объёмов.
fn bars(values: &[(String, String, RxTx)]) -> (Vec<Bar>, Vec<String>) {
    let max = values.iter().map(|v| v.2.rx.max(v.2.tx)).max().unwrap_or(0) as f64;
    let nice = fmt::nice_bytes(max.max(1024.0));
    let n = values.len();
    let step = if n > 16 { n.div_ceil(12) } else { 1 };
    let bars = values
        .iter()
        .enumerate()
        .map(|(i, (label, full, v))| Bar {
            down: (v.rx as f64 / nice) as f32,
            up: (v.tx as f64 / nice) as f32,
            label: if i % step == 0 { ss(label) } else { ss("") },
            tip: ss(format!("{}\n↓ {}\n↑ {}\n{} {}", full, fmt::bytes(v.rx as f64), fmt::bytes(v.tx as f64), t("Всего", "Total"), fmt::bytes(v.total() as f64))),
        })
        .collect();
    let labels = (0..=4).map(|i| fmt::bytes(nice * (4 - i) as f64 / 4.0)).collect();
    (bars, labels)
}

fn arc_path(a0: f64, a1: f64) -> String {
    let (cx, cy, r, ri) = (100.0f64, 100.0f64, 96.0f64, 62.0f64);
    let p = |ang: f64, rad: f64| (cx + rad * ang.sin(), cy - rad * ang.cos());
    let large = if a1 - a0 > std::f64::consts::PI { 1 } else { 0 };
    let (x0, y0) = p(a0, r);
    let (x1, y1) = p(a1, r);
    let (x2, y2) = p(a1, ri);
    let (x3, y3) = p(a0, ri);
    format!("M {x0:.2} {y0:.2} A {r} {r} 0 {large} 1 {x1:.2} {y1:.2} L {x2:.2} {y2:.2} A {ri} {ri} 0 {large} 0 {x3:.2} {y3:.2} Z")
}

fn slice_path(a0: f64, a1: f64) -> String {
    if a1 - a0 > std::f64::consts::TAU - 0.01 {
        let mid = a0 + std::f64::consts::PI;
        format!("{} {}", arc_path(a0, mid), arc_path(mid, a0 + std::f64::consts::TAU - 0.0001))
    } else {
        arc_path(a0, a1)
    }
}

fn hwnd_of(ui: &AppWindow) -> Option<windows_sys::Win32::Foundation::HWND> {
    use raw_window_handle::{HasWindowHandle, RawWindowHandle};
    let wh = ui.window().window_handle();
    let h = wh.window_handle().ok()?;
    match h.as_raw() {
        RawWindowHandle::Win32(w) => Some(w.hwnd.get() as windows_sys::Win32::Foundation::HWND),
        _ => None,
    }
}

impl App {
    fn ui(&self) -> Option<AppWindow> {
        self.ui.upgrade()
    }

    // ---------- общий тик ----------

    fn tick(&mut self) {
        let snap = self.shared.last.lock().unwrap().clone();
        self.ticks += 1;
        if std::env::var_os("NETPULSE_DEBUG").is_some() {
            crate::log::info(&format!("ui tick {} visible={} down={}", self.ticks, self.visible, snap.down));
        }
        self.refresh_totals();
        if self.ticks % 5 == 1 {
            self.check_limits();
        }
        self.update_tray(&snap);
        if !self.visible {
            return;
        }
        let Some(ui) = self.ui() else { return };
        let st = ui.global::<AppState>();
        st.set_paused(snap.paused);
        st.set_etw_active(snap.etw_active);
        st.set_etw_status(ss(self.etw_status(&snap)));
        match st.get_page() {
            0 => self.update_dashboard(&ui, &snap),
            1 => self.update_procs(&ui, &snap),
            3 => self.update_limits(&ui),
            5 if self.ticks % 2 == 0 => self.update_details(&ui, &snap),
            _ => {}
        }
    }

    fn etw_status(&self, snap: &Snapshot) -> String {
        if !crate::win::is_elevated() {
            t("Запустите NetpulseRS от имени администратора, чтобы видеть трафик отдельных приложений", "Run NetpulseRS as administrator to see per-application traffic").into()
        } else if !self.settings.process_tracking {
            t("Выключено", "Disabled").into()
        } else if snap.etw_active {
            t("Работает: события ядра Windows (ETW)", "Active: Windows kernel events (ETW)").into()
        } else {
            snap.etw_error.clone().unwrap_or_else(|| t("Запускается…", "Starting…").into())
        }
    }

    fn refresh_totals(&mut self) {
        let today = time::today();
        let p = Period::for_day(today, self.settings.billing_start_day);
        let store = self.shared.store.lock().unwrap();
        self.today = store.totals_days(today, today);
        self.period = store.totals_days(p.start, p.last_day());
        self.all = store.all_time();
        self.first_day = store.first_day();
    }

    fn update_tray(&mut self, snap: &Snapshot) {
        let tip = format!(
            "NetpulseRS\n↓ {}   ↑ {}\n{}: {}",
            fmt::speed(snap.down),
            fmt::speed(snap.up),
            t("Сегодня", "Today"),
            fmt::bytes(self.today.total() as f64)
        );
        self.tray.update(&fmt::tray(snap.down), &fmt::tray(snap.up), snap.paused, &tip);
    }

    fn update_menu(&self) {
        let paused = self.shared.paused.load(Ordering::Relaxed);
        self.tray.set_menu_text(t("Открыть NetpulseRS", "Open NetpulseRS"), t("Пауза", "Pause"), t("Выход", "Exit"), paused);
    }

    // ---------- главная ----------

    fn update_dashboard(&mut self, ui: &AppWindow, snap: &Snapshot) {
        let st = ui.global::<AppState>();
        st.set_down_speed(ss(fmt::speed(snap.down)));
        st.set_up_speed(ss(fmt::speed(snap.up)));
        let (pd, pu) = self.build_chart(ui);
        st.set_peak_down(ss(format!("{} {}", t("пик", "peak"), fmt::speed(pd))));
        st.set_peak_up(ss(format!("{} {}", t("пик", "peak"), fmt::speed(pu))));
        st.set_today_total(ss(fmt::bytes(self.today.total() as f64)));
        st.set_today_details(ss(format!("↓ {}   ↑ {}", fmt::bytes(self.today.rx as f64), fmt::bytes(self.today.tx as f64))));
        let p = Period::for_day(time::today(), self.settings.billing_start_day);
        st.set_period_total(ss(fmt::bytes(self.period.total() as f64)));
        st.set_period_details(ss(format!("{} — {}", time::fmt_date(p.start), time::fmt_date(p.last_day()))));
        st.set_all_total(ss(fmt::bytes(self.all.total() as f64)));
        st.set_all_details(ss(match self.first_day {
            Some(d) => format!("{} {}", t("с", "since"), time::fmt_date(d)),
            None => String::new(),
        }));

        let mut rows: Vec<Row> = snap.procs.iter().map(|p| Row { name: p.name.clone(), path: p.path.clone(), v: [p.down, p.up, p.down + p.up], share: 0.0 }).collect();
        sort_rows(&mut rows, self.top_sort.0, self.top_sort.1);
        rows.truncate(15);
        let f: [&dyn Fn(f64) -> String; 3] = [&speed_or_dash, &speed_or_dash, &speed_or_dash];
        self.top_model.set_vec(rows.iter().map(|r| proc_row(r, f, false)).collect::<Vec<_>>());
        st.set_top_sort_col(self.top_sort.0);
        st.set_top_sort_asc(self.top_sort.1);
    }

    /// Строит пути графика скорости; возвращает пики загрузки и отдачи за выбранный интервал.
    fn build_chart(&self, ui: &AppWindow) -> (f64, f64) {
        let st = ui.global::<AppState>();
        let range = match st.get_range_index() {
            1 => 300,
            2 => 3600,
            _ => 60,
        };
        let bucket = if range == 3600 { 10 } else { 1 };
        let n = range / bucket;
        let mut pts = vec![(0f32, 0f32); n];
        let (mut pd, mut pu) = (0f32, 0f32);
        {
            let hist = self.shared.history.lock().unwrap();
            let len = hist.len();
            let take = len.min(range);
            for (k, v) in hist.iter().skip(len - take).enumerate() {
                let i = (range - take + k) / bucket;
                pts[i].0 += v.0 / bucket as f32;
                pts[i].1 += v.1 / bucket as f32;
                pd = pd.max(v.0);
                pu = pu.max(v.1);
            }
        }
        let max = pts.iter().map(|p| p.0.max(p.1)).fold(0.0f32, f32::max) as f64;
        let nice = if fmt::bits() { fmt::nice_max((max * 8.0).max(8000.0)) / 8.0 } else { fmt::nice_bytes(max.max(1024.0)) };
        let path = |sel: fn(&(f32, f32)) -> f32| {
            let mut s = String::with_capacity(n * 16);
            for (i, p) in pts.iter().enumerate() {
                let x = i as f64 / (n - 1).max(1) as f64 * 1000.0;
                let y = 1000.0 - (sel(p) as f64 / nice * 1000.0).clamp(0.0, 1000.0);
                s += &format!("{}{:.1} {:.1} ", if i == 0 { "M " } else { "L " }, x, y);
            }
            let area = format!("{}L 1000 1000 L 0 1000 Z", s);
            (s, area)
        };
        let (dl, da) = path(|p| p.0);
        let (ul, ua) = path(|p| p.1);
        st.set_chart_down_line(ss(dl));
        st.set_chart_down_area(ss(da));
        st.set_chart_up_line(ss(ul));
        st.set_chart_up_area(ss(ua));
        st.set_chart_points(n as i32);
        st.set_chart_ylabels(strings((0..=4).map(|i| fmt::speed(nice * (4 - i) as f64 / 4.0)).collect()));
        st.set_chart_xleft(ss(match range {
            60 => t("1 минуту назад", "1 minute ago"),
            300 => t("5 минут назад", "5 minutes ago"),
            _ => t("1 час назад", "1 hour ago"),
        }));
        st.set_chart_xright(ss(t("сейчас", "now")));
        CHART.with(|c| *c.borrow_mut() = (pts, bucket));
        (pd as f64, pu as f64)
    }

    // ---------- процессы ----------

    fn update_procs(&mut self, ui: &AppWindow, snap: &Snapshot) {
        let st = ui.global::<AppState>();
        let filter = st.get_procs_filter().to_lowercase();
        let today = time::today();
        let mut map: HashMap<String, Row> = HashMap::new();
        {
            let store = self.shared.store.lock().unwrap();
            for (id, v) in store.app_totals(today, today) {
                if let Some(m) = store.apps.get(id as usize) {
                    map.insert(app_key(&m.name, &m.path), Row { name: m.name.clone(), path: m.path.clone(), v: [0.0, 0.0, v.total() as f64], share: 0.0 });
                }
            }
        }
        for p in &snap.procs {
            let e = map.entry(app_key(&p.name, &p.path)).or_insert_with(|| Row { name: p.name.clone(), path: p.path.clone(), v: [0.0; 3], share: 0.0 });
            e.v[0] = p.down;
            e.v[1] = p.up;
        }
        let total: f64 = map.values().map(|r| r.v[2]).sum::<f64>().max(1.0);
        let mut rows: Vec<Row> = map
            .into_values()
            .filter(|r| filter.is_empty() || r.name.to_lowercase().contains(&filter) || r.path.to_lowercase().contains(&filter))
            .map(|mut r| {
                r.share = r.v[2] / total;
                r
            })
            .collect();
        sort_rows(&mut rows, self.procs_sort.0, self.procs_sort.1);
        rows.truncate(300);
        let f: [&dyn Fn(f64) -> String; 3] = [&speed_or_dash, &speed_or_dash, &bytes_or_dash];
        self.procs_model.set_vec(rows.iter().map(|r| proc_row(r, f, true)).collect::<Vec<_>>());
        st.set_procs_sort_col(self.procs_sort.0);
        st.set_procs_sort_asc(self.procs_sort.1);
    }

    // ---------- карточка процесса ----------

    fn open_process(&mut self, name: String, path: String) {
        let Some(ui) = self.ui() else { return };
        let st = ui.global::<AppState>();
        let page = st.get_page();
        if page != 5 {
            self.prev_page = page;
        }
        self.det = Some((name, path));
        self.det_day = time::today();
        st.set_page(5);
        let snap = self.shared.last.lock().unwrap().clone();
        self.update_details(&ui, &snap);
    }

    fn update_details(&mut self, ui: &AppWindow, snap: &Snapshot) {
        let Some((name, path)) = self.det.clone() else { return };
        let st = ui.global::<AppState>();
        st.set_det_name(ss(&name));
        st.set_det_path(ss(if path.is_empty() { t("Путь неизвестен", "Path unknown") } else { &path }));
        st.set_det_icon(crate::icons::for_path(&path));
        let key = app_key(&name, &path);
        let live = snap.procs.iter().find(|p| app_key(&p.name, &p.path) == key);
        st.set_det_live(ss(match live {
            Some(p) => format!("↓ {}  ↑ {}", fmt::speed(p.down), fmt::speed(p.up)),
            None => "—".into(),
        }));
        let today = time::today();
        let p = Period::for_day(today, self.settings.billing_start_day);
        let store = self.shared.store.lock().unwrap();
        let id = store.find_app(&name, &path);
        let total = |from: i32, to: i32| id.map(|i| store.app_total(i, from, to)).unwrap_or_default();
        let (td, pd) = (total(today, today), total(p.start, p.last_day()));
        st.set_det_today(ss(fmt::bytes(td.total() as f64)));
        st.set_det_today_details(ss(format!("↓ {}   ↑ {}", fmt::bytes(td.rx as f64), fmt::bytes(td.tx as f64))));
        st.set_det_period(ss(fmt::bytes(pd.total() as f64)));
        st.set_det_period_details(ss(format!("↓ {}   ↑ {}", fmt::bytes(pd.rx as f64), fmt::bytes(pd.tx as f64))));
        st.set_det_day_text(ss(time::fmt_date(self.det_day)));
        let hourly = id.map(|i| store.app_hourly_series(i, self.det_day)).unwrap_or_default();
        let hv: Vec<(String, String, RxTx)> = hourly.iter().enumerate().map(|(h, v)| (h.to_string(), format!("{} {:02}:00", time::fmt_date(self.det_day), h), *v)).collect();
        let (b, l) = bars(&hv);
        st.set_det_bars(ModelRc::new(VecModel::from(b)));
        st.set_det_ylabels(strings(l));
        let from = today - 29;
        let daily = id.map(|i| store.app_daily_series(i, from, today)).unwrap_or_else(|| vec![RxTx::default(); 30]);
        let dv: Vec<(String, String, RxTx)> = daily.iter().enumerate().map(|(i, v)| (time::fmt_date_short(from + i as i32), time::fmt_date(from + i as i32), *v)).collect();
        let (b, l) = bars(&dv);
        st.set_det_days(ModelRc::new(VecModel::from(b)));
        st.set_det_days_ylabels(strings(l));
    }

    // ---------- отчёты ----------

    fn load_report(&mut self) {
        let Some(ui) = self.ui() else { return };
        let st = ui.global::<AppState>();
        let r = {
            let store = self.shared.store.lock().unwrap();
            reports::build(&store, self.rep_kind, self.rep_anchor, self.settings.billing_start_day)
        };
        let range_text = |a: i32, b: i32| if a == b { time::fmt_date(a) } else { format!("{} — {}", time::fmt_date(a), time::fmt_date(b)) };
        st.set_rep_kind(match self.rep_kind {
            reports::Kind::Day => 0,
            reports::Kind::Week => 1,
            reports::Kind::Month => 2,
        });
        st.set_rep_period_text(ss(range_text(r.from, r.to)));
        st.set_rep_prev_text(ss(format!("{} {}", t("сравнение с", "compared to"), range_text(r.prev_from, r.prev_to))));
        st.set_rep_down(ss(fmt::bytes(r.totals.rx as f64)));
        st.set_rep_up(ss(fmt::bytes(r.totals.tx as f64)));
        st.set_rep_total(ss(fmt::bytes(r.totals.total() as f64)));
        let change = |c: u64, p: u64| {
            if p == 0 { t("нет данных за прошлый период", "no data for previous period").to_string() } else { format!("{} {}", fmt::change(c, p), t("к прошлому периоду", "vs previous")) }
        };
        st.set_rep_change_down(ss(change(r.totals.rx, r.prev_totals.rx)));
        st.set_rep_change_up(ss(change(r.totals.tx, r.prev_totals.tx)));
        st.set_rep_change_total(ss(change(r.totals.total(), r.prev_totals.total())));
        let peak = r.buckets.iter().max_by_key(|b| b.value.total()).filter(|b| b.value.total() > 0);
        if r.hourly {
            st.set_rep_average(ss(format!("{} {}", fmt::bytes(r.totals.total() as f64 / 24.0), t("в час", "per hour"))));
            st.set_rep_chart_title(ss(t("По часам", "By hour")));
            st.set_rep_peak(ss(peak.map(|b| format!("{} {}:00 — {}", t("пик", "peak"), b.label, fmt::bytes(b.value.total() as f64))).unwrap_or_default()));
        } else {
            let days = (r.to - r.from + 1).max(1) as f64;
            st.set_rep_average(ss(format!("{} {}", fmt::bytes(r.totals.total() as f64 / days), t("в день", "per day"))));
            st.set_rep_chart_title(ss(t("По дням", "By day")));
            st.set_rep_peak(ss(peak.map(|b| format!("{} {} — {}", t("пик", "peak"), b.full_label, fmt::bytes(b.value.total() as f64))).unwrap_or_default()));
        }
        let bv: Vec<(String, String, RxTx)> = r.buckets.iter().map(|b| (b.label.clone(), b.full_label.clone(), b.value)).collect();
        let (b, l) = bars(&bv);
        st.set_rep_bars(ModelRc::new(VecModel::from(b)));
        st.set_rep_ylabels(strings(l));

        // Кольцо: топ-7 и «Остальные».
        let apps_total: u64 = r.apps.iter().map(|a| a.value.total()).sum::<u64>().max(1);
        let mut slices = Vec::new();
        let mut parts: Vec<(String, u64)> = r.apps.iter().take(7).map(|a| (a.name.clone(), a.value.total())).collect();
        let rest: u64 = r.apps.iter().skip(7).map(|a| a.value.total()).sum();
        if rest > 0 {
            parts.push((t("Остальные", "Others").into(), rest));
        }
        let mut a0 = 0.0f64;
        let gap = if parts.len() > 1 { 0.012 } else { 0.0 };
        for (i, (name, v)) in parts.iter().enumerate() {
            let frac = *v as f64 / apps_total as f64;
            let a1 = a0 + frac * std::f64::consts::TAU;
            let c = if name == t("Остальные", "Others") { PALETTE[7] } else { PALETTE[i % 7] };
            if a1 - a0 > gap * 2.0 {
                slices.push(Slice {
                    path: ss(slice_path(a0 + gap, a1 - gap)),
                    color: slint::Color::from_rgb_u8(c.0, c.1, c.2),
                    name: ss(name),
                    value: ss(fmt::bytes(*v as f64)),
                    pct: ss(fmt::percent(frac)),
                });
            }
            a0 = a1;
        }
        st.set_rep_slices(ModelRc::new(VecModel::from(slices)));
        st.set_rep_apps_total(ss(fmt::bytes(apps_total as f64)));
        self.report = Some(r);
        self.fill_rep_apps(&ui);
    }

    fn fill_rep_apps(&self, ui: &AppWindow) {
        let Some(r) = &self.report else { return };
        let st = ui.global::<AppState>();
        let total: f64 = r.apps.iter().map(|a| a.value.total() as f64).sum::<f64>().max(1.0);
        let mut rows: Vec<Row> = r
            .apps
            .iter()
            .map(|a| Row { name: a.name.clone(), path: a.path.clone(), v: [a.value.rx as f64, a.value.tx as f64, a.value.total() as f64], share: a.value.total() as f64 / total })
            .collect();
        sort_rows(&mut rows, self.rep_sort.0, self.rep_sort.1);
        rows.truncate(30);
        let f: [&dyn Fn(f64) -> String; 3] = [&|v| fmt::bytes(v), &|v| fmt::bytes(v), &|v| fmt::bytes(v)];
        st.set_rep_apps(ModelRc::new(VecModel::from(rows.iter().map(|r| proc_row(r, f, true)).collect::<Vec<_>>())));
        st.set_rep_sort_col(self.rep_sort.0);
        st.set_rep_sort_asc(self.rep_sort.1);
    }

    // ---------- лимиты ----------

    fn used(&self, v: RxTx) -> u64 {
        if self.settings.limit_counts_upload { v.total() } else { v.rx }
    }

    fn update_limits(&mut self, ui: &AppWindow) {
        let st = ui.global::<AppState>();
        let s = &self.settings;
        let left = |used: u64, enabled: bool, gb: f64| {
            let limit = gb * GB;
            if !enabled {
                (t("Лимит не задан", "No limit set").to_string(), 0.0)
            } else if used as f64 > limit {
                (format!("{} {} ({} {})", t("Превышен на", "Exceeded by"), fmt::bytes(used as f64 - limit), t("лимит", "limit"), fmt::bytes(limit)), (used as f64 / limit) as f32)
            } else {
                (format!("{} {} {} {}", t("Осталось", "Left"), fmt::bytes(limit - used as f64), t("из", "of"), fmt::bytes(limit)), (used as f64 / limit) as f32)
            }
        };
        let tu = self.used(self.today);
        let (tl, tf) = left(tu, s.daily_limit_enabled, s.daily_limit_gb);
        st.set_lim_today_used(ss(fmt::bytes(tu as f64)));
        st.set_lim_today_left(ss(tl));
        st.set_lim_today_frac(tf);
        let pu = self.used(self.period);
        let (pl, pf) = left(pu, s.monthly_limit_enabled, s.monthly_limit_gb);
        st.set_lim_month_used(ss(fmt::bytes(pu as f64)));
        st.set_lim_month_left(ss(pl));
        st.set_lim_month_frac(pf);
        let p = Period::for_day(time::today(), s.billing_start_day);
        st.set_lim_period_text(ss(format!("{} {} — {}", t("Период:", "Period:"), time::fmt_date(p.start), time::fmt_date(p.last_day()))));
        let forecast = p.forecast(pu, time::now_unix());
        st.set_lim_forecast(ss(format!("{} {}", t("Прогноз к концу периода:", "Forecast for the period:"), fmt::bytes(forecast as f64))));
        st.set_lim_forecast_over(s.monthly_limit_enabled && forecast as f64 > s.monthly_limit_gb * GB);
        let alerts: Vec<AlertRow> = {
            let store = self.shared.store.lock().unwrap();
            store.alerts.iter().rev().take(50).map(|a| AlertRow { time: ss(time::fmt_local_time(a.ts)), text: ss(&a.message) }).collect()
        };
        st.set_alerts(ModelRc::new(VecModel::from(alerts)));
    }

    fn check_limits(&mut self) {
        let today = time::today();
        let p = Period::for_day(today, self.settings.billing_start_day);
        let mut changed = false;
        if self.settings.limit_day_key != today {
            self.settings.limit_day_key = today;
            self.settings.limit_day_fired = 0;
            changed = true;
        }
        if self.settings.limit_period_key != p.start {
            self.settings.limit_period_key = p.start;
            self.settings.limit_period_fired = 0;
            changed = true;
        }
        if self.settings.daily_limit_enabled && self.settings.daily_limit_gb > 0.0 {
            let fired = self.evaluate(self.used(self.today), self.settings.daily_limit_gb * GB, self.settings.limit_day_fired, t("Дневной лимит", "Daily limit"));
            if fired != self.settings.limit_day_fired {
                self.settings.limit_day_fired = fired;
                changed = true;
            }
        }
        if self.settings.monthly_limit_enabled && self.settings.monthly_limit_gb > 0.0 {
            let fired = self.evaluate(self.used(self.period), self.settings.monthly_limit_gb * GB, self.settings.limit_period_fired, t("Лимит на период", "Period limit"));
            if fired != self.settings.limit_period_fired {
                self.settings.limit_period_fired = fired;
                changed = true;
            }
        }
        if changed {
            self.settings.save();
        }
    }

    fn evaluate(&mut self, used: u64, limit: f64, fired: u32, name: &str) -> u32 {
        let pct = used as f64 / limit * 100.0;
        let highest = [80u32, 90, 100].into_iter().filter(|&l| pct >= l as f64 && l > fired).max().unwrap_or(fired);
        if highest == fired {
            return fired;
        }
        let enabled = match highest {
            80 => self.settings.notify80,
            90 => self.settings.notify90,
            _ => self.settings.notify100,
        };
        if enabled {
            let title = if highest >= 100 {
                format!("{}: {}", name, t("превышен", "exceeded"))
            } else {
                format!("{}: {}%", name, highest)
            };
            let msg = format!("{} {} {} {} ({})", t("Израсходовано", "Used"), fmt::bytes(used as f64), t("из", "of"), fmt::bytes(limit), fmt::percent(used as f64 / limit));
            self.tray.notify(&title, &msg);
            self.shared.store.lock().unwrap().add_alert(if highest >= 100 { "limit-exceeded" } else { "limit" }, &format!("{}. {}", title, msg));
        }
        highest
    }

    // ---------- настройки ----------

    fn fill_settings(&mut self, ui: &AppWindow, query_autostart: bool) {
        let st = ui.global::<AppState>();
        let s = &self.settings;
        st.set_s_min_tray(s.minimize_to_tray);
        st.set_s_start_min(s.start_minimized);
        st.set_s_tracking(s.process_tracking);
        st.set_s_theme(s.theme as i32);
        st.set_s_lang(if s.language == "en" { 1 } else { 0 });
        st.set_s_units(if s.bits { 0 } else { 1 });
        st.set_s_incl_virtual(s.include_virtual);
        st.set_s_incl_vpn(s.include_vpn);
        st.set_daily_enabled(s.daily_limit_enabled);
        st.set_daily_gb(s.daily_limit_gb.round() as i32);
        st.set_monthly_enabled(s.monthly_limit_enabled);
        st.set_monthly_gb(s.monthly_limit_gb.round() as i32);
        st.set_billing_day(s.billing_start_day as i32);
        st.set_count_upload(s.limit_counts_upload);
        st.set_n80(s.notify80);
        st.set_n90(s.notify90);
        st.set_n100(s.notify100);
        if query_autostart {
            st.set_s_autostart(crate::autostart::is_enabled());
            let rows: Vec<AdapterRow> = crate::adapters::visible_adapters()
                .into_iter()
                .map(|a| {
                    let mut details = Vec::new();
                    if let Some(ip) = a.ipv4.first() {
                        details.push(ip.clone());
                    }
                    if !a.mac.is_empty() {
                        details.push(a.mac.clone());
                    }
                    if a.speed_bps > 0 {
                        details.push(format!("{} {}", a.speed_bps / 1_000_000, t("Мбит/с", "Mbps")));
                    }
                    if !a.up {
                        details.push(t("отключён", "disconnected").into());
                    }
                    AdapterRow { enabled: !s.excluded_adapters.contains(&a.id), id: ss(&a.id), name: ss(&a.name), desc: ss(&a.description), details: ss(details.join("  ·  ")), up: a.up }
                })
                .collect();
            st.set_adapters(ModelRc::new(VecModel::from(rows)));
            let size = self.shared.store.lock().unwrap().file_size();
            st.set_data_path(ss(data_dir().to_string_lossy()));
            st.set_data_size(ss(fmt::bytes(size as f64)));
        }
    }

    fn apply_filter(&self) {
        let s = &self.settings;
        *self.shared.filter.lock().unwrap() = collector::Filter { excluded: s.excluded_adapters.iter().cloned().collect(), include_virtual: s.include_virtual, include_vpn: s.include_vpn };
        self.shared.tracking.store(s.process_tracking, Ordering::Relaxed);
    }

    fn apply_theme(&self, ui: &AppWindow) {
        let dark = match self.settings.theme {
            1 => false,
            2 => true,
            _ => !crate::win::system_uses_light_theme(true),
        };
        ui.global::<Theme>().set_dark(dark);
        if let Some(hwnd) = hwnd_of(ui) {
            let v: i32 = dark as i32;
            unsafe {
                use windows_sys::Win32::UI::WindowsAndMessaging::*;
                // 20 — DWMWA_USE_IMMERSIVE_DARK_MODE (Windows 10 20H1+), 19 — то же в ранних сборках.
                if windows_sys::Win32::Graphics::Dwm::DwmSetWindowAttribute(hwnd, 20, &v as *const i32 as *const _, 4) != 0 {
                    windows_sys::Win32::Graphics::Dwm::DwmSetWindowAttribute(hwnd, 19, &v as *const i32 as *const _, 4);
                }
                // Windows 10 перерисовывает заголовок только после смены рамки.
                SetWindowPos(hwnd, std::ptr::null_mut(), 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            }
        }
    }

    fn apply_language(&mut self) {
        let en = self.settings.language == "en";
        fmt::set_russian(!en);
        fmt::set_bits(self.settings.bits);
        let _ = slint::select_bundled_translation(if en { "en" } else { "" });
        self.update_menu();
    }

    fn settings_changed(&mut self) {
        let Some(ui) = self.ui() else { return };
        let st = ui.global::<AppState>();
        let s = &mut self.settings;
        s.minimize_to_tray = st.get_s_min_tray();
        s.start_minimized = st.get_s_start_min();
        s.process_tracking = st.get_s_tracking();
        s.theme = st.get_s_theme().clamp(0, 2) as u8;
        s.language = if st.get_s_lang() == 1 { "en".into() } else { "ru".into() };
        s.bits = st.get_s_units() == 0;
        s.include_virtual = st.get_s_incl_virtual();
        s.include_vpn = st.get_s_incl_vpn();
        s.save();
        self.apply_filter();
        self.apply_theme(&ui);
        self.apply_language();
        self.refresh_page(&ui);
    }

    fn limits_changed(&mut self) {
        let Some(ui) = self.ui() else { return };
        let st = ui.global::<AppState>();
        let s = &mut self.settings;
        s.daily_limit_enabled = st.get_daily_enabled();
        s.daily_limit_gb = st.get_daily_gb().max(1) as f64;
        s.monthly_limit_enabled = st.get_monthly_enabled();
        s.monthly_limit_gb = st.get_monthly_gb().max(1) as f64;
        s.billing_start_day = st.get_billing_day().clamp(1, 31) as u32;
        s.limit_counts_upload = st.get_count_upload();
        s.notify80 = st.get_n80();
        s.notify90 = st.get_n90();
        s.notify100 = st.get_n100();
        s.save();
        self.refresh_totals();
        self.update_limits(&ui);
    }

    // ---------- навигация и окно ----------

    fn refresh_page(&mut self, ui: &AppWindow) {
        let snap = self.shared.last.lock().unwrap().clone();
        match ui.global::<AppState>().get_page() {
            0 => self.update_dashboard(ui, &snap),
            1 => self.update_procs(ui, &snap),
            2 => self.load_report(),
            3 => self.update_limits(ui),
            4 => self.fill_settings(ui, true),
            5 => self.update_details(ui, &snap),
            _ => {}
        }
    }

    fn navigate(&mut self, page: i32) {
        let Some(ui) = self.ui() else { return };
        let st = ui.global::<AppState>();
        st.set_message(ss(""));
        st.set_page(page);
        self.refresh_page(&ui);
    }

    fn show_window(&mut self) {
        let Some(ui) = self.ui() else { return };
        let _ = ui.show();
        self.visible = true;
        self.apply_theme(&ui);
        if let Some(hwnd) = hwnd_of(&ui) {
            use windows_sys::Win32::UI::WindowsAndMessaging::*;
            unsafe {
                if IsIconic(hwnd) != 0 {
                    ShowWindow(hwnd, SW_RESTORE);
                }
                SetForegroundWindow(hwnd);
            }
        }
        self.refresh_page(&ui);
    }

    fn save_window_size(&mut self) {
        let Some(ui) = self.ui() else { return };
        let w = ui.window();
        if !self.visible || w.is_maximized() || w.is_minimized() {
            return;
        }
        let size = w.size().to_logical(w.scale_factor());
        self.settings.window_w = size.width;
        self.settings.window_h = size.height;
    }

    fn quit(&mut self) {
        self.save_window_size();
        self.settings.save();
        if let Some(ui) = self.ui() {
            let _ = ui.hide();
        }
        let _ = slint::quit_event_loop();
    }

    fn toggle_pause(&mut self) {
        let p = !self.shared.paused.load(Ordering::Relaxed);
        self.shared.paused.store(p, Ordering::Relaxed);
        if p {
            self.shared.reset_baseline.store(true, Ordering::Relaxed);
        }
        if let Some(ui) = self.ui() {
            ui.global::<AppState>().set_paused(p);
        }
        self.update_menu();
    }

    fn export(&mut self, kind: &str) {
        let Some(r) = self.report.clone() else { return };
        let name = format!("NetpulseRS_{}_{}.{}", time::iso_date(r.from), time::iso_date(r.to), kind);
        let filter = if kind == "csv" { "CSV" } else { "Excel" };
        let tray_notify = move |ok: bool, path: &str| {
            with_app(|a| {
                if ok {
                    a.tray.notify(t("Отчёт сохранён", "Report saved"), path);
                } else {
                    a.tray.notify(t("Не удалось сохранить отчёт", "Failed to save report"), path);
                }
            })
        };
        let kind = kind.to_string();
        // Диалог крутит собственный цикл сообщений — открываем его вне заимствования состояния.
        slint::Timer::single_shot(Duration::ZERO, move || {
            let Some(path) = crate::export::save_dialog(&name, filter, &kind) else { return };
            let data = if kind == "csv" { crate::export::csv(&r) } else { crate::export::xlsx(&r) };
            let ok = std::fs::write(&path, data).is_ok();
            tray_notify(ok, &path);
        });
    }
}

fn on_tray(e: TrayEvent) {
    later(move |a| match e {
        TrayEvent::Open | TrayEvent::BalloonClick => a.show_window(),
        TrayEvent::TogglePause => a.toggle_pause(),
        TrayEvent::Exit | TrayEvent::Shutdown => a.quit(),
        TrayEvent::SettingsChanged => {
            a.tray.invalidate_theme();
            if let Some(ui) = a.ui() {
                a.apply_theme(&ui);
            }
        }
    });
}

pub fn run(start_minimized: bool) -> Result<(), slint::PlatformError> {
    let settings = Settings::load();
    fmt::set_russian(settings.language != "en");
    fmt::set_bits(settings.bits);
    let store = Store::open(&data_dir().join("data"));
    let shared = Shared::new(store);

    let ui = AppWindow::new()?;
    let tray = Tray::create(on_tray);
    let top_model = Rc::new(VecModel::default());
    let procs_model = Rc::new(VecModel::default());
    let today = time::today();
    let app = Rc::new(RefCell::new(App {
        ui: ui.as_weak(),
        shared: shared.clone(),
        settings: settings.clone(),
        tray,
        visible: false,
        prev_page: 0,
        ticks: 0,
        top_sort: (3, false),
        procs_sort: (3, false),
        rep_sort: (3, false),
        rep_kind: reports::Kind::Day,
        rep_anchor: today,
        report: None,
        det: None,
        det_day: today,
        today: RxTx::default(),
        period: RxTx::default(),
        all: RxTx::default(),
        first_day: None,
        top_model: top_model.clone(),
        procs_model: procs_model.clone(),
    }));
    APP.with(|a| *a.borrow_mut() = Some(app.clone()));

    let st = ui.global::<AppState>();
    st.set_version(ss(env!("CARGO_PKG_VERSION")));
    st.set_elevated(crate::win::is_elevated());
    st.set_top(ModelRc::from(top_model));
    st.set_procs(ModelRc::from(procs_model));
    st.set_range_index(match settings.dashboard_range {
        300 => 1,
        3600 => 2,
        _ => 0,
    });
    {
        let mut a = app.borrow_mut();
        a.apply_filter();
        a.apply_language();
        a.fill_settings(&ui, false);
        a.apply_theme(&ui);
        a.refresh_totals();
    }

    st.on_navigate(|p| with_app(|a| a.navigate(p)));
    st.on_toggle_pause(|| with_app(|a| a.toggle_pause()));
    st.on_restart_elevated(|| {
        later(|a| {
            if crate::win::shell_execute("runas", &crate::win::exe_path(), "--restarted") {
                a.quit();
            }
        })
    });
    st.on_set_range(|i| {
        with_app(|a| {
            a.settings.dashboard_range = match i {
                1 => 300,
                2 => 3600,
                _ => 60,
            };
            a.settings.save();
            if let Some(ui) = a.ui() {
                ui.global::<AppState>().set_range_index(i);
                a.build_chart(&ui);
            }
        })
    });
    st.on_chart_tip(|i| {
        CHART.with(|c| {
            let c = c.borrow();
            let Some(p) = c.0.get(i.max(0) as usize) else { return SharedString::new() };
            let ago = (c.0.len() - 1 - i as usize) * c.1;
            let when = if ago == 0 {
                t("сейчас", "now").to_string()
            } else if ago < 60 {
                format!("{} {}", ago, t("с назад", "s ago"))
            } else {
                format!("{} {} {} {}", ago / 60, t("мин", "min"), ago % 60, t("с назад", "s ago"))
            };
            ss(format!("{}\n↓ {}\n↑ {}", when, fmt::speed(p.0 as f64), fmt::speed(p.1 as f64)))
        })
    });
    st.on_sort_top(|c| {
        with_app(|a| {
            a.top_sort = next_sort(a.top_sort, c);
            if let Some(ui) = a.ui() {
                let snap = a.shared.last.lock().unwrap().clone();
                a.update_dashboard(&ui, &snap);
            }
        })
    });
    st.on_sort_procs(|c| {
        with_app(|a| {
            a.procs_sort = next_sort(a.procs_sort, c);
            if let Some(ui) = a.ui() {
                let snap = a.shared.last.lock().unwrap().clone();
                a.update_procs(&ui, &snap);
            }
        })
    });
    st.on_filter_procs(|_| {
        with_app(|a| {
            if let Some(ui) = a.ui() {
                let snap = a.shared.last.lock().unwrap().clone();
                a.update_procs(&ui, &snap);
            }
        })
    });
    st.on_open_process(|r| with_app(|a| a.open_process(r.name.to_string(), r.path.to_string())));
    st.on_back(|| {
        with_app(|a| {
            let p = a.prev_page;
            a.navigate(p);
        })
    });
    st.on_det_shift(|d| {
        with_app(|a| {
            a.det_day = (a.det_day + d).min(time::today());
            if let Some(ui) = a.ui() {
                let snap = a.shared.last.lock().unwrap().clone();
                a.update_details(&ui, &snap);
            }
        })
    });
    st.on_rep_set_kind(|k| {
        with_app(|a| {
            a.rep_kind = reports::Kind::from_index(k);
            a.load_report();
        })
    });
    st.on_rep_shift(|d| {
        with_app(|a| {
            let next = reports::shift(a.rep_kind, a.rep_anchor, d, a.settings.billing_start_day);
            if next <= time::today() || d < 0 {
                a.rep_anchor = next.min(time::today());
            }
            a.load_report();
        })
    });
    st.on_rep_today(|| {
        with_app(|a| {
            a.rep_anchor = time::today();
            a.load_report();
        })
    });
    st.on_rep_export(|k| with_app(|a| a.export(&k)));
    st.on_sort_rep(|c| {
        with_app(|a| {
            a.rep_sort = next_sort(a.rep_sort, c);
            if let Some(ui) = a.ui() {
                a.fill_rep_apps(&ui);
            }
        })
    });
    st.on_limits_changed(|| with_app(|a| a.limits_changed()));
    st.on_settings_changed(|| with_app(|a| a.settings_changed()));
    st.on_autostart_changed(|on| {
        later(move |a| {
            let ok = if on { crate::autostart::enable() } else { crate::autostart::disable() };
            if let Some(ui) = a.ui() {
                let st = ui.global::<AppState>();
                st.set_s_autostart(crate::autostart::is_enabled());
                if !ok {
                    st.set_message(ss(t("Не удалось изменить задачу автозапуска в Планировщике заданий", "Failed to update the Task Scheduler autostart task")));
                }
            }
        })
    });
    st.on_adapter_toggled(|id, on| {
        with_app(|a| {
            let id = id.to_string();
            a.settings.excluded_adapters.retain(|x| *x != id);
            if !on {
                a.settings.excluded_adapters.push(id);
            }
            a.settings.save();
            a.apply_filter();
        })
    });
    st.on_open_data_folder(|| {
        crate::win::shell_execute("open", &data_dir().to_string_lossy(), "");
    });
    st.on_reset_stats(|| {
        with_app(|a| {
            let r = a.shared.store.lock().unwrap().reset_all();
            a.shared.history.lock().unwrap().clear();
            a.refresh_totals();
            if let Some(ui) = a.ui() {
                ui.global::<AppState>().set_message(ss(match r {
                    Ok(()) => t("Статистика сброшена", "Statistics cleared").to_string(),
                    Err(e) => format!("{}: {}", t("Ошибка", "Error"), e),
                }));
                a.fill_settings(&ui, true);
            }
        })
    });
    st.on_open_github(|| crate::win::open_url(GITHUB));

    ui.window().on_close_requested(|| {
        let mut hide = true;
        with_app(|a| {
            a.save_window_size();
            a.settings.save();
            if a.settings.minimize_to_tray {
                a.visible = false;
            } else {
                hide = false;
            }
        });
        if !hide {
            later(|a| a.quit());
        }
        slint::CloseRequestResponse::HideWindow
    });

    ui.window().set_size(slint::LogicalSize::new(settings.window_w, settings.window_h));
    let handle = collector::spawn(shared.clone(), || {
        let _ = slint::invoke_from_event_loop(|| with_app(|a| a.tick()));
    });

    if let Some(p) = std::env::var("NETPULSE_PAGE").ok().and_then(|v| v.parse::<i32>().ok()) {
        st.set_page(p); // для отладки и скриншотов
    }
    if !(start_minimized || settings.start_minimized) {
        app.borrow_mut().show_window();
    }
    app.borrow_mut().update_menu();

    slint::run_event_loop_until_quit()?;

    shared.stop.store(true, Ordering::Relaxed);
    let _ = handle.join();
    if let Some(app) = APP.with(|a| a.borrow_mut().take()) {
        if let Ok(mut a) = app.try_borrow_mut() {
            a.tray.remove();
        }
    }
    Ok(())
}
