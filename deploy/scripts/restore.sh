#!/usr/bin/env bash
# Atlas restore (docker compose deployments) from a folder produced by backup.sh.
# Usage: deploy/scripts/restore.sh <backup-folder>
# Stops api/worker, restores the database (drop + recreate schema), uploads and MinIO data, then starts everything.
set -euo pipefail
cd "$(dirname "$0")/../.."
src="${1:?backup folder required}"
[[ -f "$src/atlas.dump" ]] || { echo "No atlas.dump in $src"; exit 1; }

echo "▸ Restoring from $src"
if [[ -f "$src/env.backup" && ! -f .env ]]; then cp "$src/env.backup" .env; echo "  ✓ .env restored"; fi

docker compose stop atlas-api atlas-worker atlas-web >/dev/null
docker compose up -d atlas-postgres atlas-object-storage >/dev/null
for i in $(seq 1 30); do docker compose exec -T atlas-postgres pg_isready -U atlas -d atlas >/dev/null 2>&1 && break; sleep 2; done

# 1. Database: recreate and restore. --clean drops objects that exist; --if-exists keeps it idempotent.
docker compose exec -T atlas-postgres pg_restore -U atlas -d atlas --clean --if-exists --no-owner --no-privileges < "$src/atlas.dump" \
  || echo "  ! pg_restore reported errors (usually harmless 'does not exist' notices on a fresh database)"
echo "  ✓ database"

# 2. Uploads.
if [[ -f "$src/uploads.tgz" ]]; then
  docker compose run --rm --no-deps -T --entrypoint sh -v "$(cd "$src" && pwd):/backup" -u root atlas-api \
    -c 'rm -rf /var/atlas/uploads/* && tar xzf /backup/uploads.tgz -C /var/atlas/uploads && chown -R app:app /var/atlas/uploads'
  echo "  ✓ uploads"
fi

# 3. MinIO.
if [[ -f "$src/minio.tgz" ]]; then
  docker compose stop atlas-object-storage >/dev/null
  docker run --rm -v atlas_atlas-minio-data:/data -v "$(cd "$src" && pwd):/backup:ro" alpine:3.20 sh -c 'rm -rf /data/* && tar xzf /backup/minio.tgz -C /data'
  echo "  ✓ minio"
fi

docker compose up -d >/dev/null
echo "✓ Restore complete — the API applies any pending migrations on start."
