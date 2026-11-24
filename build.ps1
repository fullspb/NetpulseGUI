<#
.SYNOPSIS
    Сборка NetpulseRS: тесты и релизный exe (Rust + MinGW-w64, без Visual Studio).
.EXAMPLE
    .\build.ps1
    .\build.ps1 -SkipTests
#>
param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$mingw = Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" -Directory -Filter 'BrechtSanders.WinLibs*' -ErrorAction SilentlyContinue |
    ForEach-Object { Join-Path $_.FullName 'mingw64\bin' } | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $mingw) { throw 'MinGW-w64 не найден: winget install BrechtSanders.WinLibs.POSIX.UCRT' }
$env:PATH = "$env:USERPROFILE\.cargo\bin;$mingw;$env:PATH"
Set-Location $PSScriptRoot
if (-not $SkipTests) {
    $env:__COMPAT_LAYER = 'RunAsInvoker'   # тестовый exe тоже получает манифест requireAdministrator
    cargo test --release
    if ($LASTEXITCODE -ne 0) { throw 'Тесты не прошли' }
    Remove-Item Env:__COMPAT_LAYER
}
cargo build --release
if ($LASTEXITCODE -ne 0) { throw 'Ошибка сборки' }
$exe = Join-Path $PSScriptRoot 'target\release\NetpulseRS.exe'
New-Item -ItemType Directory -Force (Join-Path $PSScriptRoot 'dist') | Out-Null
Copy-Item $exe (Join-Path $PSScriptRoot 'dist\NetpulseRS.exe') -Force
$zip = Join-Path $PSScriptRoot 'dist\NetpulseRS.zip'
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path $exe -DestinationPath $zip -CompressionLevel Optimal
'{0}: {1:N1} МБ, в zip: {2:N1} МБ' -f 'NetpulseRS.exe', ((Get-Item $exe).Length / 1MB), ((Get-Item $zip).Length / 1MB)
