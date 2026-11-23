$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$runner = Join-Path ([IO.Path]::GetTempPath()) ('OnSongViewerTests-' + [Guid]::NewGuid().ToString('N') + '.exe')
try {
    & $compiler /nologo /langversion:5 /warnaserror+ /codepage:65001 /reference:System.Xml.dll /reference:System.Drawing.dll "/out:$runner" (Join-Path $root 'src\SongViewer.cs') (Join-Path $PSScriptRoot 'ViewerTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Song viewer tests did not compile.' }
    & $runner
    if ($LASTEXITCODE -ne 0) { throw 'Song viewer tests failed.' }

} finally {
    [IO.File]::Delete($runner)
}
