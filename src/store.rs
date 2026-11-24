//! Хранилище статистики без внешних зависимостей.
//!
//! Все уровни агрегации держатся в памяти (BTreeMap) и обновляются каждый тик:
//! минуты (48 ч), часы (адаптеры — бессрочно, приложения — 31 день), дни (бессрочно).
//! На диск пишется снимок `data-<gen>.bin` и журнал изменений `journal-<gen>.bin`
//! (дописывается раз в 15 секунд). Раз в час и при выходе журнал сворачивается в новый снимок.

use std::collections::{BTreeMap, HashMap};
use std::io::Write;
use std::path::{Path, PathBuf};

#[derive(Clone, Copy, Default, Debug, PartialEq, Eq)]
pub struct RxTx {
    pub rx: u64,
    pub tx: u64,
}

impl RxTx {
    pub fn total(&self) -> u64 {
        self.rx + self.tx
    }
    pub fn add(&mut self, o: RxTx) {
        self.rx += o.rx;
        self.tx += o.tx;
    }
}

pub type Key = (i64, u32);
pub type Table = BTreeMap<Key, RxTx>;

#[derive(Clone, Debug)]
pub struct Alert {
    pub ts: i64,
    pub kind: String,
    pub message: String,
}

#[derive(Clone, Debug)]
pub struct AppMeta {
    pub name: String,
    pub path: String,
}

const MINUTE_KEEP: i64 = 48 * 3600;
const APP_HOUR_KEEP: i64 = 31 * 86_400;
const MAGIC: &[u8; 6] = b"NPRS01";

#[derive(Default)]
pub struct Store {
    dir: PathBuf,
    gen: u64,
    pub adapters: Vec<(String, String)>,
    adapter_ids: HashMap<String, u32>,
    pub apps: Vec<AppMeta>,
    app_ids: HashMap<String, u32>,
    pub ad_min: Table,
    pub ad_hour: Table,
    pub ad_day: Table,
    pub app_min: Table,
    pub app_hour: Table,
    pub app_day: Table,
    pub alerts: Vec<Alert>,
    /// Ещё не записанные в журнал изменения.
    pending: Vec<u8>,
    pending_deltas: HashMap<(u8, i64, i32, u32), RxTx>,
}

// ---------- сериализация ----------

fn put_str(b: &mut Vec<u8>, s: &str) {
    let bytes = s.as_bytes();
    let len = bytes.len().min(u16::MAX as usize);
    b.extend_from_slice(&(len as u16).to_le_bytes());
    b.extend_from_slice(&bytes[..len]);
}

struct Reader<'a> {
    d: &'a [u8],
    p: usize,
}

impl<'a> Reader<'a> {
    fn take(&mut self, n: usize) -> Option<&'a [u8]> {
        let s = self.d.get(self.p..self.p + n)?;
        self.p += n;
        Some(s)
    }
    fn u8(&mut self) -> Option<u8> {
        self.take(1).map(|b| b[0])
    }
    fn u16(&mut self) -> Option<u16> {
        self.take(2).map(|b| u16::from_le_bytes([b[0], b[1]]))
    }
    fn u32(&mut self) -> Option<u32> {
        self.take(4).map(|b| u32::from_le_bytes(b.try_into().unwrap()))
    }
    fn i32(&mut self) -> Option<i32> {
        self.u32().map(|v| v as i32)
    }
    fn u64(&mut self) -> Option<u64> {
        self.take(8).map(|b| u64::from_le_bytes(b.try_into().unwrap()))
    }
    fn i64(&mut self) -> Option<i64> {
        self.u64().map(|v| v as i64)
    }
    fn str(&mut self) -> Option<String> {
        let n = self.u16()? as usize;
        self.take(n).map(|b| String::from_utf8_lossy(b).into_owned())
    }
}

fn put_table(b: &mut Vec<u8>, t: &Table) {
    b.extend_from_slice(&(t.len() as u32).to_le_bytes());
    for (&(ts, id), v) in t {
        b.extend_from_slice(&ts.to_le_bytes());
        b.extend_from_slice(&id.to_le_bytes());
        b.extend_from_slice(&v.rx.to_le_bytes());
        b.extend_from_slice(&v.tx.to_le_bytes());
    }
}

