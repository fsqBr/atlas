#!/usr/bin/env bash
# Atlas backup (docker compose deployments): PostgreSQL dump + uploads volume + MinIO data, one timestamped folder.
# Usage: deploy/scripts/backup.sh [target-dir] (default ./backups)
# Restore with deploy/scripts/restore.sh <folder>.
# Needs: docker compose (project "atlas"), tar. Runs from the repository root (docker-compose.yml + .env).
set -euo pipefail
cd "$(dirname "$0")/../.."

target="${1:-./backups}/atlas-$(date -u +%Y%m%dT%H%M%SZ)"
mkdir -p "$target"
echo "▸ Backing up to $target"

# 1. Database — logical dump, custom format (compressed, restorable table by table).
docker compose exec -T atlas-postgres pg_dump -U atlas -d atlas -Fc > "$target/atlas.dump"
echo "  ✓ postgres dump $(du -h "$target/atlas.dump" | cut -f1)"

# 2. Uploads volume — archives behind "upload" assessments (needed for their re-runs).
docker compose run --rm --no-deps -T --entrypoint sh -v "$(pwd)/$target:/backup" atlas-api \
  -c 'cd /var/atlas/uploads && tar czf /backup/uploads.tgz . 2>/dev/null || tar czf /backup/uploads.tgz --files-from /dev/null'
echo "  ✓ uploads $(du -h "$target/uploads.tgz" | cut -f1)"

# 3. MinIO data (evidence/reports) — straight from the volume.
docker run --rm -v atlas_atlas-minio-data:/data:ro -v "$(pwd)/$target:/backup" alpine:3.20 tar czf /backup/minio.tgz -C /data .
echo "  ✓ minio $(du -h "$target/minio.tgz" | cut -f1)"

# 4. Configuration that is not in the database. .env holds ATLAS_MASTER_KEY: without it, stored credentials and the
# AI key in the dump are unreadable. Keep this file encrypted at rest.
cp .env "$target/env.backup"
chmod 600 "$target/env.backup"
echo "  ✓ .env (contains ATLAS_MASTER_KEY — protect this file)"

cat > "$target/MANIFEST.txt" <<EOF
atlas backup $(date -u +%FT%TZ)
postgres: atlas.dump (pg_dump -Fc)
uploads:  uploads.tgz (volume atlas_atlas-uploads, /var/atlas/uploads)
minio:    minio.tgz  (volume atlas_atlas-minio-data)
env:      env.backup (.env incl. ATLAS_MASTER_KEY)
not included: atlas-vulndata (OSV bundle, re-synced by the API), atlas-workspaces (clone cache)
EOF
echo "✓ Done: $target"
