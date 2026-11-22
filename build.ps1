<#
.SYNOPSIS
    Сборка NetpulseGUI: тесты, портативный single-file exe и (если установлен Inno Setup) установщик.
.EXAMPLE
    .\build.ps1                # тесты + publish + установщик
    .\build.ps1 -SkipTests
    .\build.ps1 -SkipInstaller
#>
param(
    [switch]$SkipTests,
    [switch]$SkipInstaller,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$publishDir = Join-Path $artifacts 'publish'

if (-not $SkipTests) {
    Write-Host '== Тесты ==' -ForegroundColor Cyan
    dotnet test (Join-Path $root 'tests/NetpulseGUI.Tests/NetpulseGUI.Tests.csproj') -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw 'Тесты не прошли' }
}

Write-Host '== Публикация single-file (win-x64, self-contained) ==' -ForegroundColor Cyan
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish (Join-Path $root 'src/NetpulseGUI/NetpulseGUI.csproj') -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -o $publishDir
if ($LASTEXITCODE -ne 0) { throw 'Ошибка публикации' }

$exe = Join-Path $publishDir 'NetpulseGUI.exe'
Write-Host ("NetpulseGUI.exe: {0:N1} МБ" -f ((Get-Item $exe).Length / 1MB))

if (-not $SkipInstaller) {
    $iscc = @(
        (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source,
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

    if ($iscc) {
        Write-Host '== Установщик Inno Setup ==' -ForegroundColor Cyan
        $version = (Get-Item $exe).VersionInfo.ProductVersion -replace '\+.*$', ''
        & $iscc "/DAppVersion=$version" "/DPublishDir=$publishDir" "/O$artifacts" (Join-Path $root 'installer/NetpulseGUI.iss')
        if ($LASTEXITCODE -ne 0) { throw 'Ошибка сборки установщика' }
    }
    else {
        Write-Warning 'Inno Setup 6 не найден — установщик пропущен (https://jrsoftware.org/isinfo.php)'
    }
}

Write-Host "Готово: $artifacts" -ForegroundColor Green
