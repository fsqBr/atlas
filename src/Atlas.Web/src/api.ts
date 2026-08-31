// Thin typed client over the Atlas API (same origin: nginx in Docker, Vite proxy in dev).
import { getAccessToken, isAuthEnabled, signIn } from "./auth";

export interface AssessmentSummary {
  id: string;
  name: string;
  sourceKind: string;
  sourceLocator: string;
  status: string;
  createdAtUtc: string;
  completedAtUtc: string | null;
  healthScore: number | null;
  riskLevel: string | null;
  openFindings: number | null;
  activeJobState: string | null;
}

export interface Scan {
  id: string;
  scannerId: string;
  scannerVersion: string;
  commitSha: string | null;
  status: string;
  error: string | null;
  findingsEmitted: number;
  findingsNew: number;
  findingsRecurring: number;
  findingsResolved: number;
  findingsRegressed: number;
  startedAtUtc: string;
  finishedAtUtc: string | null;
}

export interface Assessment {
  id: string;
  name: string;
  sourceKind: string;
  sourceLocator: string;
  branch: string | null;
  credentialName: string | null;
  excludePaths: string[];
  rerunEveryDays: number | null;
  webhookUrl: string | null;
  targetScore: number | null;
  targetDate: string | null;
  status: string;
  failureReason: string | null;
  createdAtUtc: string;
  startedAtUtc: string | null;
  completedAtUtc: string | null;
  scans: Scan[];
  /** Queued / Leased / Running while a (re)run is pending or in progress. */
  activeJobState: string | null;
  tags: string[] | null;
}

export interface Finding {
  id: string;
  ruleId: string;
  category: string;
  severity: string;
  status: string;
  origin: string;
  title: string;
  message: string | null;
  confidence: string | null;
  remediation: string | null;
  filePath: string | null;
  lineStart: number | null;
  lineEnd: number | null;
  symbol: string | null;
  scannerId: string | null;
  createdAtUtc: string;
  updatedAtUtc: string;
  suppression: { kind: string; reason: string; author: string; createdAtUtc: string } | null;
}

export interface TriageRequest {
  action: "Suppress" | "FalsePositive" | "Reopen";
  reason: string | null;
  author: string;
  /** Optional waiver end for Suppress: the finding reopens automatically after this instant. */
  expiresAtUtc?: string | null;
}

export interface CostProfile {
  currency: string;
  hourlyRate: number;
  teamSize: number;
  isDefault: boolean;
  updatedBy: string | null;
  updatedAtUtc: string | null;
}

export interface Paged<T> {
  items: T[];
  page: number;
  pageSize: number;
  total: number;
}

export interface HealthContributor {
  ruleId: string;
  count: number;
  points: number;
}

export interface HealthDimension {
  name: string;
  weight: number;
  score: number;
  penalty: number;
  contributors: HealthContributor[];
}

export interface Health {
  score: number;
  riskLevel: string;
  modelVersion: string;
  explanation: string;
  openFindings: number;
  projectCount: number;
  commitSha: string | null;
  createdAtUtc: string;
  dimensions: HealthDimension[];
}

export interface Credential {
  name: string;
  username: string | null;
  description: string | null;
  createdAtUtc: string;
  updatedAtUtc: string;
  lastUsedAtUtc: string | null;
  usedByAssessments: number;
}

export interface DiscoveredRepository {
  name: string;
  locator: string;
  kind: string;
  defaultBranch: string | null;
  archived: boolean;
  language: string | null;
  lastPushUtc: string | null;
  isPrivate: boolean;
}

export interface BrowseResult {
  roots: { path: string; label: string; exists: boolean }[];
  current: string | null;
  parent: string | null;
  entries: { name: string; path: string; hasDotNetProjects: boolean; hasSolution: boolean; isGitRepo: boolean }[];
}

export interface LocalSource {
  name: string;
  path: string;
  hasDotNetProjects: boolean;
}

export interface Run {
  id: string;
  number: number;
  commitSha: string | null;
  status: string;
  failureReason: string | null;
  startedAtUtc: string;
  finishedAtUtc: string | null;
  healthScore: number | null;
  openFindings: number | null;
  findingsNew: number;
  findingsRecurring: number;
  findingsResolved: number;
  findingsRegressed: number;
  scannersRun: number;
  scannersFailed: number;
}

