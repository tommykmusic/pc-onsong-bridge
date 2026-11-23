[CmdletBinding()]
param(
    [string] $OutputDirectory,
    [switch] $Package
)

$ErrorActionPreference = 'Stop'
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $PSScriptRoot 'dist'
}

$frameworkRoots = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319')
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319')
)
$framework = $frameworkRoots | Where-Object { Test-Path -LiteralPath (Join-Path $_ 'csc.exe') } | Select-Object -First 1
if (-not $framework) {
    throw 'The Windows .NET Framework C# compiler is required.'
}

$compiler = Join-Path $framework 'csc.exe'
$speech = Join-Path $framework 'WPF\System.Speech.dll'
if (-not (Test-Path -LiteralPath $speech)) {
    throw 'The Windows .NET Framework System.Speech assembly is required.'
}

$sources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' -File |
    Sort-Object Name | Select-Object -ExpandProperty FullName)
if ($sources.Count -eq 0) {
    throw 'No C# source files were found.'
}

$destination = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
$staging = Join-Path ([IO.Path]::GetTempPath()) ('PCOnSongBridge-' + [Guid]::NewGuid().ToString('N'))
$app = Join-Path $staging 'PC-OnSong-Bridge.exe'
$setup = Join-Path $staging 'PC-OnSong-Bridge-Setup.exe'
$setupSource = Join-Path $PSScriptRoot 'setup\Setup.cs'
$license = Join-Path $PSScriptRoot 'LICENSE'
$icon = Join-Path $PSScriptRoot 'assets\bridge.ico'
$options = @('/nologo', '/target:winexe', '/platform:anycpu', '/langversion:5', '/codepage:65001', '/optimize+', '/warn:4', '/warnaserror+',
    '/reference:System.Core.dll', '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll',
    "/resource:$license,LICENSE", "/win32icon:$icon", "/resource:$icon,Bridge.ico")

[IO.Directory]::CreateDirectory($staging) | Out-Null
try {
    & $compiler @options /reference:System.IO.Compression.dll /reference:System.Net.Http.dll "/reference:$speech" "/out:$app" @sources
    if ($LASTEXITCODE -ne 0) { throw 'The app build failed.' }

    & $compiler @options "/out:$setup" "/resource:$app,BridgeApp.exe" $setupSource (Join-Path $PSScriptRoot 'src\BridgeRelease.cs') (Join-Path $PSScriptRoot 'src\BridgeWindow.cs')
    if ($LASTEXITCODE -ne 0) { throw 'The installer build failed.' }

    [IO.Directory]::CreateDirectory($destination) | Out-Null
    foreach ($file in @($app, $setup)) {
        $output = Join-Path $destination ([IO.Path]::GetFileName($file))
        [IO.File]::Copy($file, $output, $true)
        Write-Output $output
    }

    if ($Package) {
        Add-Type -AssemblyName System.IO.Compression
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $version = [Reflection.AssemblyName]::GetAssemblyName($app).Version.ToString(3)
        $zipName = "PC-OnSong-Bridge-$version.zip"
        $stagedZip = Join-Path $staging $zipName
        try {
            $zip = [IO.Compression.ZipFile]::Open($stagedZip, [IO.Compression.ZipArchiveMode]::Create)
            try {
                $files = @(Get-Item -LiteralPath $app, $setup)
                foreach ($name in '.gitignore','README.md','LICENSE','build.ps1','test.ps1') {
                    $files += Get-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Force
                }
                $files += Get-Item -LiteralPath $icon
                foreach ($dir in 'src','setup') {
                    $files += Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot $dir) -File -Filter '*.cs'
                }
                $files += Get-ChildItem -Path (Join-Path $PSScriptRoot 'tests\*\*.cs') -File
                $files += Get-ChildItem -Path (Join-Path $PSScriptRoot 'tests\*\run.ps1') -File
                foreach ($file in $files) {
                    $entry = $file.Name
                    if ($file.DirectoryName -ne $staging) {
                        $entry = $file.FullName.Substring($PSScriptRoot.Length + 1).Replace('\','/')
                    }
                    [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $entry)
                }
            } finally { $zip.Dispose() }
            $output = Join-Path $destination $zipName
            [IO.File]::Copy($stagedZip, $output, $true)
            Write-Output $output
        } finally { [IO.File]::Delete($stagedZip) }
    }
}
finally {
    [IO.File]::Delete($app)
    [IO.File]::Delete($setup)
    [IO.Directory]::Delete($staging)
}
