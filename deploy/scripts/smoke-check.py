"""End-to-end smoke check of a running Atlas (ideally a fresh install). Usage: python deploy/scripts/smoke-check.py http://localhost:8080 [bearer-token]
Needs the bundled sample mounted as /sources/legacy-shop (ATLAS_LOCAL_SOURCES=./tests/Atlas.IntegrationTests/Corpus).
Exercises the core product (assessment run, findings, reports, portfolio, rules, exports, waivers) and the AI Estate
module (allowlist, prices, usage, OTLP, budgets, teams, reconciliation). Prints PASS/FAIL per check and a summary."""
import json, sys, time, urllib.request, urllib.error
sys.stdout.reconfigure(encoding="utf-8")

BASE = sys.argv[1].rstrip("/")
TOKEN = sys.argv[2] if len(sys.argv) > 2 else None
results = []


def call(method, path, body=None, headers=None, raw=False, timeout=60):
    data = None
    h = {"Accept": "application/json"}
    if body is not None:
        data = body if isinstance(body, (bytes, bytearray)) else json.dumps(body).encode()
        h["Content-Type"] = "application/json"
    if TOKEN:
        h["Authorization"] = f"Bearer {TOKEN}"
    if headers:
        h.update(headers)
    req = urllib.request.Request(BASE + path, data=data, method=method, headers=h)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            content = r.read()
            if raw:
                return r.status, content
            try:
                return r.status, (json.loads(content) if content else None)
            except ValueError:
                return r.status, content[:200]
    except urllib.error.HTTPError as e:
        content = e.read()
        try:
            return e.code, json.loads(content) if content else None
        except Exception:
            return e.code, content[:200]


def check(name, ok, detail=""):
    results.append((name, bool(ok), detail))
    print(("PASS " if ok else "FAIL ") + name + (f"  — {detail}" if detail else ""), flush=True)
    return ok


# ---- health & metadata ----
s, _ = call("GET", "/health/ready")
check("health/ready", s == 200, str(s))
s, info = call("GET", "/api/auth/config") if True else (0, None)
check("auth config readable", s in (200, 404), str(s))

# ---- assessment: create, run, wait ----
s, created = call("POST", "/api/assessments", {"name": "Deep check legacy-shop", "sourceKind": "local", "sourceLocator": "/sources/legacy-shop", "branch": None})
check("create assessment (local sample)", s in (200, 201, 202) and created and "id" in created, f"{s} {created if s >= 400 else ''}")
aid = created["id"] if created and "id" in created else None
status = None
if aid:
    for _ in range(120):
        s, a = call("GET", f"/api/assessments/{aid}")
        status = (a or {}).get("status") or (a or {}).get("lastRunStatus")
        runs = call("GET", f"/api/assessments/{aid}/runs")[1] or []
        if runs and isinstance(runs, list) and runs[0].get("status") in ("Completed", "Failed", "Succeeded"):
            status = runs[0].get("status")
            break
        time.sleep(3)
    check("run completes", status in ("Completed", "Succeeded"), f"status={status}")
    s, findings = call("GET", f"/api/assessments/{aid}/findings?pageSize=500")
    items = findings.get("items") if isinstance(findings, dict) else findings
    n = len(items) if isinstance(items, list) else (findings.get("total") if isinstance(findings, dict) else 0)
    check("findings present", s == 200 and (n or 0) > 10, f"{s} count={n}")
    rules_hit = {f.get("ruleId") for f in (items or []) if isinstance(f, dict)}
    for fam in ("sec.", "privacy.", "quality.", "dependency.", "database.", "java."):
        check(f"finding family {fam}*", any(r and r.startswith(fam) for r in rules_hit), "")
    s, html = call("GET", f"/api/assessments/{aid}/report?lang=en", raw=True)
    check("executive report HTML", s == 200 and b"<html" in html.lower() and b"AI estate" in html, f"{s} {len(html)}B")
    s, pt = call("GET", f"/api/assessments/{aid}/report?lang=pt-BR", raw=True)
    check("executive report pt-BR", s == 200 and (b"Sa\xc3\xbade" in pt or b"Resumo" in pt or b"IA" in pt), str(s))
    s, pdf = call("GET", f"/api/assessments/{aid}/report.pdf?lang=en", raw=True, timeout=120)
    check("executive report PDF (gotenberg sidecar)", s == 200 and pdf and pdf[:4] == b"%PDF", f"{s} {len(pdf or b'')}B")
    sarif_log = {"version": "2.1.0", "$schema": "https://json.schemastore.org/sarif-2.1.0.json", "runs": [{"tool": {"driver": {"name": "deep-check", "rules": [{"id": "DC001", "shortDescription": {"text": "Deep check rule"}}]}}, "results": [{"ruleId": "DC001", "level": "warning", "message": {"text": "external finding"}, "locations": [{"physicalLocation": {"artifactLocation": {"uri": "Shop.Web/Default.aspx.cs"}, "region": {"startLine": 1}}}]}]}]}
    s, sarif = call("POST", f"/api/assessments/{aid}/sarif", sarif_log)
    check("SARIF import accepts a valid log", s in (200, 202), f"{s} {sarif if s >= 400 else ''}")
    s, _ = call("POST", f"/api/assessments/{aid}/sarif", {"nope": 1})
    check("SARIF import rejects an invalid log", s == 400, str(s))
    s, csv = call("GET", f"/api/assessments/{aid}/findings/export", raw=True)
    check("CSV export", s == 200 and len(csv) > 100, str(s))
    s, ai = call("GET", f"/api/assessments/{aid}/ai-estate?lang=en")
    check("assessment AI estate (scanned, no AI in this corpus)", s == 200 and ai and ai.get("scanned") is True, f"{s} record={'present' if (ai or {}).get('record') else 'none'}")
    s, mod = call("GET", f"/api/assessments/{aid}/modernization?lang=en")
    check("modernization plan", s == 200, str(s))
    s, sup = call("GET", f"/api/assessments/{aid}/suppressions")
    check("suppressions list", s == 200 and isinstance(sup, list), str(s))
    s, mig = call("GET", f"/api/assessments/{aid}/suppressions/migratable")
    check("migratable waivers", s == 200 and isinstance(mig, list), str(s))
    # triage one finding as false positive, then a policy
    if items:
        fid = items[0].get("id")
        s, _ = call("POST", f"/api/assessments/{aid}/findings/{fid}/triage", {"action": "FalsePositive", "reason": "deep check", "author": "check"})
        check("triage a finding", s in (200, 204), str(s))
    s, pol = call("POST", f"/api/assessments/{aid}/policies", {"rulePattern": "quality.tests.none", "pathGlob": None, "reason": "deep check", "author": "check"})
    check("create suppression policy", s in (200, 201), str(s))
    s, cmp_runs = call("GET", f"/api/assessments/{aid}/runs")
    check("runs list", s == 200 and isinstance(cmp_runs, list) and len(cmp_runs) >= 1, str(s))