fn read_table(r: &mut Reader) -> Option<Table> {
    let n = r.u32()?;
    let mut t = Table::new();
    for _ in 0..n {
        let ts = r.i64()?;
        let id = r.u32()?;
        let rx = r.u64()?;
        let tx = r.u64()?;
        t.insert((ts, id), RxTx { rx, tx });
    }
    Some(t)
}

fn bump(t: &mut Table, key: Key, v: RxTx) {
    t.entry(key).or_default().add(v);
}

impl Store {
    pub fn open(dir: &Path) -> Store {
        let _ = std::fs::create_dir_all(dir);
        let mut s = Store { dir: dir.to_path_buf(), ..Default::default() };
        // Самый свежий снимок.
        let mut best: Option<u64> = None;
        if let Ok(rd) = std::fs::read_dir(dir) {
            for e in rd.flatten() {
                let n = e.file_name().to_string_lossy().into_owned();
                if let Some(g) = n.strip_prefix("data-").and_then(|x| x.strip_suffix(".bin")).and_then(|x| x.parse::<u64>().ok()) {
                    best = Some(best.map_or(g, |b: u64| b.max(g)));
                }
            }
        }
        if let Some(g) = best {
            if let Ok(d) = std::fs::read(s.snapshot_path(g)) {
                if !s.load_snapshot(&d) {
                    crate::log::warn("Снимок данных повреждён, начинаем с пустой статистики");
                    s = Store { dir: dir.to_path_buf(), ..Default::default() };
                }
            }
            s.gen = g;
        }
        if let Ok(d) = std::fs::read(s.journal_path(s.gen)) {
            s.replay(&d);
        }
        s.cleanup_files();
        s
    }

    fn snapshot_path(&self, g: u64) -> PathBuf {
        self.dir.join(format!("data-{}.bin", g))
    }
    fn journal_path(&self, g: u64) -> PathBuf {
        self.dir.join(format!("journal-{}.bin", g))
    }

    fn cleanup_files(&self) {
        let Ok(rd) = std::fs::read_dir(&self.dir) else { return };
        for e in rd.flatten() {
            let n = e.file_name().to_string_lossy().into_owned();
            let keep = n == format!("data-{}.bin", self.gen) || n == format!("journal-{}.bin", self.gen);
            if !keep && (n.starts_with("data-") || n.starts_with("journal-")) && n.ends_with(".bin") {
                let _ = std::fs::remove_file(e.path());
            }
        }
    }

    fn load_snapshot(&mut self, d: &[u8]) -> bool {
        let mut r = Reader { d, p: 0 };
        let ok = (|| -> Option<()> {
            if r.take(6)? != MAGIC {
                return None;
            }
            let na = r.u32()?;
            for i in 0..na {
                let g = r.str()?;
                let n = r.str()?;
                self.adapter_ids.insert(g.clone(), i);
                self.adapters.push((g, n));
            }
            let np = r.u32()?;
            for i in 0..np {
                let name = r.str()?;
                let path = r.str()?;
                self.app_ids.insert(app_key(&name, &path), i);
                self.apps.push(AppMeta { name, path });
            }
            self.ad_min = read_table(&mut r)?;
            self.ad_hour = read_table(&mut r)?;
            self.ad_day = read_table(&mut r)?;
            self.app_min = read_table(&mut r)?;
            self.app_hour = read_table(&mut r)?;
            self.app_day = read_table(&mut r)?;
            let nl = r.u32()?;
            for _ in 0..nl {
                let ts = r.i64()?;
                let kind = r.str()?;
                let message = r.str()?;
                self.alerts.push(Alert { ts, kind, message });
            }
            Some(())
        })();
        ok.is_some()
    }

    fn replay(&mut self, d: &[u8]) {
        let mut r = Reader { d, p: 0 };
        loop {
            let start = r.p;
            let ok = (|| -> Option<bool> {
                let Some(tag) = r.u8() else { return Some(false) };
                match tag {
                    1 => {
                        let id = r.u32()?;
                        let g = r.str()?;
                        let n = r.str()?;
                        self.set_adapter_meta(id, g, n);
                    }
                    2 => {
                        let id = r.u32()?;
                        let name = r.str()?;
                        let path = r.str()?;
                        self.set_app_meta(id, name, path);
                    }
                    3 | 4 => {
                        let ts = r.i64()?;
                        let day = r.i32()?;
                        let id = r.u32()?;
                        let v = RxTx { rx: r.u64()?, tx: r.u64()? };
                        self.apply(tag == 4, ts, day, id, v);
                    }
                    5 => {
                        let ts = r.i64()?;
                        let kind = r.str()?;
                        let message = r.str()?;
                        self.alerts.push(Alert { ts, kind, message });
                    }
                    6 => self.clear_all(),
                    _ => return None,
                }
                Some(true)
            })();
            match ok {
                Some(true) => continue,
                Some(false) => break,
                None => {
                    // Оборванная запись в конце журнала (сбой при записи) — отбрасываем хвост.
                    let _ = start;
                    break;
                }
            }
        }
    }

