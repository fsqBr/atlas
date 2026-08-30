<#
.SYNOPSIS  Atlas backup for docker compose deployments on Windows: PostgreSQL dump + uploads + MinIO + .env.
.EXAMPLE   .\deploy\scripts\backup.ps1 -Target E:\Backups\atlas
#>
param([string]$Target = ".\backups")
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..\..")
$stamp = (Get-Date).ToUniversalTime().ToString("yyyyMMddTHHmmssZ")
$dir = Join-Path $Target "atlas-$stamp"
New-Item -ItemType Directory -Force $dir | Out-Null
$abs = (Resolve-Path $dir).Path -replace "\\", "/"
Write-Host "▸ Backing up to $dir"

docker compose exec -T atlas-postgres pg_dump -U atlas -d atlas -Fc | Set-Content -AsByteStream (Join-Path $dir "atlas.dump")
Write-Host "  ✓ postgres dump"

$env:MSYS_NO_PATHCONV = "1"
docker compose run --rm --no-deps -T --entrypoint sh -v "${abs}:/backup" atlas-api -c "cd /var/atlas/uploads && tar czf /backup/uploads.tgz ." | Out-Null
Write-Host "  ✓ uploads"
docker run --rm -v atlas_atlas-minio-data:/data:ro -v "${abs}:/backup" alpine:3.20 tar czf /backup/minio.tgz -C /data . | Out-Null
Write-Host "  ✓ minio"

Copy-Item .env (Join-Path $dir "env.backup")
Write-Host "  ✓ .env (contains ATLAS_MASTER_KEY — protect this file)"
@"
atlas backup $((Get-Date).ToUniversalTime().ToString("o"))
postgres: atlas.dump (pg_dump -Fc)
uploads:  uploads.tgz (volume atlas_atlas-uploads)
minio:    minio.tgz  (volume atlas_atlas-minio-data)
env:      env.backup (.env incl. ATLAS_MASTER_KEY)
not included: atlas-vulndata (re-synced), atlas-workspaces (cache)
"@ | Set-Content (Join-Path $dir "MANIFEST.txt")
Write-Host "✓ Done: $dir"