export interface DimensionDelta {
  name: string;
  before: number | null;
  after: number;
  delta: number | null;
}

export interface RuleDelta {
  ruleId: string;
  title: string;
  category: string;
  maxSeverity: string;
  count: number;
  sampleLocations: string[];
}

export interface InventoryDelta {
  linesBefore: number;
  linesAfter: number;
  filesBefore: number;
  filesAfter: number;
  projectsBefore: number;
  projectsAfter: number;
}

export interface RunComparison {
  current: Run;
  previous: Run | null;
  sameCommit: boolean;
  healthDelta: number | null;
  dimensions: DimensionDelta[];
  resolved: RuleDelta[];
  new: RuleDelta[];
  regressed: RuleDelta[];
  inventory: InventoryDelta | null;
}

export interface RangeValue {
  optimistic: number;
  likely: number;
  conservative: number;
}

export interface EstimateInfo {
  modelVersion: string;
  effortHours: RangeValue;
  durationMonths: RangeValue;
  cost: RangeValue & { currency: string };
  confidence: string;
  confidenceLabel: string;
  breakdown: { key: string; label: string; hours: number; quantity: number }[];
  assumptions: { key: string; label: string; value: string }[];
}

export interface StrategyInfo {
  strategy: string;
  name: string;
  description: string;
  fitScore: number;
  risk: string;
  recommended: boolean;
  rationale: string[];
  prerequisites: string[];
  blockers: string[];
  benefits: string[];
  estimate: EstimateInfo;
  paybackMonths: number | null;
}

export interface SavingsInfo {
  modelVersion: string;
  items: { key: string; label: string; annualAmount: number; quantity: number }[];
  annualTotal: number;
  currency: string;
  assumptions: { key: string; label: string; value: string }[];
}

export interface RoadmapPhase {
  key: string;
  name: string;
  order: number;
  effortShare: number;
  effortHours: RangeValue;
  durationMonths: RangeValue;
  dependsOn: string[];
  dependsOnNames: string[];
  workItems: { key: string; label: string; quantity: number }[];
}

export interface ModernizationPlan {
  modelVersion: string;
  profile: {
    linesOfCode: number;
    projects: number;
    legacyFrameworkProjects: number;
    modernFrameworkProjects: number;
    unknownFrameworkProjects: number;
    legacyProjectFormat: number;
    prerequisiteBlockers: number;
    highBlockers: number;
    mediumBlockers: number;
    projectsWithBlockers: number;
    criticalSecurity: number;
    highSecurity: number;
    mediumSecurity: number;
    secretsFound: number;
    vulnerablePackages: number;
    hasTests: boolean;
    coverageLineRate: number | null;
    projectsWithoutTests: number;
    architectureCycles: number;
    tier: string | null;
  };
  recommended: string;
  recommendedName: string;
  strategies: StrategyInfo[];
  roadmap: { modelVersion: string; strategy: string; phases: RoadmapPhase[] };
  savings: SavingsInfo | null;
}

export interface Portfolio {
  assessments: number;
  assessed: number;
  averageScore: number | null;
  byRisk: Record<string, number>;
  lines: number;
  files: number;
  projects: number;
  legacyProjects: number;
  modernProjects: number;
  unknownProjects: number;
  frameworks: { framework: string; count: number; legacy: boolean }[];
  openFindings: number;
  openBySeverity: Record<string, number>;
  openByCategory: Record<string, number>;
  benchmark: { name: string; count: number; p25: number; p50: number; p75: number; best: number; worst: number }[];
  targets: Record<string, number>;
  topRules: { ruleId: string; title: string; category: string; maxSeverity: string; count: number; assessments: number }[];
  rows: {
    id: string;
    name: string;
    sourceKind: string;
    status: string;
    score: number | null;
    risk: string | null;
    openFindings: number | null;
    lines: number;
    projects: number;
    legacyProjects: number;
    completedAtUtc: string | null;
    percentile: number | null;
    targetScore: number | null;
    targetDate: string | null;
    targetStatus: string;
    tags: string[] | null;
  }[];
}

