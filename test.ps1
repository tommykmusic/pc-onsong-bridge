[CmdletBinding()]
param([string]$ScrcpyArchive)

$ErrorActionPreference = 'Stop'
foreach ($suite in 'route','voice','command','swipe','transport','wireless','discovery','ndi','connect','setup','version') {
    Write-Output "Running $suite tests"
    & (Join-Path $PSScriptRoot "tests\$suite\run.ps1")
}
Write-Output 'Running scrcpy package tests'
& (Join-Path $PSScriptRoot 'tests\package\run.ps1') -Archive $ScrcpyArchive
