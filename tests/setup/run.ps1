$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$stage = Join-Path ([IO.Path]::GetTempPath()) ('OnSongSetupChecks-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($stage)
$fx = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
try {
    $icon = Join-Path $root 'assets\bridge.ico'
    $flags = @('/nologo', '/langversion:5', '/warnaserror+', '/codepage:65001', "/resource:$icon,Bridge.ico",
        '/r:System.Windows.Forms.dll','/r:System.Drawing.dll')
    $app = @(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' -File | Select-Object -ExpandProperty FullName)
    $setup = @('setup\Setup.cs','src\BridgeRelease.cs','src\BridgeWindow.cs') | ForEach-Object { Join-Path $root $_ }
    foreach ($suite in 'PlatformTests','FeatureTests') {
        $exe = Join-Path $stage ($suite + '.exe')
        $sources = if ($suite -eq 'PlatformTests') { $setup } else { $app }
        & (Join-Path $fx 'csc.exe') @flags /r:System.Net.Http.dll /r:System.IO.Compression.dll "/r:$fx\WPF\System.Speech.dll" "/main:$suite" "/out:$exe" @sources (Join-Path $PSScriptRoot ($suite+'.cs'))
        if ($LASTEXITCODE) { throw "$suite build failed." }
        & $exe $stage
        if ($LASTEXITCODE) { throw "$suite failed." }
    }
} finally {
    foreach ($name in 'PlatformTests.exe','FeatureTests.exe','scrcpy.exe','adb.exe') { [IO.File]::Delete((Join-Path $stage $name)) }
    [IO.Directory]::Delete($stage)
}
