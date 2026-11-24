//! Трафик по процессам через ETW: ядерная сессия (TCP/IP + UDP, IPv4/IPv6, события процессов).
//! Требует прав администратора. Петлевой трафик не учитывается.

use crate::win::wide;
use std::collections::HashMap;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex, OnceLock};
use windows_sys::core::GUID;
use windows_sys::Win32::Foundation::{CloseHandle, ERROR_ALREADY_EXISTS};
use windows_sys::Win32::System::Diagnostics::Etw::*;
use windows_sys::Win32::System::Threading::{OpenProcess, QueryFullProcessImageNameW, PROCESS_QUERY_LIMITED_INFORMATION};

pub const SESSION_NAME: &str = "NetpulseRS-Kernel-Network";

const TCPIP: GUID = GUID::from_u128(0x9a280ac0_c8e0_11d1_84e2_00c04fb998a2);
const UDPIP: GUID = GUID::from_u128(0xbf3a50c5_a9c9_4988_a005_2df0b7c80f80);
const PROCESS: GUID = GUID::from_u128(0x3d6fa8d0_fe05_11d0_9dda_00c04fd7ba7c);
/// Произвольный GUID нашей сессии (режим системного логгера допускает любой).
const SESSION_GUID: GUID = GUID::from_u128(0x6a1f2c9e_4b7d_4e21_9c3a_5e8d0b7f1a42);

#[derive(Clone, Debug, PartialEq, Eq, Hash)]
pub struct Identity {
    pub name: String,
    pub path: String,
}

impl Identity {
    pub fn key(&self) -> &str {
        if self.path.is_empty() { &self.name } else { &self.path }
    }
}

/// Объём трафика приложения (все его процессы объединены) за интервал.
#[derive(Clone, Debug)]
pub struct AppUsage {
    pub name: String,
    pub path: String,
    pub rx: u64,
    pub tx: u64,
    pub pids: usize,
}

#[derive(Default)]
struct Shared {
    pending: HashMap<u32, (u64, u64)>,
    exited: Vec<u32>,
    images: HashMap<u32, String>,
    cache: HashMap<u32, Identity>,
}

static SHARED: OnceLock<Mutex<Shared>> = OnceLock::new();
fn shared() -> &'static Mutex<Shared> {
    SHARED.get_or_init(|| Mutex::new(Shared::default()))
}

fn query_path(pid: u32) -> Option<String> {
    unsafe {
        let h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, pid);
        if h.is_null() {
            return None;
        }
        let mut buf = [0u16; 1024];
        let mut size = buf.len() as u32;
        let ok = QueryFullProcessImageNameW(h, 0, buf.as_mut_ptr(), &mut size);
        CloseHandle(h);
        (ok != 0).then(|| String::from_utf16_lossy(&buf[..size as usize]))
    }
}

fn file_name(path: &str) -> String {
    path.rsplit(['\\', '/']).next().unwrap_or(path).to_string()
}

fn resolve(s: &mut Shared, pid: u32) -> Identity {
    if pid == 0 || pid == 4 {
        return Identity { name: "System".into(), path: "System".into() };
    }
    if let Some(id) = s.cache.get(&pid) {
        return id.clone();
    }
    let id = match query_path(pid) {
        Some(p) => Identity { name: file_name(&p), path: p },
        None => match s.images.get(&pid) {
            Some(img) => Identity { name: img.clone(), path: String::new() },
            None => Identity { name: "Unknown".into(), path: String::new() },
        },
    };
    if s.cache.len() > 4096 {
        s.cache.clear();
    }
    s.cache.insert(pid, id.clone());
    id
}

fn read_u32(data: &[u8], off: usize) -> Option<u32> {
    data.get(off..off + 4).map(|b| u32::from_le_bytes([b[0], b[1], b[2], b[3]]))
}

fn is_loopback_v4(a: u32) -> bool {
    (a & 0xff) == 127 // адрес в сетевом порядке байт: первый октет — младший байт
}

fn is_loopback_v6(b: &[u8]) -> bool {
    b.len() == 16 && b[..15].iter().all(|&x| x == 0) && b[15] == 1
}

