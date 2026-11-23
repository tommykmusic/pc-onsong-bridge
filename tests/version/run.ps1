$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
$stage = Join-Path ([IO.Path]::GetTempPath()) ('OnSongVersionTests-' + [Guid]::NewGuid().ToString('N'))
$app = Join-Path $stage 'PC-OnSong-Bridge.exe'
$setup = Join-Path $stage 'PC-OnSong-Bridge-Setup.exe'
$runner = Join-Path $stage 'VersionTests.exe'
[IO.Directory]::CreateDirectory($stage) | Out-Null
try {
    & (Join-Path $root 'build.ps1') -OutputDirectory $stage | Out-Null
    & $compiler /nologo /langversion:5 /codepage:65001 /warnaserror+ /reference:System.Windows.Forms.dll /reference:System.Drawing.dll "/out:$runner" (Join-Path $PSScriptRoot 'VersionTests.cs') (Join-Path $PSScriptRoot 'IconResources.cs')
    if ($LASTEXITCODE) { throw 'Version test compilation failed.' }
    & $runner $app $setup (Join-Path $root 'assets\bridge.ico')
    if ($LASTEXITCODE) { throw 'Version tests failed.' }
} finally {
    foreach ($file in $runner, $setup, $app) { [IO.File]::Delete($file) }
    [IO.Directory]::Delete($stage)
}
