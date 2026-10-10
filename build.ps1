# Builds dist\2XPing.exe (2X Ping by D7oom2x) with the C# compiler that ships with Windows (.NET Framework 4.8).
# Nothing to install.
#   powershell -ExecutionPolicy Bypass -File build.ps1            -> dist\2XPing.exe
#   powershell -ExecutionPolicy Bypass -File build.ps1 -Release   -> also release\2XPing-v<version>.zip + SHA256
param([switch]$Release)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { throw "C# compiler not found: $csc (.NET Framework 4.8 is required)" }
New-Item -ItemType Directory -Force dist | Out-Null

if (-not (Test-Path assets\app.ico)) {
    & $csc /nologo /out:dist\MakeLogo.exe /r:System.Drawing.dll tools\MakeLogo.cs
    & dist\MakeLogo.exe assets
    Remove-Item dist\MakeLogo.exe
}

& $csc /nologo /target:winexe /optimize+ /codepage:65001 /out:dist\2XPing.exe /win32manifest:app.manifest /win32icon:assets\app.ico `
    /resource:assets\logo.png,logo.png `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll `
    src\*.cs
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Copy-Item relay\setup-relay.sh dist\ -Force
Write-Host "Built dist\2XPing.exe"

if ($Release) {
    $version = (Get-Item dist\2XPing.exe).VersionInfo.ProductVersion   # e.g. 0.9.0-beta
    $name = "2XPing-v$version"
    $stage = Join-Path "release" $name
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Force $stage | Out-Null
    Copy-Item dist\2XPing.exe, relay\setup-relay.sh, LICENSE, README.md, README.en.md, CHANGELOG.md $stage
    # keep the relay script LF so it runs on Linux even if git checked it out with CRLF
    $sh = Join-Path $stage "setup-relay.sh"
    [IO.File]::WriteAllText($sh, ([IO.File]::ReadAllText($sh) -replace "`r`n", "`n"))
    $zip = Join-Path "release" "$name.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path "$stage\*" -DestinationPath $zip
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
    "$hash  $name.zip" | Set-Content (Join-Path "release" "$name.zip.sha256") -Encoding ascii
    Remove-Item $stage -Recurse -Force
    Write-Host "Release package: $zip"
    Write-Host "SHA256: $hash"
}