export interface PortfolioTrendPoint {
  date: string;
  averageScore: number | null;
  openFindings: number;
  assessed: number;
  dimensions: Record<string, number> | null;
}

export interface RuleCatalogEntry {
  id: string;
  scannerId: string;
  category: string;
  defaultSeverity: string;
  overrideSeverity: string | null;
  title: string;
  description: string;
  remediation: string | null;
  openFindings: number;
  assessments: number;
}

export interface SuppressionPolicy {
  id: string;
  assessmentId: string | null;
  rulePattern: string;
  pathGlob: string | null;
  reason: string;
  author: string;
  createdAtUtc: string;
}

export interface ActualOutcome {
  assessmentId: string;
  strategy: string;
  strategyName: string;
  actualHours: number;
  actualMonths: number | null;
  actualCost: number | null;
  currency: string;
  notes: string | null;
  recordedBy: string;
  recordedAtUtc: string;
}

export interface Calibration {
  points: number;
  meanRatio: number | null;
  medianRatio: number | null;
  recommendation: string;
  recommendationText: string;
  items: { assessmentId: string; assessmentName: string; strategy: string; strategyName: string; estimatedLikelyHours: number; actualHours: number; ratio: number; notes: string | null; recordedAtUtc: string }[];
}

export interface RuleGroup {
  ruleId: string;
  title: string;
  category: string;
  maxSeverity: string;
  count: number;
  sampleFiles: string[];
}

export interface HeatmapRow {
  folder: string;
  open: number;
  critical: number;
  high: number;
  medium: number;
  low: number;
  informational: number;
  files: number;
}

export interface Job {
  id: string;
  assessmentId: string;
  assessmentName: string | null;
  kind: string;
  state: string;
  attempt: number;
  error: string | null;
  queuedAtUtc: string;
  startedAtUtc: string | null;
  finishedAtUtc: string | null;
  leasedBy: string | null;
}

export interface FindingQuery {
  page?: number;
  pageSize?: number;
  severity?: string;
  category?: string;
  status?: string;
  ruleId?: string;
  search?: string;
  lang?: string;
}

(window as unknown as { __atlasToken?: () => string | null }).__atlasToken = () => getAccessToken();

async function http<T>(url: string, init?: RequestInit): Promise<T> {
  const token = getAccessToken();
  const auth: Record<string, string> = token ? { Authorization: `Bearer ${token}` } : {};
  const response = await fetch(url, { ...init, headers: { Accept: "application/json", ...auth, ...(init?.headers ?? {}) } });
  if (response.status === 401 && isAuthEnabled()) {
    void signIn();
    throw new Error("unauthorized");
  }
  if (response.status === 404) throw new Error("not-found");
  if (response.status === 204) return undefined as T;
  if (!response.ok) {
    let detail = response.statusText;
    try {
      const body = (await response.json()) as { error?: string };
      if (body.error) detail = body.error;
    } catch {
      /* no body */
    }
    throw new Error(detail);
  }
  return (await response.json()) as T;
}

export interface Me {
  name: string | null;
  tenantId: string | null;
  tenantName: string | null;
  isDefaultTenant: boolean;
  roles: string[];
}

export interface ComparisonSide {
  id: string;
  name: string;
  sourceKind: string;
  status: string;
  completedAtUtc: string | null;
  score: number | null;
  risk: string | null;
  dimensions: Record<string, number>;
  openFindings: number;
  openBySeverity: Record<string, number>;
  openByCategory: Record<string, number>;
  lines: number;
  files: number;
  projects: number;
  legacyProjects: number;
  uiFrameworks: Record<string, number>;
  recommendedStrategy: string | null;
  likelyHours: number | null;
  likelyCost: number | null;
  currency: string | null;
  targetScore: number | null;
  topRules: Record<string, number>;
}

export interface SideBySide {
  a: ComparisonSide;
  b: ComparisonSide;
  ruleDifferences: { ruleId: string; title: string; category: string; maxSeverity: string; countA: number; countB: number }[];
}

export interface AccessEntry {
  id: string;
  subject: string;
  subjectName: string | null;
  role: string;
  grantedBy: string;
  grantedAtUtc: string;
}

export interface AccessView {
  restricted: boolean;
  myRole: string | null;
  canManage: boolean;
  canEdit: boolean;
  entries: AccessEntry[];
}

