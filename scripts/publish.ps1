[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) {
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
}

$artifactRoot = Join-Path $projectRoot 'artifacts\win-x64'
$runnerPublish = Join-Path $artifactRoot 'runner-publish'
$appPublish = Join-Path $artifactRoot 'RelayLoop-portable'

New-Item -ItemType Directory -Force -Path $runnerPublish, $appPublish | Out-Null

& $dotnet publish (Join-Path $projectRoot 'src\RelayLoop.Runner\RelayLoop.Runner.csproj') `
    --configuration $Configuration --runtime win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:PublishTrimmed=false --output $runnerPublish --nologo
if ($LASTEXITCODE -ne 0) { throw 'Runner publish failed.' }

& $dotnet publish (Join-Path $projectRoot 'src\RelayLoop.App\RelayLoop.App.csproj') `
    --configuration $Configuration --runtime win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:PublishTrimmed=false --output $appPublish --nologo
if ($LASTEXITCODE -ne 0) { throw 'Application publish failed.' }

$stubDirectory = Join-Path $appPublish 'RunnerStub'
New-Item -ItemType Directory -Force -Path $stubDirectory | Out-Null
Copy-Item -LiteralPath (Join-Path $runnerPublish 'RelayLoop.Runner.exe') `
    -Destination (Join-Path $stubDirectory 'RelayLoop.Runner.exe') -Force

$sourceReadme = Join-Path $projectRoot 'README.md'
$sourcePrivacy = Join-Path $projectRoot 'PRIVACY.md'
$sourceNotices = Join-Path $projectRoot 'THIRD_PARTY_NOTICES.md'
foreach ($document in @($sourceReadme, $sourcePrivacy, $sourceNotices)) {
    if (Test-Path -LiteralPath $document) {
        Copy-Item -LiteralPath $document -Destination $appPublish -Force
    }
}

Write-Host "Portable RelayLoop release: $appPublish"
