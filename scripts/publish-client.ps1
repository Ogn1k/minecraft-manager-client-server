[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repositoryRoot 'src/MinecraftManager.App/MinecraftManager.App.csproj'
$outputRoot = Join-Path $repositoryRoot 'artifacts/client'
$runtimeIdentifiers = @('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64')

foreach ($runtimeIdentifier in $runtimeIdentifiers) {
    $output = Join-Path $outputRoot $runtimeIdentifier
    dotnet publish $project --configuration $Configuration --runtime $runtimeIdentifier --self-contained true --output $output -p:PublishSingleFile=false
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $runtimeIdentifier." }
    New-Item -ItemType Directory -Path (Join-Path $output 'updates') -Force | Out-Null
}

Get-ChildItem -LiteralPath $outputRoot -Recurse -File |
    Where-Object Name -ne 'SHA256SUMS.txt' |
    Get-FileHash -Algorithm SHA256 |
    Sort-Object Path |
    ForEach-Object { "{0}  {1}" -f $_.Hash.ToLowerInvariant(), $_.Path.Substring($outputRoot.Length + 1) } |
    Set-Content -LiteralPath (Join-Path $outputRoot 'SHA256SUMS.txt') -Encoding utf8
