[CmdletBinding()]
param([string]$Archive)

$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$source = Join-Path $root 'src\ScrcpyPackage.cs'
$test = Join-Path $PSScriptRoot 'PackageTests.cs'
$exe = Join-Path ([IO.Path]::GetTempPath()) ('OnSongPackageTests-' + [Guid]::NewGuid().ToString('N') + '.exe')
try {
    & $compiler /nologo /langversion:5 /warn:4 /warnaserror+ /reference:System.IO.Compression.dll /reference:System.Net.Http.dll "/out:$exe" $test $source
    if ($LASTEXITCODE) { throw 'Package test compilation failed.' }
    if ($Archive) { & $exe (Resolve-Path -LiteralPath $Archive).Path } else { & $exe }
    if ($LASTEXITCODE) { throw 'Package installation tests failed.' }
}
finally { [IO.File]::Delete($exe) }
