//! Сетевые адаптеры: список (GetAdaptersAddresses), видимость как в «Панели управления»,
//! классификация и быстрое чтение 64-битных счётчиков (GetIfTable2).

use crate::win::{from_wide, guid_string, RegKey};
use std::collections::{HashMap, HashSet};
use windows_sys::Win32::NetworkManagement::IpHelper::*;
use windows_sys::Win32::Networking::WinSock::{AF_INET, AF_INET6, AF_UNSPEC, SOCKADDR, SOCKADDR_IN, SOCKADDR_IN6};
use windows_sys::Win32::System::Registry::HKEY_LOCAL_MACHINE;

#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub enum Kind {
    Physical,
    Wireless,
    Virtual,
    Vpn,
    Tunnel,
    Loopback,
}

#[derive(Clone, Debug)]
#[allow(dead_code)]
pub struct AdapterMeta {
    pub id: String,
    pub name: String,
    pub description: String,
    pub kind: Kind,
    pub mac: String,
    pub speed_bps: u64,
    pub up: bool,
    pub ipv4: Vec<String>,
    pub ipv6: Vec<String>,
    pub gateways: Vec<String>,
}

const IF_TYPE_PPP: u32 = 23;
const IF_TYPE_LOOPBACK: u32 = 24;
const IF_TYPE_WIFI: u32 = 71;
const IF_TYPE_TUNNEL: u32 = 131;

const VIRTUAL: &[&str] = &[
    "hyper-v", "vethernet", "wsl", "virtualbox", "vmware", "virtual adapter", "virtual ethernet", "kernel debug", "npcap loopback", "docker",
    "container", "microsoft wi-fi direct virtual", "bluetooth device (personal area network)", "parallels", "qemu", "virtio",
];
const VPN: &[&str] = &[
    "vpn", "tap-windows", "tap-", "wireguard", "wintun", "openvpn", "fortinet", "cisco anyconnect", "juniper", "pangp", "globalprotect", "zerotier",
    "tailscale", "hamachi", "radmin", "softether", "outline", "amnezia", "nordlynx", "protonvpn", "sstp", "l2tp", "pptp", "ikev2",
];
const SERVICE: &[&str] = &["qos packet scheduler", "lightweight filter", "wfp ", "wan miniport", "kernel debug", "teredo", "isatap", "6to4", "ip-https", "wi-fi direct virtual"];

pub fn classify(name: &str, description: &str, if_type: u32) -> Kind {
    if if_type == IF_TYPE_LOOPBACK {
        return Kind::Loopback;
    }
    let text = format!("{} {}", name, description).to_lowercase();
    if if_type == IF_TYPE_PPP || VPN.iter().any(|m| text.contains(m)) {
        return Kind::Vpn;
    }
    if VIRTUAL.iter().any(|m| text.contains(m)) {
        return Kind::Virtual;
    }
    match if_type {
        IF_TYPE_TUNNEL => Kind::Tunnel,
        IF_TYPE_WIFI => Kind::Wireless,
        _ => Kind::Physical,
    }
}

/// Учитывать ли адаптер с учётом настроек.
pub fn should_include(kind: Kind, include_virtual: bool, include_vpn: bool) -> bool {
    match kind {
        Kind::Loopback | Kind::Tunnel => false,
        Kind::Virtual => include_virtual,
        Kind::Vpn => include_vpn,
        _ => true,
    }
}

const NET_CLASS: &str = "{4D36E972-E325-11CE-BFC1-08002BE10318}";

/// GUID адаптеров, видимых в «Панель управления → Сетевые подключения» (в верхнем регистре).
pub fn control_panel_ids() -> Option<HashSet<String>> {
    let cls = RegKey::open(HKEY_LOCAL_MACHINE, &format!(r"SYSTEM\CurrentControlSet\Control\Class\{}", NET_CLASS), false)?;
    let mut registered = HashSet::new();
    let mut hidden = HashSet::new();
    for sub in cls.subkeys() {
        let Some(k) = RegKey::open(HKEY_LOCAL_MACHINE, &format!(r"SYSTEM\CurrentControlSet\Control\Class\{}\{}", NET_CLASS, sub), false) else { continue };
        let Some(id) = k.get_string("NetCfgInstanceId") else { continue };
        let id = id.to_uppercase();
        if k.get_dword("Characteristics").unwrap_or(0) & 0x8 != 0 {
            hidden.insert(id.clone());
        }
        registered.insert(id);
    }
    let base = format!(r"SYSTEM\CurrentControlSet\Control\Network\{}", NET_CLASS);
    let net = RegKey::open(HKEY_LOCAL_MACHINE, &base, false)?;
    let mut result = HashSet::new();
    for id in net.subkeys() {
        let up = id.to_uppercase();
        if !registered.contains(&up) || hidden.contains(&up) {
            continue;
        }
        if RegKey::open(HKEY_LOCAL_MACHINE, &format!(r"{}\{}\Connection", base, id), false).is_some() {
            result.insert(up);
        }
    }
    (!result.is_empty()).then_some(result)
}

