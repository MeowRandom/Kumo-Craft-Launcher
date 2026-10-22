param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$projectDirectory = $PSScriptRoot
$publishDirectory = Join-Path $projectDirectory "publish"
$artifactName = "Kumo Craft Launcher.exe"

dotnet restore (Join-Path $projectDirectory "Kumo.csproj") `
    -r win-x64 `
    --configfile (Join-Path $projectDirectory "NuGet.Config")
dotnet build (Join-Path $projectDirectory "Kumo.csproj") `
    -c $Configuration --no-restore
dotnet publish (Join-Path $projectDirectory "Kumo.csproj") `
    -c $Configuration --no-restore -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:PublishTrimmed=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $publishDirectory

$publishedExecutable = Join-Path $publishDirectory "Kumo.exe"
$artifactPath = Join-Path $publishDirectory $artifactName
Move-Item -LiteralPath $publishedExecutable -Destination $artifactPath -Force

Write-Host ""
Write-Host "Kumo built successfully:" -ForegroundColor Green
Write-Host $artifactPath
