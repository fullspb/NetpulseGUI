//! Значок в трее (Shell_NotifyIcon) со скоростью, меню, всплывающие уведомления,
//! а также скрытое окно для сообщений от второго экземпляра программы.

use crate::win::wide;
use std::cell::RefCell;
use windows_sys::Win32::Foundation::*;
use windows_sys::Win32::Graphics::Gdi::*;
use windows_sys::Win32::System::LibraryLoader::GetModuleHandleW;
use windows_sys::Win32::UI::Shell::*;
use windows_sys::Win32::UI::WindowsAndMessaging::*;

pub const CLASS_NAME: &str = "NetpulseRS.Messages";
pub const WM_TRAY: u32 = WM_APP + 1;
pub const WM_SHOW_WINDOW: u32 = WM_APP + 2;
pub const WM_SHUTDOWN: u32 = WM_APP + 3;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum TrayEvent {
    Open,
    TogglePause,
    Exit,
    BalloonClick,
    Shutdown,
    SettingsChanged,
}

thread_local! {
    static HANDLER: RefCell<Option<Box<dyn Fn(TrayEvent)>>> = const { RefCell::new(None) };
    static MENU_TEXT: RefCell<(String, String, String, bool)> = const { RefCell::new((String::new(), String::new(), String::new(), false)) };
}

static mut TASKBAR_CREATED: u32 = 0;

fn emit(e: TrayEvent) {
    HANDLER.with(|h| {
        if let Some(f) = h.borrow().as_ref() {
            f(e)
        }
    });
}

unsafe extern "system" fn wndproc(hwnd: HWND, msg: u32, wp: WPARAM, lp: LPARAM) -> LRESULT {
    match msg {
        WM_TRAY => {
            match lp as u32 {
                WM_LBUTTONUP => emit(TrayEvent::Open),
                WM_RBUTTONUP => show_menu(hwnd),
                0x405 => emit(TrayEvent::BalloonClick), // NIN_BALLOONUSERCLICK
                _ => {}
            }
            0
        }
        WM_SHOW_WINDOW => {
            emit(TrayEvent::Open);
            0
        }
        WM_SHUTDOWN => {
            emit(TrayEvent::Shutdown);
            0
        }
        WM_SETTINGCHANGE => {
            emit(TrayEvent::SettingsChanged);
            0
        }
        _ => {
            if msg != 0 && msg == TASKBAR_CREATED {
                // Проводник перезапущен — значок нужно добавить заново.
                TRAY_ADDED.with(|a| a.set(false));
                emit(TrayEvent::SettingsChanged);
            }
            DefWindowProcW(hwnd, msg, wp, lp)
        }
    }
}

thread_local! {
    static TRAY_ADDED: std::cell::Cell<bool> = const { std::cell::Cell::new(false) };
}

unsafe fn show_menu(hwnd: HWND) {
    let (open, pause, exit, paused) = MENU_TEXT.with(|m| m.borrow().clone());
    let menu = CreatePopupMenu();
    let o = wide(&open);
    let p = wide(&pause);
    let x = wide(&exit);
    AppendMenuW(menu, MF_STRING, 1, o.as_ptr());
    AppendMenuW(menu, MF_STRING | if paused { MF_CHECKED } else { 0 }, 2, p.as_ptr());
    AppendMenuW(menu, MF_SEPARATOR, 0, std::ptr::null());
    AppendMenuW(menu, MF_STRING, 3, x.as_ptr());
    SetMenuDefaultItem(menu, 1, 0);
    let mut pt = POINT { x: 0, y: 0 };
    GetCursorPos(&mut pt);
    SetForegroundWindow(hwnd);
    let cmd = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN, pt.x, pt.y, 0, hwnd, std::ptr::null());
    PostMessageW(hwnd, WM_NULL, 0, 0);
    DestroyMenu(menu);
    match cmd {
        1 => emit(TrayEvent::Open),
        2 => emit(TrayEvent::TogglePause),
        3 => emit(TrayEvent::Exit),
        _ => {}
    }
}

pub struct Tray {
    hwnd: HWND,
    icon: HICON,
    light: bool,
    light_checked: i64,
}

