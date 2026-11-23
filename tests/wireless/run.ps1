[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
$icon = Join-Path $root 'assets\bridge.ico'
$compiler = Join-Path $framework 'csc.exe'
$tempRoot = [IO.Path]::GetTempPath()
$stage = Join-Path $tempRoot ('OnSongWirelessTests-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($stage) | Out-Null
try {
    $options = @('/nologo', '/langversion:5', '/codepage:65001', '/warnaserror+')
    foreach ($tool in 'adb','scrcpy') {
        & $compiler @options "/out:$stage\$tool.exe" (Join-Path $PSScriptRoot 'Fixture.cs')
        if ($LASTEXITCODE) { throw "Could not build $tool fixture." }
    }
    $sources = Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' -File | Select-Object -ExpandProperty FullName
    $exe = Join-Path $stage 'WirelessTests.exe'
    & $compiler @options "/out:$exe" "/resource:$icon,Bridge.ico" /main:WirelessTests "/reference:$framework\WPF\System.Speech.dll" /reference:System.Net.Http.dll /reference:System.IO.Compression.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll @sources (Join-Path $PSScriptRoot 'WirelessTests.cs')
    if ($LASTEXITCODE) { throw 'Could not build wireless tests.' }
    & $exe
    if ($LASTEXITCODE) { throw 'Wireless tests failed.' }
}
finally {
    $absolute = (Resolve-Path -LiteralPath $stage).Path
    if ([IO.Path]::GetDirectoryName($absolute) -eq $tempRoot.TrimEnd('\') -and [IO.Path]::GetFileName($absolute) -match '^OnSongWirelessTests-[0-9a-f]{32}$') {
        Remove-Item -LiteralPath $absolute -Force -Recurse
    }
}