export interface ApiToken {
  id: string;
  name: string;
  hint: string;
  role: string;
  createdBy: string;
  createdAtUtc: string;
  expiresAtUtc: string | null;
  lastUsedAtUtc: string | null;
  revokedAtUtc: string | null;
  active: boolean;
}

export interface ApiTokenCreated {
  token: ApiToken;
  secret: string;
}

export interface AiProviderInfo {
  id: string;
  defaultModel: string;
  defaultBaseUrl: string | null;
  requiresKey: boolean;
}

export interface AiSettings {
  configured: boolean;
  secretStoreConfigured: boolean;
  provider: string;
  model: string;
  baseUrl: string | null;
  hasKey: boolean;
  requiresKey: boolean;
  enabled: boolean;
  usable: boolean;
  maxSnippetsPerAnalysis: number;
  updatedAtUtc: string | null;
  lastTestedAtUtc: string | null;
  lastTestSucceeded: boolean | null;
  lastTestMessage: string | null;
  providers: AiProviderInfo[];
  localOllama: { url: string | null; available: boolean; models: string[]; defaultModel: string } | null;
}

export interface AiTestResult {
  succeeded: boolean;
  message: string;
  model: string;
  elapsedMs: number;
  inputTokens: number;
  outputTokens: number;
}

export interface BusinessRule {
  id: string;
  filePath: string;
  symbol: string;
  startLine: number;
  name: string;
  description: string;
  category: string;
  conditions: string[];
  confidence: number;
  model: string;
  createdAtUtc: string;
  rating?: number | null;
  feedbackComment?: string | null;
}

export interface BusinessRuleAnalysis {
  id: string;
  provider: string;
  model: string;
  status: string;
  candidatesFound: number;
  snippetsSent: number;
  rulesFound: number;
  inputTokens: number;
  outputTokens: number;
  error: string | null;
  startedAtUtc: string;
  completedAtUtc: string | null;
}

export interface Narrative {
  text: string;
  model: string;
  cached: boolean;
  createdAtUtc: string;
  rating?: number | null;
  feedbackComment?: string | null;
}

export interface FeedbackBody {
  rating: number;
  comment: string | null;
  author: string | null;
}

export interface AiFeedbackSummary {
  up: number;
  down: number;
  byKind: { key: string; up: number; down: number; helpfulShare: number | null }[];
  byModel: { key: string; up: number; down: number; helpfulShare: number | null }[];
  recent: { kind: string; model: string; rating: number; comment: string | null; assessmentId: string; ratedBy: string | null; ratedAtUtc: string; title: string }[];
}

export interface FindingFix {
  fix: Narrative | null;
  jobState: string | null;
  jobError: string | null;
}

export interface AiEstimate {
  methods: number;
  requests: number;
  inputTokens: number;
  outputTokens: number;
  note: string;
}

export interface BusinessRules {
  aiUsable: boolean;
  analyses: BusinessRuleAnalysis[];
  rules: BusinessRule[];
}

/** Download/iframe URL with the access token appended when auth is on. */
export function downloadUrl(url: string): string {
  return withToken(url);
}

/** Browser navigations (iframe, download links) cannot carry a header: pass the token as a query parameter. */
function withToken(url: string): string {
  const token = getAccessToken();
  return token ? `${url}${url.includes("?") ? "&" : "?"}access_token=${encodeURIComponent(token)}` : url;
}

