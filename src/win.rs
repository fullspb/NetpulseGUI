//! Небольшие помощники для Win32: строки UTF-16, реестр, запуск процессов.

use std::ffi::c_void;
use windows_sys::Win32::Foundation::*;
use windows_sys::Win32::System::Registry::*;

pub fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

pub fn from_wide(p: *const u16) -> String {
    if p.is_null() {
        return String::new();
    }
    unsafe {
        let mut len = 0;
        while *p.add(len) != 0 {
            len += 1;
        }
        String::from_utf16_lossy(std::slice::from_raw_parts(p, len))
    }
}

pub fn from_wide_buf(buf: &[u16]) -> String {
    let len = buf.iter().position(|&c| c == 0).unwrap_or(buf.len());
    String::from_utf16_lossy(&buf[..len])
}

pub fn guid_string(g: &windows_sys::core::GUID) -> String {
    format!(
        "{{{:08X}-{:04X}-{:04X}-{:02X}{:02X}-{:02X}{:02X}{:02X}{:02X}{:02X}{:02X}}}",
        g.data1, g.data2, g.data3, g.data4[0], g.data4[1], g.data4[2], g.data4[3], g.data4[4], g.data4[5], g.data4[6], g.data4[7]
    )
}

/// Открытый ключ реестра; закрывается при удалении.
pub struct RegKey(HKEY);

impl RegKey {
    pub fn open(root: HKEY, path: &str, write: bool) -> Option<RegKey> {
        let p = wide(path);
        let mut h: HKEY = std::ptr::null_mut();
        let access = if write { KEY_READ | KEY_WRITE } else { KEY_READ } | KEY_WOW64_64KEY;
        let r = unsafe { RegOpenKeyExW(root, p.as_ptr(), 0, access, &mut h) };
        if r == ERROR_SUCCESS { Some(RegKey(h)) } else { None }
    }

    pub fn subkeys(&self) -> Vec<String> {
        let mut out = Vec::new();
        let mut buf = [0u16; 260];
        let mut i = 0;
        loop {
            let mut len = buf.len() as u32;
            let r = unsafe {
                RegEnumKeyExW(self.0, i, buf.as_mut_ptr(), &mut len, std::ptr::null(), std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null_mut())
            };
            if r != ERROR_SUCCESS {
                break;
            }
            out.push(String::from_utf16_lossy(&buf[..len as usize]));
            i += 1;
        }
        out
    }

    pub fn get_string(&self, name: &str) -> Option<String> {
        let n = wide(name);
        let mut ty = 0u32;
        let mut size = 0u32;
        unsafe {
            if RegQueryValueExW(self.0, n.as_ptr(), std::ptr::null(), &mut ty, std::ptr::null_mut(), &mut size) != ERROR_SUCCESS {
                return None;
            }
            if ty != REG_SZ && ty != REG_EXPAND_SZ {
                return None;
            }
            let mut buf = vec![0u16; (size as usize / 2) + 1];
            let mut size2 = (buf.len() * 2) as u32;
            if RegQueryValueExW(self.0, n.as_ptr(), std::ptr::null(), &mut ty, buf.as_mut_ptr() as *mut u8, &mut size2) != ERROR_SUCCESS {
                return None;
            }
            Some(from_wide_buf(&buf))
        }
    }

    pub fn get_dword(&self, name: &str) -> Option<u32> {
        let n = wide(name);
        let mut ty = 0u32;
        let mut v = 0u32;
        let mut size = 4u32;
        let r = unsafe { RegQueryValueExW(self.0, n.as_ptr(), std::ptr::null(), &mut ty, &mut v as *mut u32 as *mut u8, &mut size) };
        if r == ERROR_SUCCESS && ty == REG_DWORD { Some(v) } else { None }
    }
}

impl Drop for RegKey {
    fn drop(&mut self) {
        unsafe { RegCloseKey(self.0) };
    }
}

/// Светлая ли тема приложений Windows (для режима «как в системе»).
pub fn system_uses_light_theme(apps: bool) -> bool {
    RegKey::open(HKEY_CURRENT_USER, r"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", false)
        .and_then(|k| k.get_dword(if apps { "AppsUseLightTheme" } else { "SystemUsesLightTheme" }))
        .map(|v| v == 1)
        .unwrap_or(!apps)
}

pub fn is_elevated() -> bool {
    use windows_sys::Win32::Security::*;
    use windows_sys::Win32::System::Threading::*;
    unsafe {
        let mut token: HANDLE = std::ptr::null_mut();
        if OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut token) == 0 {
            return false;
        }
        let mut elev = TOKEN_ELEVATION { TokenIsElevated: 0 };
        let mut len = 0u32;
        let ok = GetTokenInformation(token, TokenElevation, &mut elev as *mut _ as *mut c_void, std::mem::size_of::<TOKEN_ELEVATION>() as u32, &mut len);
        CloseHandle(token);
        ok != 0 && elev.TokenIsElevated != 0
    }
}

/// ShellExecute (открыть ссылку/папку или запустить с «runas»). Возвращает успех.
pub fn shell_execute(verb: &str, file: &str, args: &str) -> bool {
    use windows_sys::Win32::UI::Shell::ShellExecuteW;
    use windows_sys::Win32::UI::WindowsAndMessaging::SW_SHOWNORMAL;
    let v = wide(verb);
    let f = wide(file);
    let a = wide(args);
    let r = unsafe {
        ShellExecuteW(std::ptr::null_mut(), if verb.is_empty() { std::ptr::null() } else { v.as_ptr() }, f.as_ptr(), if args.is_empty() { std::ptr::null() } else { a.as_ptr() }, std::ptr::null(), SW_SHOWNORMAL)
    };
    r as isize > 32
}

/// Открыть URL так, чтобы браузер запустился без прав администратора (через explorer.exe).
pub fn open_url(url: &str) {
    let _ = std::process::Command::new("explorer.exe").arg(url).spawn();
}

pub fn exe_path() -> String {
    std::env::current_exe().map(|p| p.to_string_lossy().into_owned()).unwrap_or_default()
}

/// Запуск консольной утилиты без окна; возвращает код выхода.
pub fn run_hidden(program: &str, args: &[&str]) -> Option<i32> {
    use std::os::windows::process::CommandExt;
    const CREATE_NO_WINDOW: u32 = 0x0800_0000;
    std::process::Command::new(program).args(args).creation_flags(CREATE_NO_WINDOW).output().ok().map(|o| o.status.code().unwrap_or(-1))
}
