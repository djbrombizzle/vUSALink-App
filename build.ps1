# Builds vUSALink App with the C# compiler that ships with Windows (.NET Framework 4.8) - no SDK needed.
# WebView2 SDK DLLs (Microsoft.Web.WebView2 NuGet package, Microsoft-signed) live in .\lib and are copied next to the exe.
$ErrorActionPreference = 'Stop'
$fw  = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$wpf = "$fw\WPF"
$lib = Join-Path $PSScriptRoot 'lib'
$out = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Force $out | Out-Null

$src = Get-ChildItem (Join-Path $PSScriptRoot 'src') -Filter *.cs | ForEach-Object { $_.FullName }
& "$fw\csc.exe" /nologo /target:winexe /optimize+ /platform:x64 /warn:3 /codepage:65001 `
    "/out:$out\vUSALinkApp.exe" `
    "/r:$wpf\PresentationCore.dll" "/r:$wpf\PresentationFramework.dll" "/r:$wpf\WindowsBase.dll" `
    "/r:$fw\System.Xaml.dll" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll `
    "/r:$lib\Microsoft.Web.WebView2.Core.dll" "/r:$lib\Microsoft.Web.WebView2.Wpf.dll" `
    $src
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Copy-Item "$lib\Microsoft.Web.WebView2.Core.dll", "$lib\Microsoft.Web.WebView2.Wpf.dll", "$lib\WebView2Loader.dll" $out -Force
Write-Host "Built $out\vUSALinkApp.exe"