export const api = {
  listAssessments: () => http<AssessmentSummary[]>("/api/assessments"),
  getAssessment: (id: string) => http<Assessment>(`/api/assessments/${id}`),
  getHealth: async (id: string): Promise<Health | null> => {
    try {
      return await http<Health>(`/api/assessments/${id}/health`);
    } catch (e) {
      if (e instanceof Error && e.message === "not-found") return null;
      throw e;
    }
  },
  getFindings: (id: string, q: FindingQuery) => {
    const params = new URLSearchParams();
    for (const [k, v] of Object.entries(q)) if (v !== undefined && v !== "" && v !== null) params.set(k, String(v));
    return http<Paged<Finding>>(`/api/assessments/${id}/findings?${params.toString()}`);
  },
  listLocalSources: () => http<LocalSource[]>("/api/sources/local"),
  browseLocal: (path?: string) => http<BrowseResult>(`/api/sources/local/browse${path ? `?path=${encodeURIComponent(path)}` : ""}`),
  discover: (body: { sourceKind: string; locator: string; credentialName: string | null }) =>
    http<DiscoveredRepository[]>("/api/sources/discover", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    }),
  createAssessment: (body: {
    name: string;
    sourceKind: string;
    sourceLocator: string;
    branch: string | null;
    credentialName: string | null;
    excludePaths?: string[];
  }) =>
    http<{ id: string; jobId: string }>("/api/assessments", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    }),
  reportUrl: (id: string, lang: string, since?: string) => withToken(`/api/assessments/${id}/report?lang=${encodeURIComponent(lang)}${since ? `&since=${encodeURIComponent(since)}` : ""}`),
  sbomUrl: (id: string) => withToken(`/api/assessments/${id}/sbom`),
  reportPdfUrl: (id: string, lang: string, since?: string) => withToken(`/api/assessments/${id}/report.pdf?lang=${encodeURIComponent(lang)}${since ? `&since=${encodeURIComponent(since)}` : ""}`),
  renameAssessment: (id: string, name: string) =>
    http<{ id: string; name: string }>(`/api/assessments/${id}`, {
      method: "PATCH",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ name }),
    }),
  deleteAssessment: (id: string) => http<void>(`/api/assessments/${id}`, { method: "DELETE" }),
  triage: (id: string, findingId: string, body: TriageRequest, lang: string) =>
    http<Finding>(`/api/assessments/${id}/findings/${findingId}/triage?lang=${encodeURIComponent(lang)}`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    }),
  listCredentials: () => http<{ configured: boolean; items: Credential[] }>("/api/credentials"),
  upsertCredential: (name: string, body: { secret: string; username: string | null; description: string | null }) =>
    http<Credential>(`/api/credentials/${encodeURIComponent(name)}`, {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    }),
  deleteCredential: (name: string) => http<void>(`/api/credentials/${encodeURIComponent(name)}`, { method: "DELETE" }),
  getModernization: async (id: string, lang: string): Promise<ModernizationPlan | null> => {
    try {
      return await http<ModernizationPlan>(`/api/assessments/${id}/modernization?lang=${encodeURIComponent(lang)}`);
    } catch (e) {
      if (e instanceof Error && e.message === "not-found") return null;
      throw e;
    }
  },
  exportUrl: (id: string, format: "csv" | "json" | "sarif", lang: string, status?: string) =>
    withToken(`/api/assessments/${id}/findings/export?format=${format}&lang=${encodeURIComponent(lang)}${status ? `&status=${status}` : ""}`),
  setScope: (id: string, excludePaths: string[]) =>
    http<{ id: string; excludePaths: string[]; defaults: string[] }>(`/api/assessments/${id}/scope`, {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ excludePaths }),
    }),
  listPolicies: (id: string) => http<SuppressionPolicy[]>(`/api/assessments/${id}/policies`),
  createPolicy: (id: string, body: { rulePattern: string; pathGlob: string | null; reason: string; author: string }) =>
    http<{ policy: SuppressionPolicy; appliedToExisting: number }>(`/api/assessments/${id}/policies`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    }),
  deletePolicy: (policyId: string) => http<void>(`/api/policies/${policyId}`, { method: "DELETE" }),
  getActual: async (id: string, lang: string): Promise<ActualOutcome | null> => {
    try {
      return await http<ActualOutcome>(`/api/assessments/${id}/actuals?lang=${encodeURIComponent(lang)}`);
    } catch (e) {
      if (e instanceof Error && e.message === "not-found") return null;
      throw e;
    }
  },
  recordActual: (id: string, body: { strategy: string; actualHours: number; actualMonths: number | null; actualCost: number | null; currency: string | null; notes: string | null; recordedBy: string }, lang: string) =>
    http<ActualOutcome>(`/api/assessments/${id}/actuals?lang=${encodeURIComponent(lang)}`, {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    }),
  getCalibration: (lang: string) => http<Calibration>(`/api/calibration?lang=${encodeURIComponent(lang)}`),
  findingsByRule: (id: string, lang: string) => http<RuleGroup[]>(`/api/assessments/${id}/findings/by-rule?lang=${encodeURIComponent(lang)}`),
  findingsHeatmap: (id: string, depth = 2) => http<HeatmapRow[]>(`/api/assessments/${id}/findings/heatmap?depth=${depth}`),
  setSchedule: (id: string, body: { rerunEveryDays: number | null; webhookUrl: string | null; targetScore: number | null; targetDate: string | null }) =>
    http<{ id: string; rerunEveryDays: number | null; webhookUrl: string | null }>(`/api/assessments/${id}/schedule`, {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    }),
  listJobs: (state?: string) => http<Job[]>(`/api/jobs${state ? `?state=${state}` : ""}`),
  retryJob: (id: string) => http<{ jobId: string }>(`/api/jobs/${id}/retry`, { method: "POST" }),
  getPortfolio: (lang: string) => http<Portfolio>(`/api/portfolio?lang=${encodeURIComponent(lang)}`),
  getPortfolioTrend: (weeks = 26) => http<PortfolioTrendPoint[]>(`/api/portfolio/trend?weeks=${weeks}`),
  getRules: (lang: string) => http<RuleCatalogEntry[]>(`/api/rules?lang=${encodeURIComponent(lang)}`),
  setTags: (id: string, tags: string[]) =>
    http<{ id: string; tags: string[] }>(`/api/assessments/${id}/tags`, {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ tags }),
    }),
  importSarif: (id: string, sarifJson: string) =>
    http<{ runNumber: number; tool: string; imported: number; newFindings: number; resolved: number; healthScore: number }>(
      `/api/assessments/${id}/sarif`,
      { method: "POST", headers: { "Content-Type": "application/json" }, body: sarifJson },
    ),
  getVersion: () => http<{ version: string }>(`/api/version`),
  exportIssues: (id: string, top: number, lang: string) =>
    http<{ created: number; urls: string[]; errors: string[] }>(`/api/assessments/${id}/export/issues?lang=${encodeURIComponent(lang)}`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ top }),
    }),
  complianceUrl: (id: string, lang: string) => withToken(`/api/assessments/${id}/compliance.zip?lang=${encodeURIComponent(lang)}`),
  seedDemo: () => http<{ created: number }>(`/api/demo`, { method: "POST" }),
  removeDemo: () => http<{ removed: number }>(`/api/demo`, { method: "DELETE" }),
  getCostProfile: () => http<CostProfile>(`/api/settings/cost`),
  setCostProfile: (body: { currency: string; hourlyRate: number; teamSize: number | null; author: string | null }) =>
    http<CostProfile>(`/api/settings/cost`, {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    }),
  resetCostProfile: () => http<void>(`/api/settings/cost`, { method: "DELETE" }),
  setRuleSeverity: (ruleId: string, severity: string | null) =>
    http<{ ruleId: string; severity: string | null }>(`/api/rules/${encodeURIComponent(ruleId)}/severity`, {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ severity }),
    }),
  listRuns: (id: string) => http<Run[]>(`/api/assessments/${id}/runs`),
  runAgain: (id: string) => http<{ jobId: string }>(`/api/assessments/${id}/runs`, { method: "POST" }),
  me: () => http<Me>("/api/auth/me"),
  compare: (a: string, b: string, lang: string) => http<SideBySide>(`/api/assessments/compare?a=${a}&b=${b}&lang=${encodeURIComponent(lang)}`),
  getAccess: (id: string) => http<AccessView>(`/api/assessments/${id}/access`),
  grantAccess: (id: string, body: { subject: string; subjectName: string | null; role: string }) =>
    http<AccessView>(`/api/assessments/${id}/access`, { method: "PUT", body: JSON.stringify(body), headers: { "Content-Type": "application/json" } }),
  revokeAccess: (id: string, entryId: string) => http<AccessView>(`/api/assessments/${id}/access/${entryId}`, { method: "DELETE" }),
  authConfig: () => http<{ enabled: boolean }>("/api/auth/config"),
  listTokens: () => http<ApiToken[]>("/api/tokens"),
  createToken: (body: { name: string; role: string; expiresAtUtc: string | null }) =>
    http<ApiTokenCreated>("/api/tokens", { method: "POST", body: JSON.stringify(body), headers: { "Content-Type": "application/json" } }),
  revokeToken: (id: string) => http<void>(`/api/tokens/${id}`, { method: "DELETE" }),
  getAiSettings: () => http<AiSettings>("/api/ai/settings"),
  saveAiSettings: (body: { provider: string; model: string | null; baseUrl: string | null; apiKey: string | null; enabled: boolean; maxSnippetsPerAnalysis: number | null }) =>
    http<AiSettings>("/api/ai/settings", { method: "PUT", body: JSON.stringify(body), headers: { "Content-Type": "application/json" } }),
  clearAiKey: () => http<AiSettings>("/api/ai/settings/key", { method: "DELETE" }),
  testAi: () => http<AiTestResult>("/api/ai/test", { method: "POST" }),
  aiEstimate: (methods?: number) => http<AiEstimate>(`/api/ai/estimate${methods ? `?methods=${methods}` : ""}`),
  rateNarrative: (id: string, kind: string, body: FeedbackBody, lang: string, findingId?: string) =>
    http<Narrative>(`/api/assessments/${id}/ai/feedback?kind=${encodeURIComponent(kind)}&lang=${encodeURIComponent(lang)}${findingId ? `&findingId=${findingId}` : ""}`, { method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) }),
  rateBusinessRule: (id: string, ruleId: string, body: FeedbackBody, lang: string) =>
    http<BusinessRule>(`/api/assessments/${id}/business-rules/${ruleId}/feedback?lang=${encodeURIComponent(lang)}`, { method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) }),
  aiFeedback: () => http<AiFeedbackSummary>("/api/ai/feedback"),
  getExplanation: (id: string, findingId: string, lang: string) =>
    http<Narrative | undefined>(`/api/assessments/${id}/findings/${findingId}/explain?lang=${encodeURIComponent(lang)}`),
  explainFinding: (id: string, findingId: string, lang: string, refresh = false) =>
    http<Narrative>(`/api/assessments/${id}/findings/${findingId}/explain?lang=${encodeURIComponent(lang)}${refresh ? "&refresh=true" : ""}`, { method: "POST" }),
  requestFix: (id: string, findingId: string, lang: string) =>
    http<{ jobId: string }>(`/api/assessments/${id}/findings/${findingId}/fix?lang=${encodeURIComponent(lang)}`, { method: "POST" }),
  getFix: (id: string, findingId: string, lang: string) => http<FindingFix>(`/api/assessments/${id}/findings/${findingId}/fix?lang=${encodeURIComponent(lang)}`),
  getSummary: (id: string, lang: string) => http<Narrative | undefined>(`/api/assessments/${id}/summary?lang=${encodeURIComponent(lang)}`),
  generateSummary: (id: string, lang: string) => http<Narrative>(`/api/assessments/${id}/summary/generate?lang=${encodeURIComponent(lang)}`, { method: "POST" }),
  getMigrationPlan: (id: string, lang: string) => http<Narrative | undefined>(`/api/assessments/${id}/migration-plan?lang=${encodeURIComponent(lang)}`),
  generateMigrationPlan: (id: string, lang: string) =>
    http<Narrative>(`/api/assessments/${id}/migration-plan/generate?lang=${encodeURIComponent(lang)}`, { method: "POST" }),
  migrationPlanUrl: (id: string, lang: string) => withToken(`/api/assessments/${id}/migration-plan/export?lang=${encodeURIComponent(lang)}`),
  businessRules: (id: string, lang: string) => http<BusinessRules>(`/api/assessments/${id}/business-rules?lang=${encodeURIComponent(lang)}`),
  analyzeBusinessRules: (id: string) => http<{ jobId: string }>(`/api/assessments/${id}/business-rules/analyze`, { method: "POST" }),
  replaceUpload: (id: string, uploadId: string) =>
    http<{ jobId: string }>(`/api/assessments/${id}/upload`, { method: "PUT", body: JSON.stringify({ uploadId }) }),
  compareRun: (id: string, runId: string, lang: string, withRunId?: string) =>
    http<RunComparison>(
      `/api/assessments/${id}/runs/${runId}/comparison?lang=${encodeURIComponent(lang)}${withRunId ? `&with=${withRunId}` : ""}`,
    ),
};
