<#
.SYNOPSIS
  Atlas CI gate for Windows agents: find-or-create the assessment, run it, wait, export SARIF, evaluate the gate.
.PARAMETER AtlasUrl       Atlas API base URL (or $env:ATLAS_URL)
.PARAMETER Token          Bearer token when OIDC is enabled (or $env:ATLAS_TOKEN)
.PARAMETER Tenant         Tenant external key for auth-off installs (or $env:ATLAS_TENANT)
.PARAMETER RepoUrl        Git URL Atlas can clone (default: git remote origin)
.PARAMETER Branch         Branch to assess (default: current)
.PARAMETER Kind           git | github | azure-devops | gitlab (default git)
.PARAMETER Credential     Stored Atlas credential name for private repos
.PARAMETER FailOn         Critical | High | Medium | Low
.PARAMETER MinScore       Minimum health score
.PARAMETER Sarif          Output path (default atlas.sarif)
.PARAMETER TimeoutSeconds Wait limit (default 1800)
.PARAMETER CommentPath    Write the pull-request comment (Markdown) here (or $env:ATLAS_PR_COMMENT)
.PARAMETER CommentAi      "true" adds a short AI paragraph to the comment (needs a configured provider)
.PARAMETER Lang           en | pt-BR for the comment (default en)
#>
param(
  [string]$AtlasUrl = $env:ATLAS_URL,
  [string]$Token = $env:ATLAS_TOKEN,
  [string]$Tenant = $env:ATLAS_TENANT,
  [string]$RepoUrl = $env:ATLAS_REPO_URL,
  [string]$Branch = $env:ATLAS_BRANCH,
  [string]$Kind = $(if ($env:ATLAS_KIND) { $env:ATLAS_KIND } else { "git" }),
  [string]$Credential = $env:ATLAS_CREDENTIAL,
  [string]$FailOn = $env:ATLAS_FAIL_ON,
  [string]$MinScore = $env:ATLAS_MIN_SCORE,
  [string]$Sarif = $(if ($env:ATLAS_SARIF) { $env:ATLAS_SARIF } else { "atlas.sarif" }),
  [int]$TimeoutSeconds = $(if ($env:ATLAS_TIMEOUT) { [int]$env:ATLAS_TIMEOUT } else { 1800 }),
  [string]$CommentPath = $env:ATLAS_PR_COMMENT,
  [string]$CommentAi = $(if ($env:ATLAS_PR_AI) { $env:ATLAS_PR_AI } else { "false" }),
  [string]$Lang = $(if ($env:ATLAS_LANG) { $env:ATLAS_LANG } else { "en" })
)
$ErrorActionPreference = "Stop"
if (-not $AtlasUrl) { throw "AtlasUrl / ATLAS_URL is required" }
$AtlasUrl = $AtlasUrl.TrimEnd("/")
if (-not $RepoUrl) { $RepoUrl = (git config --get remote.origin.url) }
if (-not $RepoUrl) { throw "RepoUrl / ATLAS_REPO_URL is required" }
if (-not $Branch) { $Branch = (git rev-parse --abbrev-ref HEAD); if (-not $Branch) { $Branch = "main" } }

$headers = @{ Accept = "application/json" }
if ($Token) { $headers.Authorization = "Bearer $Token" }
if ($Tenant) { $headers["X-Atlas-Tenant"] = $Tenant }

function Invoke-Atlas($Method, $Path, $Body = $null) {
  $params = @{ Method = $Method; Uri = "$AtlasUrl$Path"; Headers = $headers; ContentType = "application/json" }
  if ($null -ne $Body) { $params.Body = ($Body | ConvertTo-Json -Compress) }
  Invoke-RestMethod @params
}

Write-Host "▸ Atlas $AtlasUrl · $Kind $RepoUrl ($Branch)"

$id = $null
try {
  $enc = [uri]::EscapeDataString($RepoUrl)
  $existing = Invoke-Atlas GET "/api/assessments/by-locator?locator=$enc&kind=$Kind&branch=$([uri]::EscapeDataString($Branch))"
  $id = $existing.id
  Write-Host "▸ Assessment $id (existing) — queuing a run"
  try { Invoke-Atlas POST "/api/assessments/$id/runs" | Out-Null } catch { if ($_.Exception.Response.StatusCode.value__ -ne 409) { throw } }
} catch {
  if ($_.Exception.Response -and $_.Exception.Response.StatusCode.value__ -eq 404) {
    $name = [IO.Path]::GetFileNameWithoutExtension($RepoUrl.TrimEnd("/"))
    $body = @{ name = $name; sourceKind = $Kind; sourceLocator = $RepoUrl; branch = $Branch }
    if ($Credential) { $body.credentialName = $Credential }
    $created = Invoke-Atlas POST "/api/assessments" $body
    $id = $created.id
    Write-Host "▸ Assessment $id created — first run queued"
  } else { throw }
}

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
while ($true) {
  $a = Invoke-Atlas GET "/api/assessments/$id"
  if (($a.status -eq "Completed" -or $a.status -eq "CompletedWithWarnings") -and -not $a.activeJobState) { Write-Host "▸ Run finished ($($a.status))"; break }
  if ($a.status -eq "Failed" -and -not $a.activeJobState) { Write-Host "✗ Run failed: $($a.failureReason)"; exit 3 }
  if ((Get-Date) -gt $deadline) { Write-Host "✗ Timed out after ${TimeoutSeconds}s"; exit 4 }
  Start-Sleep -Seconds 10
}

Invoke-WebRequest -Uri "$AtlasUrl/api/assessments/$id/findings/export?format=sarif" -Headers $headers -OutFile $Sarif | Out-Null
Write-Host "▸ SARIF written to $Sarif"

$q = "x=1"
if ($FailOn) { $q += "&failOn=$FailOn" }
if ($MinScore) { $q += "&minScore=$MinScore" }
$gate = Invoke-Atlas GET "/api/assessments/$id/gate?$q"
Write-Host "▸ Health score: $($gate.score)"
foreach ($v in $gate.violations) { Write-Host "  ✗ $v" }

if ($CommentPath) {
  Invoke-WebRequest -Uri "$AtlasUrl/api/assessments/$id/pr-comment?lang=$Lang&ai=$CommentAi&$q" -Headers $headers -OutFile $CommentPath | Out-Null
  Write-Host "▸ PR comment written to $CommentPath"
}

if ($env:TF_BUILD) {
  Write-Host "##vso[task.setvariable variable=AtlasAssessmentId;isOutput=true]$id"
  Write-Host "##vso[task.setvariable variable=AtlasScore;isOutput=true]$($gate.score)"
  Write-Host "##vso[task.setvariable variable=AtlasPassed;isOutput=true]$($gate.passed)"
}
if ($env:GITHUB_OUTPUT) { "assessment-id=$id`nscore=$($gate.score)`npassed=$($gate.passed)`nsarif=$Sarif`ncomment=$CommentPath" | Add-Content $env:GITHUB_OUTPUT }

if ($gate.passed) { Write-Host "✓ Atlas gate passed" } else { Write-Host "✗ Atlas gate failed"; exit 1 }
