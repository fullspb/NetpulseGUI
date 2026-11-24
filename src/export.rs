//! Экспорт отчёта в CSV и XLSX. XLSX собирается вручную: zip без сжатия + несколько XML-файлов.

use crate::reports::Report;

fn crc32(data: &[u8]) -> u32 {
    let mut table = [0u32; 256];
    for (i, t) in table.iter_mut().enumerate() {
        let mut c = i as u32;
        for _ in 0..8 {
            c = if c & 1 != 0 { 0xEDB8_8320 ^ (c >> 1) } else { c >> 1 };
        }
        *t = c;
    }
    let mut crc = 0xFFFF_FFFFu32;
    for &b in data {
        crc = table[((crc ^ b as u32) & 0xFF) as usize] ^ (crc >> 8);
    }
    crc ^ 0xFFFF_FFFF
}

fn zip(files: &[(&str, Vec<u8>)]) -> Vec<u8> {
    let mut out = Vec::new();
    let mut central = Vec::new();
    for (name, data) in files {
        let crc = crc32(data);
        let offset = out.len() as u32;
        let n = name.as_bytes();
        let header = |sig: u32, central: bool| {
            let mut h = Vec::new();
            h.extend_from_slice(&sig.to_le_bytes());
            if central {
                h.extend_from_slice(&20u16.to_le_bytes());
            }
            h.extend_from_slice(&20u16.to_le_bytes()); // версия
            h.extend_from_slice(&0x0800u16.to_le_bytes()); // имена в UTF-8
            h.extend_from_slice(&0u16.to_le_bytes()); // без сжатия
            h.extend_from_slice(&0u16.to_le_bytes());
            h.extend_from_slice(&0x21u16.to_le_bytes());
            h.extend_from_slice(&crc.to_le_bytes());
            h.extend_from_slice(&(data.len() as u32).to_le_bytes());
            h.extend_from_slice(&(data.len() as u32).to_le_bytes());
            h.extend_from_slice(&(n.len() as u16).to_le_bytes());
            h.extend_from_slice(&0u16.to_le_bytes());
            if central {
                h.extend_from_slice(&[0u8; 8]); // комментарий, диск, атрибуты
                h.extend_from_slice(&0u32.to_le_bytes());
                h.extend_from_slice(&offset.to_le_bytes());
            }
            h.extend_from_slice(n);
            h
        };
        out.extend(header(0x0403_4b50, false));
        out.extend_from_slice(data);
        central.extend(header(0x0201_4b50, true));
    }
    let cd_offset = out.len() as u32;
    out.extend_from_slice(&central);
    out.extend_from_slice(&0x0605_4b50u32.to_le_bytes());
    out.extend_from_slice(&[0u8; 4]);
    out.extend_from_slice(&(files.len() as u16).to_le_bytes());
    out.extend_from_slice(&(files.len() as u16).to_le_bytes());
    out.extend_from_slice(&(central.len() as u32).to_le_bytes());
    out.extend_from_slice(&cd_offset.to_le_bytes());
    out.extend_from_slice(&0u16.to_le_bytes());
    out
}

fn esc(s: &str) -> String {
    s.replace('&', "&amp;").replace('<', "&lt;").replace('>', "&gt;").replace('"', "&quot;")
}

enum Cell {
    S(String),
    N(f64),
}

fn col(i: usize) -> String {
    let mut s = String::new();
    let mut n = i + 1;
    while n > 0 {
        s.insert(0, (b'A' + ((n - 1) % 26) as u8) as char);
        n = (n - 1) / 26;
    }
    s
}

