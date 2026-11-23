[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$source = Join-Path $root 'src\PlaybackRoute.cs'
$tests = Join-Path $PSScriptRoot 'RouteTests.cs'
$staging = Join-Path ([IO.Path]::GetTempPath()) ('OnSongRouteTests-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($staging) | Out-Null
try {
    $platforms = @('x86')
    if ([Environment]::Is64BitOperatingSystem) { $platforms += 'x64' }
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
    foreach ($platform in $platforms) {
        $exe = Join-Path $staging ("RouteTests-" + $platform + ".exe")
        & $csc /nologo /langversion:5 /warn:4 /warnaserror+ "/platform:$platform" "/out:$exe" $tests $source
        if ($LASTEXITCODE) { throw "Route test compilation failed for $platform." }
        Write-Output "Running $platform route tests"
        & $exe
        if ($LASTEXITCODE) { throw "Route tests failed for $platform." }
        [IO.File]::Delete($exe)
    }
}
finally {
    foreach ($name in 'RouteTests-x86.exe', 'RouteTests-x64.exe') { [IO.File]::Delete((Join-Path $staging $name)) }
    [IO.Directory]::Delete($staging)
}
