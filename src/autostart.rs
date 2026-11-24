//! Автозапуск через Планировщик заданий: «при входе», «с наивысшими правами» — без запроса UAC.

use crate::win::{exe_path, run_hidden};

fn user_name() -> String {
    let domain = std::env::var("USERDOMAIN").unwrap_or_default();
    let user = std::env::var("USERNAME").unwrap_or_default();
    if domain.is_empty() { user } else { format!("{}\\{}", domain, user) }
}

fn task_name() -> String {
    format!("NetpulseRS Autostart ({})", user_name().replace('\\', "_"))
}

pub fn is_enabled() -> bool {
    run_hidden("schtasks.exe", &["/Query", "/TN", &task_name()]) == Some(0)
}

fn xml_escape(s: &str) -> String {
    s.replace('&', "&amp;").replace('<', "&lt;").replace('>', "&gt;").replace('"', "&quot;")
}

pub fn enable() -> bool {
    let user = xml_escape(&user_name());
    let exe = exe_path();
    let dir = std::path::Path::new(&exe).parent().map(|p| p.to_string_lossy().into_owned()).unwrap_or_default();
    let xml = format!(
        r#"<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo><Description>NetpulseRS — монитор сетевого трафика. Запуск при входе пользователя.</Description><Author>NetpulseRS</Author></RegistrationInfo>
  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId><Delay>PT5S</Delay></LogonTrigger></Triggers>
  <Principals><Principal id="Author"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author"><Exec><Command>{exe}</Command><Arguments>--minimized</Arguments><WorkingDirectory>{dir}</WorkingDirectory></Exec></Actions>
</Task>"#,
        user = user,
        exe = xml_escape(&exe),
        dir = xml_escape(&dir)
    );
    let tmp = std::env::temp_dir().join("netpulse-rs-task.xml");
    // schtasks ожидает UTF-16 с BOM.
    let mut bytes = vec![0xFF, 0xFE];
    for u in xml.encode_utf16() {
        bytes.extend_from_slice(&u.to_le_bytes());
    }
    if std::fs::write(&tmp, bytes).is_err() {
        return false;
    }
    let ok = run_hidden("schtasks.exe", &["/Create", "/TN", &task_name(), "/XML", &tmp.to_string_lossy(), "/F"]) == Some(0);
    let _ = std::fs::remove_file(&tmp);
    ok
}

pub fn disable() -> bool {
    run_hidden("schtasks.exe", &["/Delete", "/TN", &task_name(), "/F"]) == Some(0)
}
