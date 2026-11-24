//! Фоновый сбор: раз в секунду — счётчики адаптеров и ETW, запись в хранилище, снимок для UI.

use crate::adapters::{counter_delta, should_include, CounterReader};
use crate::etw::EtwMonitor;
use crate::store::{RxTx, Store};
use std::collections::{HashMap, HashSet, VecDeque};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

pub const HISTORY: usize = 3600;

#[derive(Clone, Debug, Default)]
#[allow(dead_code)]
pub struct ProcRate {
    pub name: String,
    pub path: String,
    pub down: f64,
    pub up: f64,
    pub pids: usize,
}

#[derive(Clone, Debug, Default)]
pub struct Snapshot {
    pub down: f64,
    pub up: f64,
    pub paused: bool,
    pub procs: Vec<ProcRate>,
    pub etw_active: bool,
    pub etw_error: Option<String>,
}

#[derive(Clone, Default)]
pub struct Filter {
    pub excluded: HashSet<String>,
    pub include_virtual: bool,
    pub include_vpn: bool,
}

pub struct Shared {
    pub store: Mutex<Store>,
    pub filter: Mutex<Filter>,
    pub paused: AtomicBool,
    pub tracking: AtomicBool,
    pub reset_baseline: AtomicBool,
    pub stop: AtomicBool,
    pub history: Mutex<VecDeque<(f32, f32)>>,
    pub last: Mutex<Snapshot>,
}

impl Shared {
    pub fn new(store: Store) -> Arc<Shared> {
        Arc::new(Shared {
            store: Mutex::new(store),
            filter: Mutex::new(Filter::default()),
            paused: AtomicBool::new(false),
            tracking: AtomicBool::new(true),
            reset_baseline: AtomicBool::new(false),
            stop: AtomicBool::new(false),
            history: Mutex::new(VecDeque::with_capacity(HISTORY)),
            last: Mutex::new(Snapshot::default()),
        })
    }
}

pub fn spawn(shared: Arc<Shared>, on_tick: impl Fn() + Send + 'static) -> std::thread::JoinHandle<()> {
    std::thread::Builder::new()
        .name("collector".into())
        .spawn(move || run(shared, on_tick))
        .expect("collector thread")
}

