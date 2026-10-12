# Compiles every src/*.cs except Program.cs together with tests/*.cs, runs the result and passes on its exit code.
$root = Split-Path -Parent $PSScriptRoot
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$out = Join-Path $PSScriptRoot "bin"
New-Item -ItemType Directory -Force $out | Out-Null
$src = @(Get-ChildItem "$root\src\*.cs" | Where-Object Name -ne 'Program.cs' | ForEach-Object FullName)
$tests = @(Get-ChildItem "$PSScriptRoot\*.cs" | ForEach-Object FullName)
$refs = "/r:System.dll", "/r:System.Core.dll", "/r:System.Drawing.dll", "/r:System.Windows.Forms.dll", "/r:System.Web.Extensions.dll", "/r:System.IO.Compression.dll", "/r:System.IO.Compression.FileSystem.dll"
& $csc /nologo /target:exe /codepage:65001 /out:"$out\tests.exe" @refs @src @tests
if ($LASTEXITCODE -ne 0) { Write-Host "COMPILE FAILED"; exit 1 }
& "$out\tests.exe"
exit $LASTEXITCODE