    fn set_adapter_meta(&mut self, id: u32, guid: String, name: String) {
        let i = id as usize;
        if i < self.adapters.len() {
            self.adapters[i] = (guid.clone(), name);
        } else {
            while self.adapters.len() < i {
                self.adapters.push((String::new(), String::new()));
            }
            self.adapters.push((guid.clone(), name));
        }
        self.adapter_ids.insert(guid, id);
    }

    fn set_app_meta(&mut self, id: u32, name: String, path: String) {
        let i = id as usize;
        let key = app_key(&name, &path);
        let meta = AppMeta { name, path };
        if i < self.apps.len() {
            self.apps[i] = meta;
        } else {
            while self.apps.len() < i {
                self.apps.push(AppMeta { name: String::new(), path: String::new() });
            }
            self.apps.push(meta);
        }
        self.app_ids.insert(key, id);
    }

    fn clear_all(&mut self) {
        self.adapters.clear();
        self.adapter_ids.clear();
        self.apps.clear();
        self.app_ids.clear();
        for t in [&mut self.ad_min, &mut self.ad_hour, &mut self.ad_day, &mut self.app_min, &mut self.app_hour, &mut self.app_day] {
            t.clear();
        }
        self.alerts.clear();
    }

    fn apply(&mut self, app: bool, ts: i64, day: i32, id: u32, v: RxTx) {
        let minute = ts - ts.rem_euclid(60);
        let hour = ts - ts.rem_euclid(3600);
        let (m, h, d) = if app { (&mut self.app_min, &mut self.app_hour, &mut self.app_day) } else { (&mut self.ad_min, &mut self.ad_hour, &mut self.ad_day) };
        bump(m, (minute, id), v);
        bump(h, (hour, id), v);
        bump(d, (day as i64, id), v);
    }

    // ---------- запись ----------

    pub fn adapter_id(&mut self, guid: &str, name: &str) -> u32 {
        if let Some(&id) = self.adapter_ids.get(guid) {
            if self.adapters[id as usize].1 == name {
                return id;
            }
            self.adapters[id as usize].1 = name.to_string();
            self.journal_adapter(id);
            return id;
        }
        let id = self.adapters.len() as u32;
        self.adapters.push((guid.to_string(), name.to_string()));
        self.adapter_ids.insert(guid.to_string(), id);
        self.journal_adapter(id);
        id
    }

    fn journal_adapter(&mut self, id: u32) {
        let (g, n) = self.adapters[id as usize].clone();
        self.pending.push(1);
        self.pending.extend_from_slice(&id.to_le_bytes());
        put_str(&mut self.pending, &g);
        put_str(&mut self.pending, &n);
    }

    pub fn app_id(&mut self, name: &str, path: &str) -> u32 {
        let key = app_key(name, path);
        if let Some(&id) = self.app_ids.get(&key) {
            return id;
        }
        let id = self.apps.len() as u32;
        self.apps.push(AppMeta { name: name.to_string(), path: path.to_string() });
        self.app_ids.insert(key, id);
        self.pending.push(2);
        self.pending.extend_from_slice(&id.to_le_bytes());
        put_str(&mut self.pending, name);
        put_str(&mut self.pending, path);
        id
    }

    pub fn find_app(&self, name: &str, path: &str) -> Option<u32> {
        self.app_ids.get(&app_key(name, path)).copied()
    }

    pub fn add_adapter(&mut self, ts: i64, day: i32, id: u32, v: RxTx) {
        self.add(false, ts, day, id, v)
    }

    pub fn add_app(&mut self, ts: i64, day: i32, id: u32, v: RxTx) {
        self.add(true, ts, day, id, v)
    }

    fn add(&mut self, app: bool, ts: i64, day: i32, id: u32, v: RxTx) {
        if v.total() == 0 {
            return;
        }
        self.apply(app, ts, day, id, v);
        let minute = ts - ts.rem_euclid(60);
        self.pending_deltas.entry((if app { 4 } else { 3 }, minute, day, id)).or_default().add(v);
    }