fn run(sh: Arc<Shared>, on_tick: impl Fn()) {
    let mut reader = CounterReader::new();
    let mut etw = EtwMonitor::new();
    if sh.tracking.load(Ordering::Relaxed) && crate::win::is_elevated() && !etw.start() {
        crate::log::warn(&format!("ETW недоступен: {}", etw.last_error.clone().unwrap_or_default()));
    }
    let mut states: HashMap<String, (u64, u64)> = HashMap::new();
    let mut last_tick: Option<Instant> = None;
    let started = Instant::now();
    let mut last_flush = Instant::now();
    let mut last_compact = Instant::now() - Duration::from_secs(3600 - 120);
    let mut last_etw_retry = Instant::now();
    let mut next = Instant::now();

    // Базовый замер.
    let now = crate::time::now_unix();
    for c in reader.read(now) {
        states.insert(c.id.clone(), (c.rx, c.tx));
    }
    last_tick = last_tick.or(Some(Instant::now()));
    let _ = etw.drain();

    loop {
        next += Duration::from_secs(1);
        let wait = next.saturating_duration_since(Instant::now());
        // Сон короткими отрезками, чтобы быстро реагировать на остановку.
        let deadline = Instant::now() + wait;
        while Instant::now() < deadline {
            if sh.stop.load(Ordering::Relaxed) {
                break;
            }
            std::thread::sleep((deadline - Instant::now()).min(Duration::from_millis(200)));
        }
        if sh.stop.load(Ordering::Relaxed) {
            break;
        }
        if Instant::now() > next + Duration::from_secs(5) {
            next = Instant::now(); // после сна не догоняем пропущенные тики
        }

        // Включение/выключение учёта по процессам.
        let want_etw = sh.tracking.load(Ordering::Relaxed) && crate::win::is_elevated();
        if !want_etw && etw.is_running() {
            etw.stop();
        } else if want_etw && !etw.is_running() && last_etw_retry.elapsed() > Duration::from_secs(60) {
            last_etw_retry = Instant::now();
            etw.start();
        }

        let now = crate::time::now_unix();
        let tick = Instant::now();
        let elapsed = last_tick.map(|t| (tick - t).as_secs_f64()).unwrap_or(1.0).max(0.001);
        last_tick = Some(tick);
        let baseline = sh.reset_baseline.swap(false, Ordering::Relaxed) || elapsed > 300.0;
        if baseline {
            states.clear();
        }
        let filter = sh.filter.lock().unwrap().clone();
        let paused = sh.paused.load(Ordering::Relaxed);
        let counters = reader.read(now);
        let apps = if etw.is_running() { etw.drain() } else { Vec::new() };
        let day = crate::time::local_day(now);

        let mut total = RxTx::default();
        let mut seen = HashSet::new();
        {
            let mut store = sh.store.lock().unwrap();
            for c in &counters {
                seen.insert(c.id.clone());
                let Some(st) = states.get_mut(&c.id) else {
                    states.insert(c.id.clone(), (c.rx, c.tx));
                    continue;
                };
                let rx = counter_delta(st.0, c.rx, elapsed, c.speed_bps);
                let tx = counter_delta(st.1, c.tx, elapsed, c.speed_bps);
                *st = (c.rx, c.tx);
                if paused || filter.excluded.contains(&c.id) || !should_include(c.kind, filter.include_virtual, filter.include_vpn) {
                    continue;
                }
                let v = RxTx { rx, tx };
                if v.total() > 0 {
                    let id = store.adapter_id(&c.id, &c.name);
                    store.add_adapter(now, day, id, v);
                }
                total.add(v);
            }
            if !paused {
                for a in &apps {
                    if a.rx + a.tx > 0 {
                        let id = store.app_id(&a.name, &a.path);
                        store.add_app(now, day, id, RxTx { rx: a.rx, tx: a.tx });
                    }
                }
            }
        }
        states.retain(|k, _| seen.contains(k));
        if std::env::var_os("NETPULSE_DEBUG").is_some() {
            crate::log::info(&format!("tick: counters={} total={:?} elapsed={:.2} baseline={}", counters.len(), total, elapsed, baseline));
        }

        let secs = elapsed;
        let mut procs: Vec<ProcRate> = if paused {
            Vec::new()
        } else {
            apps.iter()
                .filter(|a| a.rx + a.tx > 0)
                .map(|a| ProcRate { name: a.name.clone(), path: a.path.clone(), down: a.rx as f64 / secs, up: a.tx as f64 / secs, pids: a.pids })
                .collect()
        };
        procs.sort_by(|a, b| (b.down + b.up).total_cmp(&(a.down + a.up)));
        let snap = Snapshot {
            down: if baseline { 0.0 } else { total.rx as f64 / secs },
            up: if baseline { 0.0 } else { total.tx as f64 / secs },
            paused,
            procs,
            etw_active: etw.is_running(),
            etw_error: etw.last_error.clone(),
        };
        {
            let mut h = sh.history.lock().unwrap();
            if h.len() >= HISTORY {
                h.pop_front();
            }
            h.push_back((snap.down as f32, snap.up as f32));
        }
        *sh.last.lock().unwrap() = snap;
        on_tick();

        if last_flush.elapsed() >= Duration::from_secs(15) {
            last_flush = Instant::now();
            if let Err(e) = sh.store.lock().unwrap().flush() {
                crate::log::warn(&format!("Запись журнала: {}", e));
            }
        }
        if last_compact.elapsed() >= Duration::from_secs(3600) && started.elapsed() > Duration::from_secs(60) {
            last_compact = Instant::now();
            if let Err(e) = sh.store.lock().unwrap().compact(now) {
                crate::log::warn(&format!("Компакция: {}", e));
            }
        }
    }

    etw.stop();
    let mut store = sh.store.lock().unwrap();
    if let Err(e) = store.compact(crate::time::now_unix()) {
        crate::log::warn(&format!("Сохранение при выходе: {}", e));
    }
}
