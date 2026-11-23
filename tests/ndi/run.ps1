[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$test = Join-Path ([IO.Path]::GetTempPath()) ('OnSong-NdiTest-' + [Guid]::NewGuid().ToString('N') + '.exe')
try {
    $files = @('NdiReceiver','NdiAudioNative','NdiAudioPlayback','WindowsToolTrust','Audio','LiveSpeech','PlaybackRoute','Preferences','VoiceCommands','OnSongActions') |
        ForEach-Object { Join-Path $root ('src\' + $_ + '.cs') }
    & (Join-Path $framework 'csc.exe') /nologo /langversion:5 /codepage:65001 /warnaserror+ "/out:$test" "/reference:$framework\WPF\System.Speech.dll" @files (Join-Path $PSScriptRoot 'ReceiverTests.cs')
    if ($LASTEXITCODE) { throw 'Could not compile NDI receiver tests.' }
    & $test
    if ($LASTEXITCODE) { throw 'NDI receiver tests failed.' }
}
finally { [IO.File]::Delete($test) }