fn sheet(rows: &[Vec<Cell>], widths: &[u32]) -> Vec<u8> {
    let mut x = String::from(r#"<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><cols>"#);
    for (i, w) in widths.iter().enumerate() {
        x += &format!(r#"<col min="{0}" max="{0}" width="{1}" customWidth="1"/>"#, i + 1, w);
    }
    x += "</cols><sheetData>";
    for (r, row) in rows.iter().enumerate() {
        x += &format!(r#"<row r="{}">"#, r + 1);
        for (c, cell) in row.iter().enumerate() {
            let rf = format!("{}{}", col(c), r + 1);
            let st = if r == 0 { r#" s="1""# } else { "" };
            match cell {
                Cell::S(s) => x += &format!(r#"<c r="{}" t="inlineStr"{}><is><t>{}</t></is></c>"#, rf, st, esc(s)),
                Cell::N(n) => x += &format!(r#"<c r="{}"{}><v>{}</v></c>"#, rf, st, n),
            }
        }
        x += "</row>";
    }
    x += "</sheetData></worksheet>";
    x.into_bytes()
}

fn mb(v: u64) -> f64 {
    (v as f64 / 1024.0 / 1024.0 * 100.0).round() / 100.0
}

fn bucket_rows(r: &Report) -> Vec<Vec<Cell>> {
    let t = crate::fmt::t;
    let mut rows = vec![vec![
        Cell::S(t(if r.hourly { "Час" } else { "Дата" }, if r.hourly { "Hour" } else { "Date" }).into()),
        Cell::S(t("Загрузка, МБ", "Download, MB").into()),
        Cell::S(t("Отдача, МБ", "Upload, MB").into()),
        Cell::S(t("Всего, МБ", "Total, MB").into()),
    ]];
    for b in &r.buckets {
        rows.push(vec![Cell::S(b.full_label.clone()), Cell::N(mb(b.value.rx)), Cell::N(mb(b.value.tx)), Cell::N(mb(b.value.total()))]);
    }
    rows.push(vec![Cell::S(t("Итого", "Total").into()), Cell::N(mb(r.totals.rx)), Cell::N(mb(r.totals.tx)), Cell::N(mb(r.totals.total()))]);
    rows
}

fn app_rows(r: &Report) -> Vec<Vec<Cell>> {
    let t = crate::fmt::t;
    let mut rows = vec![vec![
        Cell::S(t("Приложение", "Application").into()),
        Cell::S(t("Путь", "Path").into()),
        Cell::S(t("Загрузка, МБ", "Download, MB").into()),
        Cell::S(t("Отдача, МБ", "Upload, MB").into()),
        Cell::S(t("Всего, МБ", "Total, MB").into()),
    ]];
    for a in &r.apps {
        rows.push(vec![Cell::S(a.name.clone()), Cell::S(a.path.clone()), Cell::N(mb(a.value.rx)), Cell::N(mb(a.value.tx)), Cell::N(mb(a.value.total()))]);
    }
    rows
}

pub fn xlsx(r: &Report) -> Vec<u8> {
    let t = crate::fmt::t;
    let ct = r#"<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/worksheets/sheet2.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>"#;
    let rels = r#"<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>"#;
    let wb = format!(
        r#"<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="{}" sheetId="1" r:id="rId1"/><sheet name="{}" sheetId="2" r:id="rId2"/></sheets></workbook>"#,
        t("Трафик", "Traffic"),
        t("Приложения", "Applications")
    );
    let wbrels = r#"<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet2.xml"/><Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>"#;
    let styles = r#"<?xml version="1.0" encoding="UTF-8" standalone="yes"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font></fonts><fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills><borders count="1"><border/></borders><cellStyleXfs count="1"><xf/></cellStyleXfs><cellXfs count="2"><xf/><xf fontId="1" applyFont="1"/></cellXfs></styleSheet>"#;
    zip(&[
        ("[Content_Types].xml", ct.as_bytes().to_vec()),
        ("_rels/.rels", rels.as_bytes().to_vec()),
        ("xl/workbook.xml", wb.into_bytes()),
        ("xl/_rels/workbook.xml.rels", wbrels.as_bytes().to_vec()),
        ("xl/styles.xml", styles.as_bytes().to_vec()),
        ("xl/worksheets/sheet1.xml", sheet(&bucket_rows(r), &[18, 16, 16, 16])),
        ("xl/worksheets/sheet2.xml", sheet(&app_rows(r), &[28, 60, 16, 16, 16])),
    ])
}

pub fn csv(r: &Report) -> Vec<u8> {
    let field = |c: &Cell| match c {
        Cell::S(s) => format!("\"{}\"", s.replace('"', "\"\"")),
        Cell::N(n) => {
            let s = n.to_string();
            if crate::fmt::russian() { s.replace('.', ",") } else { s }
        }
    };
    let sep = if crate::fmt::russian() { ";" } else { "," };
    let mut out = String::from("\u{feff}");
    for row in bucket_rows(r).iter().chain([vec![]].iter()).chain(app_rows(r).iter()) {
        out += &row.iter().map(field).collect::<Vec<_>>().join(sep);
        out += "\r\n";
    }
    out.into_bytes()
}

/// Диалог «Сохранить как». Возвращает выбранный путь.
pub fn save_dialog(default_name: &str, filter_name: &str, ext: &str) -> Option<String> {
    use windows_sys::Win32::UI::Controls::Dialogs::*;
    let mut file = [0u16; 1024];
    let def: Vec<u16> = default_name.encode_utf16().collect();
    file[..def.len()].copy_from_slice(&def);
    let filter: Vec<u16> = format!("{} (*.{})\0*.{}\0\0", filter_name, ext, ext).encode_utf16().collect();
    let dext = crate::win::wide(ext);
    let mut ofn: OPENFILENAMEW = unsafe { std::mem::zeroed() };
    ofn.lStructSize = std::mem::size_of::<OPENFILENAMEW>() as u32;
    ofn.lpstrFilter = filter.as_ptr();
    ofn.lpstrFile = file.as_mut_ptr();
    ofn.nMaxFile = file.len() as u32;
    ofn.lpstrDefExt = dext.as_ptr();
    ofn.Flags = OFN_OVERWRITEPROMPT | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR;
    if unsafe { GetSaveFileNameW(&mut ofn) } == 0 {
        return None;
    }
    Some(crate::win::from_wide_buf(&file))
}

#[cfg(test)]
mod tests {
    #[test]
    fn crc() {
        assert_eq!(super::crc32(b"123456789"), 0xCBF4_3926);
        assert_eq!(super::col(0), "A");
        assert_eq!(super::col(27), "AB");
    }
}
