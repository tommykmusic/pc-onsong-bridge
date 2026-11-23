$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$runner = Join-Path ([IO.Path]::GetTempPath()) ('OnSongMidiTests-' + [guid]::NewGuid().ToString('N') + '.exe')
try {
    $sources = Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' -File | Select-Object -ExpandProperty FullName
    $tests = @((Join-Path $PSScriptRoot 'MidiTests.cs'), (Join-Path $PSScriptRoot 'MidiUiTests.cs'))
    foreach ($suite in 'MidiTests','MidiUiTests') {
        & (Join-Path $framework 'csc.exe') /nologo /langversion:5 /warnaserror+ /codepage:65001 "/main:$suite" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.IO.Compression.dll /reference:System.Net.Http.dll "/reference:$framework\WPF\System.Speech.dll" "/out:$runner" @sources @tests
        if ($LASTEXITCODE) { throw "$suite failed to compile." }
        & $runner
        if ($LASTEXITCODE) { throw "$suite failed." }
    }
} finally { [IO.File]::Delete($runner) }
