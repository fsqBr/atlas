#!/usr/bin/env bash
# Atlas CI gate: find-or-create the assessment for this repository, run it, wait, export SARIF, evaluate the gate.
# Requires: curl, jq. Environment / flags:
# ATLAS_URL Atlas API base URL (e.g. https://atlas.example.com) [required]
# ATLAS_TOKEN Bearer token when OIDC is enabled [optional]
# ATLAS_TENANT Tenant external key (auth-off installs only) [optional]
# ATLAS_REPO_URL Git URL Atlas can clone (default: git remote origin) [optional]
# ATLAS_BRANCH Branch to assess (default: current) [optional]
# ATLAS_KIND Source kind: git | github | azure-devops | gitlab (default git)
# ATLAS_CREDENTIAL Name of a stored Atlas credential for private repos [optional]
# ATLAS_FAIL_ON Fail when open findings at/above this severity exist (Critical|High|Medium|Low)
# ATLAS_MIN_SCORE Fail when health score is below this number
# ATLAS_SARIF Where to write the SARIF file (default atlas.sarif)
# ATLAS_TIMEOUT Seconds to wait for the run (default 1800)
# ATLAS_PR_COMMENT Write the pull-request comment (Markdown) to this file [optional]
# ATLAS_PR_AI true to add a short AI paragraph to the comment (needs a provider) [optional]
# ATLAS_LANG en | pt-BR for the comment (default en)
set -euo pipefail

: "${ATLAS_URL:?ATLAS_URL is required}"
ATLAS_URL="${ATLAS_URL%/}"
ATLAS_KIND="${ATLAS_KIND:-git}"
ATLAS_SARIF="${ATLAS_SARIF:-atlas.sarif}"
ATLAS_TIMEOUT="${ATLAS_TIMEOUT:-1800}"
ATLAS_REPO_URL="${ATLAS_REPO_URL:-$(git config --get remote.origin.url 2>/dev/null || true)}"
ATLAS_BRANCH="${ATLAS_BRANCH:-$(git rev-parse --abbrev-ref HEAD 2>/dev/null || echo main)}"
: "${ATLAS_REPO_URL:?ATLAS_REPO_URL is required (no git remote found)}"

hdr=(-H "Accept: application/json" -H "Content-Type: application/json")
[[ -n "${ATLAS_TOKEN:-}" ]] && hdr+=(-H "Authorization: Bearer ${ATLAS_TOKEN}")
[[ -n "${ATLAS_TENANT:-}" ]] && hdr+=(-H "X-Atlas-Tenant: ${ATLAS_TENANT}")

api() { curl -sS --fail-with-body "${hdr[@]}" "$@"; }

echo "▸ Atlas ${ATLAS_URL} · ${ATLAS_KIND} ${ATLAS_REPO_URL} (${ATLAS_BRANCH})"

# 1. Find or create the assessment for this repository.
existing=$(api -G "${ATLAS_URL}/api/assessments/by-locator" --data-urlencode "locator=${ATLAS_REPO_URL}" --data-urlencode "kind=${ATLAS_KIND}" --data-urlencode "branch=${ATLAS_BRANCH}" -w '\n%{http_code}' || true)
code=$(echo "$existing" | tail -n1); body=$(echo "$existing" | sed '$d')
if [[ "$code" == "200" ]]; then
  id=$(echo "$body" | jq -r .id)
  echo "▸ Assessment ${id} (existing) — queuing a run"
  run=$(api -X POST "${ATLAS_URL}/api/assessments/${id}/runs" -w '\n%{http_code}' || true)
  rcode=$(echo "$run" | tail -n1)
  [[ "$rcode" == "202" || "$rcode" == "409" ]] || { echo "Could not queue run (HTTP ${rcode}): $(echo "$run" | sed '$d')"; exit 2; }
else
  name=$(basename -s .git "${ATLAS_REPO_URL}")
  payload=$(jq -n --arg n "$name" --arg k "$ATLAS_KIND" --arg l "$ATLAS_REPO_URL" --arg b "$ATLAS_BRANCH" --arg c "${ATLAS_CREDENTIAL:-}" \
    '{name:$n, sourceKind:$k, sourceLocator:$l, branch:$b} + (if $c != "" then {credentialName:$c} else {} end)')
  created=$(api -X POST "${ATLAS_URL}/api/assessments" -d "$payload")
  id=$(echo "$created" | jq -r .id)
  echo "▸ Assessment ${id} created — first run queued"
fi

# 2. Wait for the run to finish.
deadline=$((SECONDS + ATLAS_TIMEOUT))
while :; do
  status=$(api "${ATLAS_URL}/api/assessments/${id}" | jq -r '.status + ":" + (.activeJobState // "")')
  case "$status" in
    Completed:|CompletedWithWarnings:) echo "▸ Run finished (${status%:})"; break ;;
    Failed:) echo "✗ Run failed"; api "${ATLAS_URL}/api/assessments/${id}" | jq -r '.failureReason // empty'; exit 3 ;;
  esac
  (( SECONDS < deadline )) || { echo "✗ Timed out after ${ATLAS_TIMEOUT}s (status ${status})"; exit 4; }
  sleep 10
done

# 3. SARIF for code-scanning upload.
api "${ATLAS_URL}/api/assessments/${id}/findings/export?format=sarif" -o "${ATLAS_SARIF}"
echo "▸ SARIF written to ${ATLAS_SARIF}"

# 4. Gate.
q=""
[[ -n "${ATLAS_FAIL_ON:-}" ]] && q="${q}&failOn=${ATLAS_FAIL_ON}"
[[ -n "${ATLAS_MIN_SCORE:-}" ]] && q="${q}&minScore=${ATLAS_MIN_SCORE}"
gate=$(api "${ATLAS_URL}/api/assessments/${id}/gate?x=1${q}")
passed=$(echo "$gate" | jq -r .passed)
score=$(echo "$gate" | jq -r '.score // "n/a"')
echo "▸ Health score: ${score}"
echo "$gate" | jq -r '.violations[]? | "  ✗ " + .'

# 5. Pull-request comment (Markdown), for the CI system to post.
if [[ -n "${ATLAS_PR_COMMENT:-}" ]]; then
  curl -sS --fail-with-body "${hdr[@]}" -H "Accept: text/markdown" \
    "${ATLAS_URL}/api/assessments/${id}/pr-comment?lang=${ATLAS_LANG:-en}&ai=${ATLAS_PR_AI:-false}${q}" -o "${ATLAS_PR_COMMENT}"
  echo "▸ PR comment written to ${ATLAS_PR_COMMENT}"
fi

if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
  { echo "assessment-id=${id}"; echo "score=${score}"; echo "passed=${passed}"; echo "sarif=${ATLAS_SARIF}"; echo "comment=${ATLAS_PR_COMMENT:-}"; } >> "$GITHUB_OUTPUT"
fi
if [[ -n "${TF_BUILD:-}" ]]; then
  echo "##vso[task.setvariable variable=AtlasAssessmentId;isOutput=true]${id}"
  echo "##vso[task.setvariable variable=AtlasScore;isOutput=true]${score}"
  echo "##vso[task.setvariable variable=AtlasPassed;isOutput=true]${passed}"
fi

if [[ "$passed" == "true" ]]; then echo "✓ Atlas gate passed"; else echo "✗ Atlas gate failed"; exit 1; fi