    pub fn add_alert(&mut self, kind: &str, message: &str) {
        let ts = crate::time::now_unix();
        self.alerts.push(Alert { ts, kind: kind.into(), message: message.into() });
        self.pending.push(5);
        self.pending.extend_from_slice(&ts.to_le_bytes());
        put_str(&mut self.pending, kind);
        put_str(&mut self.pending, message);
    }

    /// Дописать накопленные изменения в журнал.
    pub fn flush(&mut self) -> std::io::Result<()> {
        let mut deltas: Vec<_> = self.pending_deltas.drain().collect();
        deltas.sort_by_key(|(k, _)| (k.1, k.0, k.3));
        for ((tag, ts, day, id), v) in deltas {
            self.pending.push(tag);
            self.pending.extend_from_slice(&ts.to_le_bytes());
            self.pending.extend_from_slice(&day.to_le_bytes());
            self.pending.extend_from_slice(&id.to_le_bytes());
            self.pending.extend_from_slice(&v.rx.to_le_bytes());
            self.pending.extend_from_slice(&v.tx.to_le_bytes());
        }
        if self.pending.is_empty() {
            return Ok(());
        }
        let mut f = std::fs::OpenOptions::new().create(true).append(true).open(self.journal_path(self.gen))?;
        f.write_all(&self.pending)?;
        self.pending.clear();
        Ok(())
    }

    /// Удалить устаревшую детализацию и свернуть журнал в новый снимок.
    pub fn compact(&mut self, now: i64) -> std::io::Result<()> {
        self.flush()?;
        let min_cut = now - MINUTE_KEEP;
        self.ad_min = self.ad_min.split_off(&(min_cut, 0));
        self.app_min = self.app_min.split_off(&(min_cut, 0));
        self.app_hour = self.app_hour.split_off(&(now - APP_HOUR_KEEP, 0));
        if self.alerts.len() > 500 {
            let n = self.alerts.len() - 500;
            self.alerts.drain(..n);
        }
        self.write_snapshot()
    }

    fn write_snapshot(&mut self) -> std::io::Result<()> {
        let mut b = Vec::with_capacity(1 << 16);
        b.extend_from_slice(MAGIC);
        b.extend_from_slice(&(self.adapters.len() as u32).to_le_bytes());
        for (g, n) in &self.adapters {
            put_str(&mut b, g);
            put_str(&mut b, n);
        }
        b.extend_from_slice(&(self.apps.len() as u32).to_le_bytes());
        for a in &self.apps {
            put_str(&mut b, &a.name);
            put_str(&mut b, &a.path);
        }
        for t in [&self.ad_min, &self.ad_hour, &self.ad_day, &self.app_min, &self.app_hour, &self.app_day] {
            put_table(&mut b, t);
        }
        b.extend_from_slice(&(self.alerts.len() as u32).to_le_bytes());
        for a in &self.alerts {
            b.extend_from_slice(&a.ts.to_le_bytes());
            put_str(&mut b, &a.kind);
            put_str(&mut b, &a.message);
        }
        let next = self.gen + 1;
        let tmp = self.dir.join("data.tmp");
        {
            let mut f = std::fs::File::create(&tmp)?;
            f.write_all(&b)?;
            f.sync_all()?;
        }
        std::fs::rename(&tmp, self.snapshot_path(next))?;
        self.gen = next;
        self.cleanup_files();
        Ok(())
    }

    pub fn reset_all(&mut self) -> std::io::Result<()> {
        self.clear_all();
        self.pending.clear();
        self.pending_deltas.clear();
        self.write_snapshot()
    }

    pub fn file_size(&self) -> u64 {
        [self.snapshot_path(self.gen), self.journal_path(self.gen)].iter().filter_map(|p| std::fs::metadata(p).ok()).map(|m| m.len()).sum()
    }

    // ---------- запросы ----------

    fn sum_range(t: &Table, from: i64, to_excl: i64) -> RxTx {
        let mut r = RxTx::default();
        for v in t.range((from, 0)..(to_excl, 0)).map(|(_, v)| v) {
            r.add(*v);
        }
        r
    }

    /// Итоги по локальным дням [from; to].
    pub fn totals_days(&self, from: i32, to: i32) -> RxTx {
        Self::sum_range(&self.ad_day, from as i64, to as i64 + 1)
    }

