param([switch]$Live)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$runner = Join-Path ([IO.Path]::GetTempPath()) ('OnSongDiscoveryTests-' + [guid]::NewGuid().ToString('N') + '.exe')
try {
    $sources = Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' -File | Select-Object -ExpandProperty FullName
    & (Join-Path $framework 'csc.exe') /nologo /langversion:5 /warnaserror+ /codepage:65001 /main:DiscoveryTests /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.IO.Compression.dll /reference:System.Net.Http.dll "/reference:$framework\WPF\System.Speech.dll" "/out:$runner" @sources (Join-Path $PSScriptRoot 'DiscoveryTests.cs')
    if ($LASTEXITCODE) { throw 'Wi-Fi discovery test compilation failed.' }
    if ($Live) { & $runner --live } else { & $runner }
    if ($LASTEXITCODE) { throw 'Wi-Fi discovery tests failed.' }
} finally { [IO.File]::Delete($runner) }
