param(
    [ValidateSet("win-x64")]
    [string]$Target = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

Write-Host "=== DreamLauncher Build Script ===" -ForegroundColor Cyan
Write-Host ""

function Publish-Project {
    param(
        [string]$ProjectPath,
        [string]$Framework,
        [string]$Runtime,
        [string]$OutputDir
    )

    $fullOutput = Join-Path $root $OutputDir
    Write-Host "Publishing: $ProjectPath" -ForegroundColor Yellow
    Write-Host "  Framework : $Framework"
    Write-Host "  Runtime   : $Runtime"
    Write-Host "  Output    : $fullOutput"
    Write-Host ""

    dotnet publish $ProjectPath `
        -c Release `
        -f $Framework `
        -r $Runtime `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=none `
        -p:DebugSymbols=false `
        --no-self-contained `
        -o $fullOutput

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $ProjectPath"
    }

    Write-Host "Done: $fullOutput" -ForegroundColor Green
    Write-Host ""
}

if ($Target -eq "win-x64") {
    Publish-Project `
        -ProjectPath "DreamLauncher/DreamLauncher.Windows/DreamLauncher.Windows.csproj" `
        -Framework "net10.0-windows" `
        -Runtime "win-x64" `
        -OutputDir "dist"
}

Write-Host "=== Build complete ===" -ForegroundColor Cyan