impl Tray {
    pub fn create(handler: impl Fn(TrayEvent) + 'static) -> Tray {
        HANDLER.with(|h| *h.borrow_mut() = Some(Box::new(handler)));
        unsafe {
            let inst = GetModuleHandleW(std::ptr::null());
            let cls = wide(CLASS_NAME);
            let wc = WNDCLASSW { lpfnWndProc: Some(wndproc), hInstance: inst, lpszClassName: cls.as_ptr(), ..std::mem::zeroed() };
            RegisterClassW(&wc);
            let hwnd = CreateWindowExW(0, cls.as_ptr(), cls.as_ptr(), 0, 0, 0, 0, 0, std::ptr::null_mut(), std::ptr::null_mut(), inst, std::ptr::null());
            TASKBAR_CREATED = RegisterWindowMessageW(wide("TaskbarCreated").as_ptr());
            // Разрешаем сообщения от экземпляра без прав администратора (UIPI).
            for m in [WM_SHOW_WINDOW, WM_SHUTDOWN, TASKBAR_CREATED] {
                ChangeWindowMessageFilterEx(hwnd, m, MSGFLT_ALLOW, std::ptr::null_mut());
            }
            Tray { hwnd, icon: std::ptr::null_mut(), light: false, light_checked: i64::MIN }
        }
    }

    pub fn set_menu_text(&self, open: &str, pause: &str, exit: &str, paused: bool) {
        MENU_TEXT.with(|m| *m.borrow_mut() = (open.into(), pause.into(), exit.into(), paused));
    }

    fn data(&self) -> NOTIFYICONDATAW {
        let mut d: NOTIFYICONDATAW = unsafe { std::mem::zeroed() };
        d.cbSize = std::mem::size_of::<NOTIFYICONDATAW>() as u32;
        d.hWnd = self.hwnd;
        d.uID = 1;
        d
    }

    /// Обновить значок и подсказку.
    pub fn update(&mut self, down: &str, up: &str, paused: bool, tip: &str) {
        let now = crate::time::now_unix();
        if now.saturating_sub(self.light_checked) > 30 {
            self.light_checked = now;
            self.light = crate::win::system_uses_light_theme(false);
        }
        let icon = render_icon(down, up, paused, self.light);
        let mut d = self.data();
        d.uFlags = NIF_ICON | NIF_TIP | NIF_MESSAGE;
        d.uCallbackMessage = WM_TRAY;
        d.hIcon = icon;
        copy(&mut d.szTip, tip);
        let added = TRAY_ADDED.with(|a| a.get());
        unsafe {
            let ok = Shell_NotifyIconW(if added { NIM_MODIFY } else { NIM_ADD }, &d);
            if ok != 0 {
                TRAY_ADDED.with(|a| a.set(true));
            } else if added {
                TRAY_ADDED.with(|a| a.set(false));
            }
            if !self.icon.is_null() {
                DestroyIcon(self.icon);
            }
        }
        self.icon = icon;
    }

    pub fn invalidate_theme(&mut self) {
        self.light_checked = i64::MIN;
    }

    /// Всплывающее уведомление (в Windows 10/11 показывается как toast).
    pub fn notify(&self, title: &str, text: &str) {
        let mut d = self.data();
        d.uFlags = NIF_INFO;
        copy(&mut d.szInfoTitle, title);
        copy(&mut d.szInfo, text);
        d.dwInfoFlags = NIIF_INFO;
        unsafe { Shell_NotifyIconW(NIM_MODIFY, &d) };
    }

    pub fn remove(&mut self) {
        let d = self.data();
        unsafe {
            Shell_NotifyIconW(NIM_DELETE, &d);
            if !self.icon.is_null() {
                DestroyIcon(self.icon);
                self.icon = std::ptr::null_mut();
            }
        }
        TRAY_ADDED.with(|a| a.set(false));
    }
}

fn copy(dst: &mut [u16], s: &str) {
    let w: Vec<u16> = s.encode_utf16().take(dst.len() - 1).collect();
    dst[..w.len()].copy_from_slice(&w);
    dst[w.len()] = 0;
}

/// Отправить команду уже запущенному экземпляру. Возвращает true, если он найден.
pub fn signal_running_instance(msg: u32) -> bool {
    unsafe {
        let hwnd = FindWindowW(wide(CLASS_NAME).as_ptr(), std::ptr::null());
        if hwnd.is_null() {
            return false;
        }
        AllowSetForegroundWindow(u32::MAX); // ASFW_ANY: даём первому экземпляру вывести окно вперёд
        PostMessageW(hwnd, msg, 0, 0) != 0
    }
}

