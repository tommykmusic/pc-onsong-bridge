[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$icon = Join-Path $root 'assets\bridge.ico'
$compiler = Join-Path $framework 'csc.exe'
$speech = Join-Path $framework 'WPF\System.Speech.dll'
$tempRoot = [IO.Path]::GetTempPath()
$stage = Join-Path $tempRoot ('OnSongTransportTests-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($stage) | Out-Null
try {
    $options = @('/nologo', '/langversion:5', '/codepage:65001', '/warnaserror+')
    foreach ($tool in 'adb','scrcpy') {
        & $compiler @options "/out:$stage\$tool.exe" (Join-Path $PSScriptRoot 'Fixture.cs')
        if ($LASTEXITCODE) { throw "Could not compile $tool fixture." }
    }
    $sources = @(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' -File | Select-Object -ExpandProperty FullName)
    foreach ($suite in 'Test','ControlTransportTests','VoiceNavigationIntegration','UiTests') {
        $exe = Join-Path $stage "$suite.exe"
        & $compiler @options "/out:$exe" "/resource:$icon,Bridge.ico" "/main:$suite" "/reference:$speech" /reference:System.Net.Http.dll /reference:System.IO.Compression.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll @sources (Join-Path $PSScriptRoot "$suite.cs")
        if ($LASTEXITCODE) { throw "Could not compile $suite." }
        & $exe
        if ($LASTEXITCODE) { throw "$suite failed." }
        if ($suite -eq 'UiTests') {
            & $exe --talkback
            if ($LASTEXITCODE) { throw 'Hands-free wireless tablet UI tests failed.' }
        }
    }
}
finally {
    $resolved = (Resolve-Path -LiteralPath $stage).Path
    if ([IO.Path]::GetDirectoryName($resolved) -eq $tempRoot.TrimEnd('\') -and [IO.Path]::GetFileName($resolved) -match '^OnSongTransportTests-[0-9a-f]{32}$') {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
