$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$icon = Join-Path $root 'assets\bridge.ico'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$runner = Join-Path ([IO.Path]::GetTempPath()) ('OnSongCommandTests-' + [Guid]::NewGuid().ToString('N') + '.exe')
try {
    $sources = Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' -File | Select-Object -ExpandProperty FullName
    $speech = Join-Path (Split-Path -Parent $compiler) 'WPF\System.Speech.dll'
    & $compiler /nologo /langversion:5 /warnaserror+ /codepage:65001 /main:CommandTests /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.IO.Compression.dll /reference:System.Net.Http.dll "/reference:$speech" "/out:$runner" "/resource:$icon,Bridge.ico" @sources (Join-Path $PSScriptRoot 'CommandTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Song command test compilation failed.' }
    & $runner
    if ($LASTEXITCODE -ne 0) { throw 'Song command tests failed.' }
    & $runner --desktop-dpi
    if ($LASTEXITCODE -ne 0) { throw 'Song command tests failed at the desktop DPI.' }
} finally {
    [IO.File]::Delete($runner)
}