/// Рисует значок 16×16 (или больше при высоком DPI): ↓ сверху, ↑ снизу.
fn render_icon(down: &str, up: &str, paused: bool, light: bool) -> HICON {
    unsafe {
        let size = GetSystemMetrics(SM_CXSMICON).max(16);
        let screen = GetDC(std::ptr::null_mut());
        let dc = CreateCompatibleDC(screen);
        ReleaseDC(std::ptr::null_mut(), screen);
        let mut bi: BITMAPINFO = std::mem::zeroed();
        bi.bmiHeader.biSize = std::mem::size_of::<BITMAPINFOHEADER>() as u32;
        bi.bmiHeader.biWidth = size;
        bi.bmiHeader.biHeight = -size;
        bi.bmiHeader.biPlanes = 1;
        bi.bmiHeader.biBitCount = 32;
        let mut bits: *mut core::ffi::c_void = std::ptr::null_mut();
        let bmp = CreateDIBSection(dc, &bi, DIB_RGB_COLORS, &mut bits, std::ptr::null_mut(), 0);
        let old = SelectObject(dc, bmp);
        SetBkMode(dc, TRANSPARENT as i32);
        SetTextColor(dc, 0x00FF_FFFF);
        SetTextAlign(dc, TA_BASELINE | TA_LEFT);

        let half = size as f32 / 2.0;
        let pad = (half * 0.06).max(0.5);
        let cap = half - pad * 2.0;
        let em = (cap / 0.70).round() as i32;
        let face = wide("Segoe UI");
        let mut lines = [(down, 0.0f32), (up, half)];
        for (text, top) in lines.iter_mut() {
            if text.is_empty() {
                continue;
            }
            let w: Vec<u16> = text.encode_utf16().collect();
            let mut width_param = 0;
            for _ in 0..2 {
                let font = CreateFontW(-em, width_param, 0, 0, FW_BOLD as i32, 0, 0, 0, DEFAULT_CHARSET as u32, 0, 0, ANTIALIASED_QUALITY as u32, 0, face.as_ptr());
                let of = SelectObject(dc, font);
                let mut sz = SIZE { cx: 0, cy: 0 };
                GetTextExtentPoint32W(dc, w.as_ptr(), w.len() as i32, &mut sz);
                if sz.cx > size && width_param == 0 {
                    let mut tm: TEXTMETRICW = std::mem::zeroed();
                    GetTextMetricsW(dc, &mut tm);
                    width_param = ((tm.tmAveCharWidth as f32) * (size as f32 - 0.5) / sz.cx as f32).floor().max(1.0) as i32;
                    SelectObject(dc, of);
                    DeleteObject(font);
                    continue;
                }
                let x = ((size - sz.cx) / 2).max(0);
                let y = (*top + pad + cap).round() as i32;
                TextOutW(dc, x, y, w.as_ptr(), w.len() as i32);
                SelectObject(dc, of);
                DeleteObject(font);
                break;
            }
        }
        GdiFlush();

        let (dn, upc): ((u8, u8, u8), (u8, u8, u8)) = if paused {
            ((140, 140, 140), (140, 140, 140))
        } else if light {
            ((0, 95, 184), (196, 82, 0))
        } else {
            ((96, 205, 255), (255, 178, 92))
        };
        let px = std::slice::from_raw_parts_mut(bits as *mut u8, (size * size * 4) as usize);
        for y in 0..size {
            let c = if (y as f32) < half { dn } else { upc };
            for x in 0..size {
                let i = ((y * size + x) * 4) as usize;
                let a = px[i].max(px[i + 1]).max(px[i + 2]);
                px[i] = c.2;
                px[i + 1] = c.1;
                px[i + 2] = c.0;
                px[i + 3] = a;
            }
        }
        SelectObject(dc, old);
        DeleteDC(dc);
        let mask = CreateBitmap(size, size, 1, 1, std::ptr::null());
        let ii = ICONINFO { fIcon: 1, xHotspot: 0, yHotspot: 0, hbmMask: mask, hbmColor: bmp };
        let icon = CreateIconIndirect(&ii);
        DeleteObject(mask);
        DeleteObject(bmp);
        icon
    }
}
