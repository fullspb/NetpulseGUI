; Установщик NetpulseGUI (Inno Setup 6).
; Сборка: .\build.ps1   или   ISCC.exe /DAppVersion=1.0.0 /DPublishDir=..\artifacts\publish NetpulseGUI.iss

#ifndef AppVersion
  #define AppVersion "1.1.4"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif

#define AppName "NetpulseGUI"
#define AppExe "NetpulseGUI.exe"

[Setup]
AppId={{5B0F3C1E-8E2A-4C59-9F3B-6B1D2A9C7E41}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=NetpulseGUI
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputBaseFilename=NetpulseGUI-{#AppVersion}-setup
SetupIconFile=..\src\NetpulseGUI\Assets\netpulse.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
PrivilegesRequired=admin
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
ru.AutoStart=Запускать NetpulseGUI при входе в Windows
en.AutoStart=Start NetpulseGUI when signing in to Windows
ru.DeleteData=Удалить также статистику трафика и настройки NetpulseGUI?
en.DeleteData=Also delete NetpulseGUI traffic statistics and settings?
ru.LaunchApp=Запустить NetpulseGUI
en.LaunchApp=Launch NetpulseGUI

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "autostart"; Description: "{cm:AutoStart}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#PublishDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; Задача Планировщика «при входе пользователя, с наивысшими правами» — без UAC при каждом старте.
Filename: "{sys}\schtasks.exe"; \
  Parameters: "/Create /F /RL HIGHEST /SC ONLOGON /DELAY 0000:05 /TN ""NetpulseGUI Autostart ({code:TaskUser})"" /TR ""\""{app}\{#AppExe}\"" --minimized"" /RU ""{code:FullUser}"" /IT"; \
  Flags: runhidden; Tasks: autostart
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent shellexec
; Тихая установка (автообновление из приложения): запускаем новую версию сразу после установки.
Filename: "{app}\{#AppExe}"; Flags: nowait skipifnotsilent

[UninstallRun]
; Корректное завершение (запись буфера и остановка ETW), затем принудительное — на случай зависания.
Filename: "{app}\{#AppExe}"; Parameters: "--shutdown"; Flags: runhidden waituntilterminated; RunOnceId: "StopApp"
Filename: "{cmd}"; Parameters: "/c timeout /t 3 /nobreak >nul & taskkill /IM {#AppExe} /F"; Flags: runhidden; RunOnceId: "KillApp"
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /F /TN ""NetpulseGUI Autostart ({code:TaskUser})"""; Flags: runhidden; RunOnceId: "DelTask"
; Останавливаем ETW-сессию, если приложение было завершено аварийно.
Filename: "{sys}\logman.exe"; Parameters: "stop NetpulseGUI-Kernel-Network -ets"; Flags: runhidden; RunOnceId: "StopEtw"

[Code]
function FullUser(Param: String): String;
begin
  Result := GetUserNameString;
  if GetEnv('USERDOMAIN') <> '' then
    Result := GetEnv('USERDOMAIN') + '\' + Result;
end;

{ Имя задачи совпадает с тем, что создаёт само приложение: "NetpulseGUI Autostart (DOMAIN_user)" }
function TaskUser(Param: String): String;
begin
  Result := FullUser('');
  StringChangeEx(Result, '\', '_', True);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\NetpulseGUI');
    if DirExists(DataDir) and not UninstallSilent then
      if MsgBox(CustomMessage('DeleteData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
  end;
end;
