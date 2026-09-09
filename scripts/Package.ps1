$ErrorActionPreference = 'Stop'
[xml]$project = Get-Content src/SafeFileSync.App/SafeFileSync.App.csproj
$version = [string]$project.Project.PropertyGroup.Version
$commit = (git rev-parse HEAD).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+-alpha\.\d+$') { throw 'Unexpected prerelease version.' }
Copy-Item README.md,LICENSE -Destination artifacts/app
Copy-Item docs -Destination artifacts/app -Recurse
$info = @{ version=$version; commit=$commit; runtime='win-x64'; buildUtc=[DateTime]::UtcNow.ToString('O') } | ConvertTo-Json
$info | Set-Content artifacts/app/build-info.json -Encoding utf8
New-Item -ItemType Directory -Path artifacts/package -Force | Out-Null
$name = "SafeFileSync-v$version-win-x64.zip"
Compress-Archive -Path artifacts/app/* -DestinationPath "artifacts/package/$name" -CompressionLevel Optimal
$hash = (Get-FileHash "artifacts/package/$name" -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $name" | Set-Content artifacts/package/SHA256SUMS -Encoding ascii
Copy-Item artifacts/app/build-info.json artifacts/package/build-info.json
Expand-Archive "artifacts/package/$name" -DestinationPath artifacts/package-check
./scripts/Smoke-App.ps1 -Executable artifacts/package-check/SafeFileSync.App.exe
$readback = Get-Content artifacts/package-check/build-info.json | ConvertFrom-Json
if ($readback.commit -ne $commit -or $readback.version -ne $version) { throw 'Packaged build metadata differs.' }
