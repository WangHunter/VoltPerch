param(
    [string]$Dotnet = 'dotnet',
    [string]$Version = '0.3.0',
    [switch]$NoRestore
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $projectRoot 'artifacts\voltperch-publish'
$release = Join-Path $projectRoot 'artifacts\release'
$publishArgs = @('publish', (Join-Path $projectRoot 'src\BatteryDesk\BatteryDesk.csproj'), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', $publish)
if ($NoRestore) { $publishArgs += '--no-restore' }
& $Dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw 'Publishing failed' }
Get-ChildItem -LiteralPath $publish -Filter '*.pdb' -File | Remove-Item
if (Test-Path -LiteralPath (Join-Path $publish 'data')) { throw 'Refusing to package a directory containing user data' }
foreach ($name in @('README.txt','README.md','CHANGELOG.md','LICENSE','THIRD_PARTY_NOTICES.txt')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination $publish -Force
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'licenses') -Destination $publish -Recurse -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'src\BatteryDesk\Assets\app.ico') -Destination $publish -Force
New-Item -ItemType Directory -Path $release -Force | Out-Null
$zip = Join-Path $release "VoltPerch-$Version-windows-x64.zip"
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip -Force
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($zip + '.sha256', "$hash  $([IO.Path]::GetFileName($zip))`n", [Text.UTF8Encoding]::new($false))
Write-Output "Created $zip"
Write-Output "SHA256 $hash"