unsafe extern "system" fn on_event(rec: *mut EVENT_RECORD) {
    let r = &*rec;
    let h = &r.EventHeader;
    let data = if r.UserData.is_null() { &[][..] } else { std::slice::from_raw_parts(r.UserData as *const u8, r.UserDataLength as usize) };
    let op = h.EventDescriptor.Opcode;
    let pg = h.ProviderId;
    let is_tcp = pg.data1 == TCPIP.data1 && pg.data2 == TCPIP.data2 && pg.data4 == TCPIP.data4;
    let is_udp = pg.data1 == UDPIP.data1 && pg.data2 == UDPIP.data2 && pg.data4 == UDPIP.data4;
    if is_tcp || is_udp {
        let (recv, v6) = match op {
            10 => (false, false),
            11 => (true, false),
            26 => (false, true),
            27 => (true, true),
            _ => return,
        };
        let (Some(pid), Some(size)) = (read_u32(data, 0), read_u32(data, 4)) else { return };
        if size == 0 {
            return;
        }
        if v6 {
            if data.len() < 40 || is_loopback_v6(&data[8..24]) || is_loopback_v6(&data[24..40]) {
                return;
            }
        } else {
            let (Some(d), Some(s)) = (read_u32(data, 8), read_u32(data, 12)) else { return };
            if is_loopback_v4(d) || is_loopback_v4(s) {
                return;
            }
        }
        let mut sh = shared().lock().unwrap();
        let is_new = !sh.pending.contains_key(&pid);
        let e = sh.pending.entry(pid).or_insert((0, 0));
        if recv { e.0 += size as u64 } else { e.1 += size as u64 }
        // Первый пакет PID в интервале: фиксируем путь, пока процесс жив.
        if is_new && !sh.cache.contains_key(&pid) {
            resolve(&mut sh, pid);
        }
        return;
    }
    if pg.data1 == PROCESS.data1 && pg.data4 == PROCESS.data4 {
        // Process_V3/V4_TypeGroup1: UniqueProcessKey(ptr) PID ParentId SessionId ExitStatus DirectoryTableBase(ptr) [Flags v4] UserSID ImageFileName
        let Some(pid) = read_u32(data, 8) else { return };
        match op {
            1 | 3 => {
                let mut off = 8 + 4 * 4 + 8;
                if h.EventDescriptor.Version >= 4 {
                    off += 4;
                }
                // SID: перед ним структура TOKEN_USER из двух указателей.
                let Some(first) = read_u32(data, off) else { return };
                off = if first == 0 {
                    off + 4
                } else {
                    let n = *data.get(off + 16 + 1).unwrap_or(&0) as usize;
                    off + 16 + 8 + 4 * n
                };
                let Some(rest) = data.get(off..) else { return };
                let end = rest.iter().position(|&c| c == 0).unwrap_or(rest.len());
                let image = String::from_utf8_lossy(&rest[..end]).into_owned();
                if pid > 4 && !image.is_empty() {
                    let mut sh = shared().lock().unwrap();
                    sh.images.insert(pid, image);
                    sh.cache.remove(&pid);
                    if op == 1 {
                        resolve(&mut sh, pid);
                    }
                }
            }
            2 => shared().lock().unwrap().exited.push(pid),
            _ => {}
        }
    }
}

const PROPS_SIZE: usize = std::mem::size_of::<EVENT_TRACE_PROPERTIES>() + 1024 * 2;

fn props_buffer() -> Vec<u64> {
    let mut buf = vec![0u64; PROPS_SIZE.div_ceil(8)];
    let p = buf.as_mut_ptr() as *mut EVENT_TRACE_PROPERTIES;
    unsafe {
        (*p).Wnode.BufferSize = PROPS_SIZE as u32;
        (*p).LoggerNameOffset = std::mem::size_of::<EVENT_TRACE_PROPERTIES>() as u32;
    }
    buf
}

/// Остановить сессию с этим именем (например, оставшуюся после аварийного завершения).
pub fn stop_session(name: &str) -> bool {
    let mut buf = props_buffer();
    let n = wide(name);
    unsafe { ControlTraceW(CONTROLTRACE_HANDLE { Value: 0 }, n.as_ptr(), buf.as_mut_ptr() as *mut EVENT_TRACE_PROPERTIES, EVENT_TRACE_CONTROL_STOP) == 0 }
}