    pub fn all_time(&self) -> RxTx {
        let mut r = RxTx::default();
        for v in self.ad_day.values() {
            r.add(*v);
        }
        r
    }

    pub fn first_day(&self) -> Option<i32> {
        self.ad_day.keys().next().map(|k| k.0 as i32)
    }

    pub fn daily_series(&self, from: i32, to: i32) -> Vec<RxTx> {
        let mut out = vec![RxTx::default(); (to - from + 1).max(0) as usize];
        for (&(d, _), v) in self.ad_day.range((from as i64, 0)..(to as i64 + 1, 0)) {
            out[(d - from as i64) as usize].add(*v);
        }
        out
    }

    fn hourly(t: &Table, day: i32, id: Option<u32>) -> [RxTx; 24] {
        let mut out = [RxTx::default(); 24];
        let start = crate::time::day_start_utc(day);
        let end = crate::time::day_start_utc(day + 1);
        for (&(ts, i), v) in t.range((start, 0)..(end, 0)) {
            if id.is_some_and(|x| x != i) {
                continue;
            }
            let h = (crate::time::to_local(ts).rem_euclid(86_400) / 3600) as usize;
            out[h.min(23)].add(*v);
        }
        out
    }

    pub fn hourly_series(&self, day: i32) -> [RxTx; 24] {
        Self::hourly(&self.ad_hour, day, None)
    }

    pub fn app_hourly_series(&self, id: u32, day: i32) -> [RxTx; 24] {
        Self::hourly(&self.app_hour, day, Some(id))
    }

    pub fn app_daily_series(&self, id: u32, from: i32, to: i32) -> Vec<RxTx> {
        let mut out = vec![RxTx::default(); (to - from + 1).max(0) as usize];
        for (&(d, i), v) in self.app_day.range((from as i64, 0)..(to as i64 + 1, 0)) {
            if i == id {
                out[(d - from as i64) as usize].add(*v);
            }
        }
        out
    }

    /// Итоги приложений за дни [from; to], по убыванию объёма.
    pub fn app_totals(&self, from: i32, to: i32) -> Vec<(u32, RxTx)> {
        let mut map: HashMap<u32, RxTx> = HashMap::new();
        for (&(_, id), v) in self.app_day.range((from as i64, 0)..(to as i64 + 1, 0)) {
            map.entry(id).or_default().add(*v);
        }
        let mut v: Vec<_> = map.into_iter().filter(|(_, t)| t.total() > 0).collect();
        v.sort_by(|a, b| b.1.total().cmp(&a.1.total()));
        v
    }

    pub fn app_total(&self, id: u32, from: i32, to: i32) -> RxTx {
        let mut r = RxTx::default();
        for (&(_, i), v) in self.app_day.range((from as i64, 0)..(to as i64 + 1, 0)) {
            if i == id {
                r.add(*v);
            }
        }
        r
    }
}

pub fn app_key(name: &str, path: &str) -> String {
    if path.is_empty() { name.to_lowercase() } else { path.to_lowercase() }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn persist_roundtrip() {
        let dir = std::env::temp_dir().join(format!("nprs-test-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        let now = 1_764_000_000;
        let day = crate::time::local_day(now);
        {
            let mut s = Store::open(&dir);
            let a = s.adapter_id("{A}", "Ethernet");
            let p = s.app_id("chrome.exe", r"C:\chrome.exe");
            s.add_adapter(now, day, a, RxTx { rx: 100, tx: 10 });
            s.add_app(now, day, p, RxTx { rx: 60, tx: 5 });
            s.flush().unwrap();
            s.add_adapter(now + 1, day, a, RxTx { rx: 1, tx: 1 });
            s.compact(now).unwrap();
            s.add_adapter(now + 2, day, a, RxTx { rx: 1000, tx: 0 });
            s.flush().unwrap();
        }
        let s = Store::open(&dir);
        assert_eq!(s.totals_days(day, day), RxTx { rx: 1101, tx: 11 });
        assert_eq!(s.app_totals(day, day), vec![(0, RxTx { rx: 60, tx: 5 })]);
        assert_eq!(s.apps[0].name, "chrome.exe");
        assert_eq!(s.hourly_series(day).iter().map(|v| v.total()).sum::<u64>(), 1112);
        let _ = std::fs::remove_dir_all(&dir);
    }
}