pub fn is_user_visible(a: &AdapterMeta, cp: Option<&HashSet<String>>) -> bool {
    if let Some(ids) = cp {
        if !ids.contains(&a.id) {
            return false;
        }
    }
    let text = format!("{} {}", a.name, a.description).to_lowercase();
    !SERVICE.iter().any(|m| text.contains(m))
}

fn sockaddr_to_string(sa: *const SOCKADDR) -> Option<String> {
    if sa.is_null() {
        return None;
    }
    unsafe {
        match (*sa).sa_family {
            AF_INET => {
                let v = &*(sa as *const SOCKADDR_IN);
                let b = v.sin_addr.S_un.S_addr.to_ne_bytes();
                Some(format!("{}.{}.{}.{}", b[0], b[1], b[2], b[3]))
            }
            AF_INET6 => {
                let v = &*(sa as *const SOCKADDR_IN6);
                let b = v.sin6_addr.u.Byte;
                let groups: Vec<u16> = (0..8).map(|i| u16::from_be_bytes([b[i * 2], b[i * 2 + 1]])).collect();
                Some(ipv6_string(&groups))
            }
            _ => None,
        }
    }
}

fn ipv6_string(g: &[u16]) -> String {
    // Самая длинная серия нулей сворачивается в «::».
    let (mut best, mut best_len, mut i) = (usize::MAX, 0, 0);
    while i < 8 {
        if g[i] == 0 {
            let s = i;
            while i < 8 && g[i] == 0 {
                i += 1;
            }
            if i - s > best_len && i - s > 1 {
                best = s;
                best_len = i - s;
            }
        } else {
            i += 1;
        }
    }
    let hex = |r: &[u16]| r.iter().map(|x| format!("{:x}", x)).collect::<Vec<_>>().join(":");
    if best == usize::MAX {
        hex(g)
    } else {
        format!("{}::{}", hex(&g[..best]), hex(&g[best + best_len..]))
    }
}

/// Все адаптеры, кроме петлевого.
pub fn query_adapters() -> Vec<AdapterMeta> {
    let mut out = Vec::new();
    let flags = GAA_FLAG_INCLUDE_GATEWAYS | GAA_FLAG_SKIP_ANYCAST | GAA_FLAG_SKIP_MULTICAST | GAA_FLAG_SKIP_DNS_SERVER;
    let mut size: u32 = 32 * 1024;
    let mut buf: Vec<u64> = Vec::new();
    for _ in 0..4 {
        buf = vec![0u64; (size as usize).div_ceil(8)];
        let r = unsafe { GetAdaptersAddresses(AF_UNSPEC as u32, flags, std::ptr::null(), buf.as_mut_ptr() as *mut IP_ADAPTER_ADDRESSES_LH, &mut size) };
        if r == 0 {
            break;
        }
        if r != 111 {
            // ERROR_BUFFER_OVERFLOW — повтор с нужным размером; иначе ошибка
            return out;
        }
    }
    let mut p = buf.as_ptr() as *const IP_ADAPTER_ADDRESSES_LH;
    unsafe {
        while !p.is_null() {
            let a = &*p;
            p = a.Next;
            if a.IfType == IF_TYPE_LOOPBACK {
                continue;
            }
            let id = std::ffi::CStr::from_ptr(a.AdapterName as *const std::ffi::c_char).to_string_lossy().to_uppercase();
            let name = from_wide(a.FriendlyName);
            let description = from_wide(a.Description);
            let mac = a.PhysicalAddress[..(a.PhysicalAddressLength as usize).min(8)].iter().map(|b| format!("{:02X}", b)).collect::<Vec<_>>().join("-");
            let mut ipv4 = Vec::new();
            let mut ipv6 = Vec::new();
            let mut u = a.FirstUnicastAddress;
            while !u.is_null() {
                let sa = (*u).Address.lpSockaddr;
                if let Some(s) = sockaddr_to_string(sa) {
                    if (*sa).sa_family == AF_INET { ipv4.push(s) } else { ipv6.push(s) }
                }
                u = (*u).Next;
            }
            let mut gateways = Vec::new();
            let mut g = a.FirstGatewayAddress;
            while !g.is_null() {
                if let Some(s) = sockaddr_to_string((*g).Address.lpSockaddr) {
                    gateways.push(s);
                }
                g = (*g).Next;
            }
            out.push(AdapterMeta {
                kind: classify(&name, &description, a.IfType),
                id,
                name,
                description,
                mac,
                speed_bps: if a.ReceiveLinkSpeed == u64::MAX { 0 } else { a.ReceiveLinkSpeed },
                up: a.OperStatus == 1,
                ipv4,
                ipv6,
                gateways,
            });
        }
    }
    out
}