# ---- portfolio, dashboard, rules ----
s, p = call("GET", "/api/portfolio?lang=en")
check("portfolio summary", s == 200 and p and "assessments" in json.dumps(p).lower(), str(s))
s, rep = call("GET", "/api/portfolio/report?lang=en", raw=True)
check("portfolio report HTML", s == 200 and b"<html" in rep.lower(), str(s))
s, rules = call("GET", "/api/rules?lang=en")
check("rule catalog", s == 200 and rules and len(rules if isinstance(rules, list) else rules.get("items", [])) > 100, str(s))
s, q = call("GET", "/api/jobs")
check("jobs queue", s == 200 and isinstance(q, list), str(s))
s, met = call("GET", "/metrics", raw=True)
check("prometheus metrics", s == 200 and b"atlas_" in met, str(s))

# ---- AI estate module ----
s, est = call("GET", "/api/ai-estate?lang=en")
check("portfolio AI estate (204 = no AI detected in this corpus)", s in (200, 204), str(s))
s, cat = call("GET", "/api/ai-estate/catalog/providers")
check("signature catalog providers", s == 200 and len(cat) > 30, f"{s} {len(cat) if cat else 0}")
s, al = call("PUT", "/api/ai-estate/allowlist", {"approvedProviders": ["anthropic", "azure-openai"], "author": "check"})
check("save allowlist", s == 200 and al.get("source") == "tenant", str(s))
s, al2 = call("GET", "/api/ai-estate/allowlist")
check("read allowlist", s == 200 and al2.get("approvedProviders") == ["anthropic", "azure-openai"], str(s))
s, bad = call("PUT", "/api/ai-estate/allowlist", {"approvedProviders": ["nope"]})
check("allowlist rejects unknown provider", s == 400, str(s))
s, prices = call("GET", "/api/ai-estate/usage/prices")
check("price catalog", s == 200 and len(prices.get("prices", [])) > 20, str(s))
s, pr = call("PUT", "/api/ai-estate/usage/prices", {"pattern": "^deep-check-model$", "input": 1, "output": 2, "author": "check"})
check("tenant price upsert", s == 200 and pr.get("source") == "tenant", str(s))
s, _ = call("PUT", "/api/ai-estate/usage/prices", {"pattern": "^(", "input": 1, "output": 2})
check("price rejects bad regex", s == 400, str(s))
today = time.strftime("%Y-%m-%d", time.gmtime())
s, rep = call("POST", "/api/ai-estate/usage/report", {"actor": "check-dev", "tool": "claude-code", "agentVersion": "check", "entries": [{"period": today, "model": "deep-check-model", "inputTokens": 1000000, "outputTokens": 500000, "cacheReadTokens": 0, "cacheWriteTokens": 0, "requests": 3, "sessions": 1}]})
check("usage report ingest", s == 200 and rep.get("accepted") == 1 and rep.get("estimatedCost") == 2, f"{s} {rep}")
s, live = call("GET", "/api/ai-estate/usage/live")
check("live usage", s == 200 and any(a["actor"] == "check-dev" and a["activeNow"] for a in live.get("actors", [])), str(s))
s, usage = call("GET", "/api/ai-estate/usage?days=30")
check("usage summary + forecast", s == 200 and usage.get("forecast") and usage["forecast"].get("cumulativeByDay") is not None and usage.get("byProvider") is not None, str(s))
now_ns = int(time.time() * 1e9)
otlp = {"resourceMetrics": [{"resource": {"attributes": [{"key": "service.name", "value": {"stringValue": "claude-code"}}]}, "scopeMetrics": [{"metrics": [{"name": "claude_code.token.usage", "sum": {"aggregationTemporality": 1, "isMonotonic": True, "dataPoints": [{"attributes": [{"key": "type", "value": {"stringValue": "input"}}, {"key": "model", "value": {"stringValue": "claude-sonnet-4-5"}}, {"key": "user.email", "value": {"stringValue": "check@example.com"}}], "timeUnixNano": str(now_ns), "asInt": "2000000"}]}}]}]}]}
s, o = call("POST", "/api/ai-estate/otlp/v1/metrics", otlp)
check("OTLP metrics ingest", s == 200 and o.get("atlas", {}).get("accepted") == 1, f"{s} {o}")
s, o2 = call("POST", "/api/ai-estate/otlp/v1/metrics", b"\x01\x02", headers={"Content-Type": "application/x-protobuf"})
check("OTLP protobuf → 415 hint", s == 415, str(s))
s, usage = call("GET", "/api/ai-estate/usage?days=30")
otel_actor = next((a["actor"] for a in usage.get("actors", []) if a["actor"].startswith("id-")), None)
check("telemetry actor pseudonymised", otel_actor is not None and "example.com" not in (otel_actor or ""), str(otel_actor))
run_id = time.strftime("%H%M%S")
s, team = call("PUT", "/api/ai-estate/teams", {"name": f"Check team {run_id}", "members": ["check-dev", otel_actor or "x"]})
check("team upsert", s == 200 and team.get("id"), str(s))
s, actors = call("GET", "/api/ai-estate/teams/actors")
check("known actors", s == 200 and any(a["actor"] == "check-dev" and (a["team"] or "").startswith("Check team") for a in actors), str(s))
s, b = call("PUT", "/api/ai-estate/budgets", {"scope": "team", "scopeKey": f"Check team {run_id}", "monthlyAmount": 1, "name": f"Check budget {run_id}"})
check("budget upsert + immediate status", s == 200 and b.get("state") == "over", f"{s} state={(b or {}).get('state')} spent={(b or {}).get('spentMonthToDate')}")
s, alerts = call("GET", "/api/ai-estate/budgets/alerts?days=1")
check("budget alerts raised (50/80/100)", s == 200 and len([a for a in alerts if a["kind"] == "threshold" and a.get("budgetId") == b.get("id")]) == 3, f"{s} n={len(alerts) if alerts else 0}")
s, _ = call("PUT", "/api/ai-estate/budgets", {"scope": "galaxy", "monthlyAmount": 1})
check("budget rejects unknown scope", s == 400, str(s))
s, rec = call("GET", "/api/ai-estate/usage/reconciliation?days=30")
check("reconciliation", s == 200 and isinstance(rec.get("providers"), list) and any(p["provider"] == "anthropic" for p in rec["providers"]), str(s))
s, cs = call("GET", "/api/ai-estate/cost/providers")
check("cost providers include cursor", s == 200 and "cursor" in cs, str(cs))
s, _ = call("PUT", "/api/ai-estate/cost/sources/openai", {"credentialName": "missing"})
check("cost source needs an existing credential", s == 400, str(s))
s, cred = call("PUT", "/api/credentials/check-key", {"secret": "sk-admin-not-real", "description": "deep check"})
check("store a credential (encrypted)", s in (200, 201, 204), str(s))
s, src = call("PUT", "/api/ai-estate/cost/sources/openai", {"credentialName": "check-key"})
check("connect a cost source", s == 200 and src.get("provider") == "openai", str(s))
s, sync = call("POST", "/api/ai-estate/cost/sync", {"days": 7}, timeout=120)
check("cost sync isolates a failing provider", s == 200 and isinstance(sync, list) and len(sync) == 1 and sync[0]["succeeded"] is False and sync[0].get("error"), f"{s} {sync}")
s, _ = call("DELETE", "/api/ai-estate/cost/sources/openai")
check("disconnect cost source", s == 204, str(s))
s, toks = call("POST", "/api/tokens", {"name": "check-analyst", "role": "analyst"})
check("create analyst API token", s in (200, 201) and toks and toks.get("secret", "").startswith("atlas_pat_"), str(s))
s, toka = call("POST", "/api/tokens", {"name": "check-admin", "role": "admin"})
check("create admin API token", s in (200, 201), str(s))
if toks and toka:
    json.dump({"analyst": toks["secret"], "admin": toka["secret"], "assessment": aid, "team": team.get("id") if team else None}, open("smoke-check.tokens.json", "w"))
    print("tokens written to smoke-check.tokens.json (delete after use)")

# ---- summary ----
failed = [r for r in results if not r[1]]
print(f"\n{len(results) - len(failed)}/{len(results)} checks passed")
for name, ok, detail in failed:
    print("  FAILED:", name, detail)
sys.exit(1 if failed else 0)
