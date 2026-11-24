//! Настройки в %LOCALAPPDATA%\NetpulseRS\settings.ini (простые строки «ключ=значение»).

use std::path::PathBuf;

#[derive(Clone, Debug, PartialEq)]
pub struct Settings {
    pub language: String, // ru / en
    pub theme: u8,        // 0 — как в системе, 1 — светлая, 2 — тёмная
    pub bits: bool,
    pub minimize_to_tray: bool,
    pub start_minimized: bool,
    pub process_tracking: bool,
    pub excluded_adapters: Vec<String>,
    pub include_virtual: bool,
    pub include_vpn: bool,
    pub billing_start_day: u32,
    pub daily_limit_enabled: bool,
    pub daily_limit_gb: f64,
    pub monthly_limit_enabled: bool,
    pub monthly_limit_gb: f64,
    pub limit_counts_upload: bool,
    pub notify80: bool,
    pub notify90: bool,
    pub notify100: bool,
    pub limit_day_key: i32,
    pub limit_day_fired: u32,
    pub limit_period_key: i32,
    pub limit_period_fired: u32,
    pub window_w: f32,
    pub window_h: f32,
    pub dashboard_range: u32,
}

impl Default for Settings {
    fn default() -> Self {
        Settings {
            language: "ru".into(),
            theme: 0,
            bits: true,
            minimize_to_tray: true,
            start_minimized: false,
            process_tracking: true,
            excluded_adapters: Vec::new(),
            include_virtual: false,
            include_vpn: false,
            billing_start_day: 1,
            daily_limit_enabled: false,
            daily_limit_gb: 5.0,
            monthly_limit_enabled: false,
            monthly_limit_gb: 100.0,
            limit_counts_upload: true,
            notify80: true,
            notify90: true,
            notify100: true,
            limit_day_key: 0,
            limit_day_fired: 0,
            limit_period_key: 0,
            limit_period_fired: 0,
            window_w: 1100.0,
            window_h: 740.0,
            dashboard_range: 60,
        }
    }
}

pub fn data_dir() -> PathBuf {
    let base = std::env::var_os("LOCALAPPDATA").map(PathBuf::from).unwrap_or_else(std::env::temp_dir);
    base.join("NetpulseRS")
}

fn path() -> PathBuf {
    data_dir().join("settings.ini")
}

fn b(v: &str) -> bool {
    v == "1" || v.eq_ignore_ascii_case("true")
}

impl Settings {
    pub fn load() -> Settings {
        let mut s = Settings::default();
        let Ok(text) = std::fs::read_to_string(path()) else { return s };
        for line in text.lines() {
            let Some((k, v)) = line.split_once('=') else { continue };
            let v = v.trim();
            match k.trim() {
                "language" => s.language = if v == "en" { "en".into() } else { "ru".into() },
                "theme" => s.theme = v.parse().unwrap_or(0).min(2),
                "bits" => s.bits = b(v),
                "minimize_to_tray" => s.minimize_to_tray = b(v),
                "start_minimized" => s.start_minimized = b(v),
                "process_tracking" => s.process_tracking = b(v),
                "excluded_adapters" => s.excluded_adapters = v.split(',').filter(|x| !x.is_empty()).map(|x| x.to_uppercase()).collect(),
                "include_virtual" => s.include_virtual = b(v),
                "include_vpn" => s.include_vpn = b(v),
                "billing_start_day" => s.billing_start_day = v.parse().unwrap_or(1u32).clamp(1, 31),
                "daily_limit_enabled" => s.daily_limit_enabled = b(v),
                "daily_limit_gb" => s.daily_limit_gb = v.parse().unwrap_or(5.0),
                "monthly_limit_enabled" => s.monthly_limit_enabled = b(v),
                "monthly_limit_gb" => s.monthly_limit_gb = v.parse().unwrap_or(100.0),
                "limit_counts_upload" => s.limit_counts_upload = b(v),
                "notify80" => s.notify80 = b(v),
                "notify90" => s.notify90 = b(v),
                "notify100" => s.notify100 = b(v),
                "limit_day_key" => s.limit_day_key = v.parse().unwrap_or(0),
                "limit_day_fired" => s.limit_day_fired = v.parse().unwrap_or(0),
                "limit_period_key" => s.limit_period_key = v.parse().unwrap_or(0),
                "limit_period_fired" => s.limit_period_fired = v.parse().unwrap_or(0),
                "window_w" => s.window_w = v.parse().unwrap_or(1100.0f32).clamp(640.0, 4000.0),
                "window_h" => s.window_h = v.parse().unwrap_or(740.0f32).clamp(480.0, 3000.0),
                "dashboard_range" => s.dashboard_range = match v.parse().unwrap_or(60) { 300 => 300, 3600 => 3600, _ => 60 },
                _ => {}
            }
        }
        s
    }

    pub fn save(&self) {
        let bl = |v: bool| if v { "1" } else { "0" };
        let text = format!(
            "language={}\ntheme={}\nbits={}\nminimize_to_tray={}\nstart_minimized={}\nprocess_tracking={}\nexcluded_adapters={}\n\
             include_virtual={}\ninclude_vpn={}\nbilling_start_day={}\ndaily_limit_enabled={}\ndaily_limit_gb={}\nmonthly_limit_enabled={}\n\
             monthly_limit_gb={}\nlimit_counts_upload={}\nnotify80={}\nnotify90={}\nnotify100={}\nlimit_day_key={}\nlimit_day_fired={}\n\
             limit_period_key={}\nlimit_period_fired={}\nwindow_w={}\nwindow_h={}\ndashboard_range={}\n",
            self.language,
            self.theme,
            bl(self.bits),
            bl(self.minimize_to_tray),
            bl(self.start_minimized),
            bl(self.process_tracking),
            self.excluded_adapters.join(","),
            bl(self.include_virtual),
            bl(self.include_vpn),
            self.billing_start_day,
            bl(self.daily_limit_enabled),
            self.daily_limit_gb,
            bl(self.monthly_limit_enabled),
            self.monthly_limit_gb,
            bl(self.limit_counts_upload),
            bl(self.notify80),
            bl(self.notify90),
            bl(self.notify100),
            self.limit_day_key,
            self.limit_day_fired,
            self.limit_period_key,
            self.limit_period_fired,
            self.window_w.round(),
            self.window_h.round(),
            self.dashboard_range,
        );
        let p = path();
        let _ = std::fs::create_dir_all(data_dir());
        let tmp = p.with_extension("tmp");
        if std::fs::write(&tmp, text).is_ok() {
            let _ = std::fs::rename(&tmp, &p);
        }
    }
}
