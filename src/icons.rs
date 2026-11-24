//! Иконки exe-файлов (SHGetFileInfo → RGBA → slint::Image), с кэшем в потоке UI.

use crate::win::wide;
use slint::{Image, Rgba8Pixel, SharedPixelBuffer};
use std::cell::RefCell;
use std::collections::HashMap;
use windows_sys::Win32::Graphics::Gdi::*;
use windows_sys::Win32::UI::Shell::{SHGetFileInfoW, SHFILEINFOW, SHGFI_ICON, SHGFI_LARGEICON};
use windows_sys::Win32::UI::WindowsAndMessaging::{DestroyIcon, GetIconInfo, HICON, ICONINFO};

thread_local! {
    static CACHE: RefCell<HashMap<String, Image>> = RefCell::new(HashMap::new());
}

pub fn for_path(path: &str) -> Image {
    if path.len() < 3 || path.as_bytes()[1] != b':' {
        return Image::default();
    }
    let key = path.to_lowercase();
    if let Some(img) = CACHE.with(|c| c.borrow().get(&key).cloned()) {
        return img;
    }
    let img = load(path).unwrap_or_default();
    CACHE.with(|c| {
        let mut c = c.borrow_mut();
        if c.len() > 512 {
            c.clear();
        }
        c.insert(key, img.clone())
    });
    img
}

fn load(path: &str) -> Option<Image> {
    unsafe {
        let mut info: SHFILEINFOW = std::mem::zeroed();
        let p = wide(path);
        let r = SHGetFileInfoW(p.as_ptr(), 0, &mut info, std::mem::size_of::<SHFILEINFOW>() as u32, SHGFI_ICON | SHGFI_LARGEICON);
        if r == 0 || info.hIcon.is_null() {
            return None;
        }
        let img = icon_to_image(info.hIcon);
        DestroyIcon(info.hIcon);
        img
    }
}

unsafe fn read_bits(dc: HDC, bmp: HBITMAP, w: i32, h: i32) -> Vec<u8> {
    let mut bi: BITMAPINFO = std::mem::zeroed();
    bi.bmiHeader.biSize = std::mem::size_of::<BITMAPINFOHEADER>() as u32;
    bi.bmiHeader.biWidth = w;
    bi.bmiHeader.biHeight = -h;
    bi.bmiHeader.biPlanes = 1;
    bi.bmiHeader.biBitCount = 32;
    let mut buf = vec![0u8; (w * h * 4) as usize];
    GetDIBits(dc, bmp, 0, h as u32, buf.as_mut_ptr() as *mut _, &mut bi, DIB_RGB_COLORS);
    buf
}

unsafe fn icon_to_image(icon: HICON) -> Option<Image> {
    let mut ii: ICONINFO = std::mem::zeroed();
    if GetIconInfo(icon, &mut ii) == 0 {
        return None;
    }
    let result = (|| {
        if ii.hbmColor.is_null() {
            return None;
        }
        let mut bm: BITMAP = std::mem::zeroed();
        GetObjectW(ii.hbmColor, std::mem::size_of::<BITMAP>() as i32, &mut bm as *mut _ as *mut _);
        let (w, h) = (bm.bmWidth, bm.bmHeight);
        if w <= 0 || h <= 0 || w > 256 {
            return None;
        }
        let dc = CreateCompatibleDC(std::ptr::null_mut());
        let color = read_bits(dc, ii.hbmColor, w, h);
        let has_alpha = color.chunks(4).any(|p| p[3] != 0);
        let mask = if has_alpha { Vec::new() } else { read_bits(dc, ii.hbmMask, w, h) };
        DeleteDC(dc);
        let mut px = SharedPixelBuffer::<Rgba8Pixel>::new(w as u32, h as u32);
        for (i, out) in px.make_mut_slice().iter_mut().enumerate() {
            let c = &color[i * 4..i * 4 + 4];
            let a = if has_alpha { c[3] } else if mask[i * 4] == 0 { 255 } else { 0 };
            *out = Rgba8Pixel { r: c[2], g: c[1], b: c[0], a };
        }
        Some(Image::from_rgba8(px))
    })();
    if !ii.hbmColor.is_null() {
        DeleteObject(ii.hbmColor);
    }
    if !ii.hbmMask.is_null() {
        DeleteObject(ii.hbmMask);
    }
    result
}