pub struct EtwMonitor {
    running: Arc<AtomicBool>,
    pub last_error: Option<String>,
}

impl EtwMonitor {
    pub fn new() -> Self {
        EtwMonitor { running: Arc::new(AtomicBool::new(false)), last_error: None }
    }

    pub fn is_running(&self) -> bool {
        self.running.load(Ordering::Relaxed)
    }

    pub fn start(&mut self) -> bool {
        if self.is_running() {
            return true;
        }
        if !crate::win::is_elevated() {
            self.last_error = Some(crate::fmt::t("Нужны права администратора", "Administrator rights required").into());
            return false;
        }
        stop_session(SESSION_NAME);
        let mut buf = props_buffer();
        let p = buf.as_mut_ptr() as *mut EVENT_TRACE_PROPERTIES;
        let name = wide(SESSION_NAME);
        let mut handle = CONTROLTRACE_HANDLE { Value: 0 };
        let r = unsafe {
            (*p).Wnode.Guid = SESSION_GUID;
            (*p).Wnode.ClientContext = 1;
            (*p).Wnode.Flags = WNODE_FLAG_TRACED_GUID;
            (*p).BufferSize = 64;
            (*p).MinimumBuffers = 16;
            (*p).MaximumBuffers = 64;
            (*p).FlushTimer = 1;
            (*p).LogFileMode = EVENT_TRACE_REAL_TIME_MODE | EVENT_TRACE_SYSTEM_LOGGER_MODE;
            (*p).EnableFlags = EVENT_TRACE_FLAG_NETWORK_TCPIP | EVENT_TRACE_FLAG_PROCESS;
            StartTraceW(&mut handle, name.as_ptr(), p)
        };
        if r != 0 && r != ERROR_ALREADY_EXISTS {
            self.last_error = Some(format!("StartTrace: {}", r));
            return false;
        }

        let running = self.running.clone();
        running.store(true, Ordering::Relaxed);
        let spawned = std::thread::Builder::new().name("etw".into()).spawn(move || {
            let mut logger = wide(SESSION_NAME);
            let mut lf: EVENT_TRACE_LOGFILEW = unsafe { std::mem::zeroed() };
            lf.LoggerName = logger.as_mut_ptr();
            lf.Anonymous1.ProcessTraceMode = PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD;
            lf.Anonymous2.EventRecordCallback = Some(on_event);
            let h = unsafe { OpenTraceW(&mut lf) };
            if h.Value != u64::MAX {
                unsafe {
                    ProcessTrace(&h, 1, std::ptr::null(), std::ptr::null());
                    CloseTrace(h);
                }
            }
            running.store(false, Ordering::Relaxed);
        });
        if spawned.is_err() {
            self.running.store(false, Ordering::Relaxed);
            stop_session(SESSION_NAME);
            return false;
        }
        self.last_error = None;
        true
    }

    pub fn stop(&mut self) {
        stop_session(SESSION_NAME);
        for _ in 0..30 {
            if !self.is_running() {
                break;
            }
            std::thread::sleep(std::time::Duration::from_millis(100));
        }
    }

    /// Накопленные байты с прошлого вызова, объединённые по exe.
    pub fn drain(&self) -> Vec<AppUsage> {
        let mut sh = shared().lock().unwrap();
        let batch = std::mem::take(&mut sh.pending);
        let exited = std::mem::take(&mut sh.exited);
        let mut by_app: HashMap<String, AppUsage> = HashMap::new();
        for (pid, (rx, tx)) in batch {
            let id = resolve(&mut sh, pid);
            let e = by_app.entry(id.key().to_lowercase()).or_insert_with(|| AppUsage { name: id.name.clone(), path: id.path.clone(), rx: 0, tx: 0, pids: 0 });
            e.rx += rx;
            e.tx += tx;
            e.pids += 1;
        }
        for pid in exited {
            sh.cache.remove(&pid);
            sh.images.remove(&pid);
        }
        by_app.into_values().collect()
    }
}
