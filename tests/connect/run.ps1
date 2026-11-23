[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$tempRoot = [IO.Path]::GetTempPath()
$stage = Join-Path $tempRoot ('OnSongConnectTests-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($stage) | Out-Null
try {
    $src = Join-Path $root 'src'
    $sources = @(Get-ChildItem -LiteralPath $src -Filter 'Connect*.cs' -File | Select-Object -ExpandProperty FullName)
    foreach ($name in 'Audio','LiveSpeech','HandsFreeListening','PlaybackRoute','OnSongActions','Preferences','VoiceCommands') {
        $sources += Join-Path $src ($name + '.cs')
    }
    $exe = Join-Path $stage 'ConnectTests.exe'
    & (Join-Path $framework 'csc.exe') /nologo /langversion:5 /warnaserror+ /codepage:65001 "/out:$exe" /reference:System.Net.Http.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll "/reference:$framework\WPF\System.Speech.dll" @sources (Join-Path $PSScriptRoot 'ConnectTests.cs')
    if ($LASTEXITCODE) { throw 'Connect tests did not compile.' }
    & $exe $stage
    if ($LASTEXITCODE) { throw 'Connect tests failed.' }
} finally {
    $absolute = (Resolve-Path -LiteralPath $stage).Path
    if ([IO.Path]::GetDirectoryName($absolute) -eq $tempRoot.TrimEnd('\') -and [IO.Path]::GetFileName($absolute) -match '^OnSongConnectTests-[0-9a-f]{32}$') {
        Remove-Item -LiteralPath $absolute -Force -Recurse
    }
}
