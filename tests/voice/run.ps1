[CmdletBinding()]
param([switch]$CatalogAudit, [switch]$Latency)

$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$icon = Join-Path $root 'assets\bridge.ico'
$compiler = Join-Path $framework 'csc.exe'
$speech = Join-Path $framework 'WPF\System.Speech.dll'
$tempRoot = [IO.Path]::GetTempPath()
$stage = Join-Path $tempRoot ('OnSongVoiceTests-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($stage) | Out-Null
$runner = Join-Path $stage 'VoiceTests.exe'
try {
    $sources = Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' -File | Select-Object -ExpandProperty FullName
    & $compiler /nologo /main:VoiceTests /langversion:5 /warnaserror+ /codepage:65001 /reference:System.Windows.Forms.dll /reference:System.Net.Http.dll /reference:System.IO.Compression.dll /reference:System.Drawing.dll "/reference:$speech" "/out:$runner" "/resource:$icon,Bridge.ico" @sources (Join-Path $PSScriptRoot 'VoiceTests.cs') (Join-Path $PSScriptRoot 'CatalogAudit.cs') (Join-Path $PSScriptRoot 'LatencyTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Voice test compilation failed.' }
    if ($CatalogAudit) {
        $adb = Join-Path $stage 'adb.exe'
        $scrcpy = Join-Path $stage 'scrcpy.exe'
        & $compiler /nologo /langversion:5 /warnaserror+ /codepage:65001 "/out:$adb" (Join-Path $PSScriptRoot 'CatalogAdb.cs')
        if ($LASTEXITCODE) { throw 'Catalog ADB fixture compilation failed.' }
        [IO.File]::Copy($adb, $scrcpy)
        & $runner --catalog-audit $scrcpy
    } elseif ($Latency) { & $runner --latency } else { & $runner }
    if ($LASTEXITCODE -ne 0) { throw 'Voice tests failed.' }
} finally {
    $absolute = (Resolve-Path -LiteralPath $stage).Path
    if ([IO.Path]::GetDirectoryName($absolute) -eq $tempRoot.TrimEnd('\') -and [IO.Path]::GetFileName($absolute) -match '^OnSongVoiceTests-[0-9a-f]{32}$') {
        Remove-Item -LiteralPath $absolute -Force -Recurse
    }
}
