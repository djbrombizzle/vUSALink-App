# Builds vUSALink App (vUSALinkApp.exe) and vUSALink-Lite (vUSALinkLite.exe) with the C# compiler that ships with
# Windows (.NET Framework 4.8) - no SDK needed. src\Shared is compiled into both.
# WebView2 SDK DLLs (Microsoft.Web.WebView2 NuGet package, Microsoft-signed) live in .\lib and are copied next to the
# app; Lite doesn't use them.
param([string]$Out)   # optional output folder (default: .\bin)
$ErrorActionPreference = 'Stop'
$fw  = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$wpf = "$fw\WPF"
$wm  = "$env:WINDIR\System32\WinMetadata"   # Windows Runtime metadata (OCR for CRC QF readouts); ships with Windows 10/11
$lib = Join-Path $PSScriptRoot 'lib'
$out = if ($Out) { $Out } else { Join-Path $PSScriptRoot 'bin' }
New-Item -ItemType Directory -Force $out | Out-Null

function Sources($dirs) { $dirs | ForEach-Object { Get-ChildItem (Join-Path $PSScriptRoot "src\$_") -Filter *.cs } | ForEach-Object { $_.FullName } }

# vUSALink-Lite: tray helper for the EDST in a CRC browser tab. One exe, no WebView2.
& "$fw\csc.exe" /nologo /target:winexe /optimize+ /platform:x64 /warn:3 /codepage:65001 `
    "/out:$out\vUSALinkLite.exe" `
    "/r:$wpf\PresentationCore.dll" "/r:$wpf\PresentationFramework.dll" "/r:$wpf\WindowsBase.dll" `
    "/r:$fw\System.Xaml.dll" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll `
    "/r:$fw\System.Runtime.dll" "/r:$fw\System.Runtime.WindowsRuntime.dll" "/r:$wm\Windows.Foundation.winmd" "/r:$wm\Windows.Media.winmd" `
    "/r:$wm\Windows.Graphics.winmd" "/r:$wm\Windows.Storage.winmd" "/r:$wm\Windows.Globalization.winmd" `
    (Sources 'Shared', 'Lite')
if ($LASTEXITCODE -ne 0) { throw "Build failed (vUSALink-Lite)" }

# vUSALink App: the EDST in its own window (WebView2).
$src = Sources 'Shared', 'App'
& "$fw\csc.exe" /nologo /target:winexe /optimize+ /platform:x64 /warn:3 /codepage:65001 `
    "/out:$out\vUSALinkApp.exe" `
    "/r:$wpf\PresentationCore.dll" "/r:$wpf\PresentationFramework.dll" "/r:$wpf\WindowsBase.dll" `
    "/r:$fw\System.Xaml.dll" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll `
    "/r:$lib\Microsoft.Web.WebView2.Core.dll" "/r:$lib\Microsoft.Web.WebView2.Wpf.dll" `
    "/r:$fw\System.Runtime.dll" "/r:$fw\System.Runtime.WindowsRuntime.dll" "/r:$wm\Windows.Foundation.winmd" "/r:$wm\Windows.Media.winmd" `
    "/r:$wm\Windows.Graphics.winmd" "/r:$wm\Windows.Storage.winmd" "/r:$wm\Windows.Globalization.winmd" `
    $src
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Copy-Item "$lib\Microsoft.Web.WebView2.Core.dll", "$lib\Microsoft.Web.WebView2.Wpf.dll", "$lib\WebView2Loader.dll" $out -Force
Write-Host "Built $out\vUSALinkApp.exe and $out\vUSALinkLite.exe"