/// Адаптеры для показа пользователю (как в «Панели управления»), активные — первыми.
pub fn visible_adapters() -> Vec<AdapterMeta> {
    let cp = control_panel_ids();
    let mut list: Vec<AdapterMeta> = query_adapters().into_iter().filter(|a| is_user_visible(a, cp.as_ref())).collect();
    list.sort_by(|a, b| b.up.cmp(&a.up).then(a.kind.cmp(&b.kind)).then(a.name.to_lowercase().cmp(&b.name.to_lowercase())));
    list
}

#[derive(Clone, Debug)]
pub struct RawCounter {
    pub id: String,
    pub name: String,
    pub kind: Kind,
    pub speed_bps: u64,
    pub rx: u64,
    pub tx: u64,
}

/// Чтение счётчиков: один вызов GetIfTable2 на тик; метаданные кэшируются на 30 секунд.
pub struct CounterReader {
    meta: HashMap<String, AdapterMeta>,
    loaded: i64,
}

impl CounterReader {
    pub fn new() -> Self {
        CounterReader { meta: HashMap::new(), loaded: i64::MIN }
    }

    pub fn read(&mut self, now: i64) -> Vec<RawCounter> {
        if now.saturating_sub(self.loaded) > 30 {
            self.meta = query_adapters().into_iter().map(|a| (a.id.clone(), a)).collect();
            self.loaded = now;
        }
        let mut out = Vec::new();
        let mut unknown = false;
        unsafe {
            let mut table: *mut MIB_IF_TABLE2 = std::ptr::null_mut();
            if GetIfTable2(&mut table) != 0 || table.is_null() {
                return out;
            }
            let rows = std::slice::from_raw_parts((*table).Table.as_ptr(), (*table).NumEntries as usize);
            for r in rows {
                if r.InterfaceAndOperStatusFlags._bitfield & 0x02 != 0 {
                    continue; // фильтр-драйверы дублируют трафик
                }
                if r.OperStatus != 1 {
                    continue;
                }
                let id = guid_string(&r.InterfaceGuid);
                match self.meta.get(&id) {
                    Some(m) => out.push(RawCounter {
                        id,
                        name: m.name.clone(),
                        kind: m.kind,
                        speed_bps: if r.ReceiveLinkSpeed == u64::MAX { 0 } else { r.ReceiveLinkSpeed },
                        rx: r.InOctets,
                        tx: r.OutOctets,
                    }),
                    None => unknown = true,
                }
            }
            FreeMibTable(table as *const _);
        }
        // Новый активный адаптер, которого нет в кэше: перечитаем список не раньше чем через 5 секунд.
        if unknown && now.saturating_sub(self.loaded) > 5 {
            self.loaded = i64::MIN;
        }
        out
    }
}

const U32_RANGE: u64 = 1 << 32;
const MAX_BYTES_PER_SEC: f64 = 100_000_000_000.0 / 8.0;

/// Дельта счётчика с учётом 32-битного переполнения, сброса и правдоподобия.
pub fn counter_delta(prev: u64, cur: u64, elapsed: f64, link_bps: u64) -> u64 {
    let secs = elapsed.max(1.0);
    let per_sec = if link_bps > 0 { (link_bps as f64 / 8.0 * 1.5).min(MAX_BYTES_PER_SEC) } else { MAX_BYTES_PER_SEC };
    let max = (per_sec * secs) as u64;
    if cur >= prev {
        let d = cur - prev;
        return if d <= max { d } else { 0 };
    }
    if prev < U32_RANGE && cur < U32_RANGE {
        let w = U32_RANGE - prev + cur;
        if w <= max {
            return w;
        }
    }
    if cur <= max { cur } else { 0 }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn deltas() {
        assert_eq!(counter_delta(100, 300, 1.0, 0), 200);
        assert_eq!(counter_delta(U32_RANGE - 10, 5, 1.0, 1_000_000_000), 15);
        assert_eq!(counter_delta(5_000_000_000, 100, 1.0, 1_000_000_000), 100);
        assert_eq!(counter_delta(0, 10_000_000_000, 1.0, 100_000_000), 0);
    }

    #[test]
    fn classify_works() {
        assert_eq!(classify("vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter", 6), Kind::Virtual);
        assert_eq!(classify("Ethernet", "Realtek PCIe GbE", 6), Kind::Physical);
        assert_eq!(classify("WG", "WireGuard Tunnel", 53), Kind::Vpn);
        assert_eq!(ipv6_string(&[0xfe80, 0, 0, 0, 1, 2, 3, 4]), "fe80::1:2:3:4");
    }

    #[test]
    fn live_query() {
        let all = query_adapters();
        assert!(!all.is_empty());
        let mut r = CounterReader::new();
        let _ = r.read(crate::time::now_unix());
    }
}
