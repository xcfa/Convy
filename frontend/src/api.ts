// Client for Convy's UI API (/api/ui). Every request carries the X-Convy-UI header; the
// server refuses state-changing requests without it, which keeps other sites from posting.

export type Level = "Verbose" | "Debug" | "Information" | "Warning" | "Error" | "Fatal";

export interface UiUser {
  name: string | null;
  groups: string[];
  auth: "oidc" | "none";
}

export interface Source {
  id: string;
  name: string;
  protocol: string;
  status: "ok" | "error" | "disabled" | string;
  message: string | null;
}

export interface DownloaderStatus {
  provider: string;
  protocol: string;
  last_sync_at: string | null;
  ok: boolean | null;
  items: number | null;
  processed: number | null;
  error: string | null;
}

export interface UiStatus {
  version: string;
  started_at: string;
  now: string;
  sync: {
    auto_sync: boolean;
    interval_seconds: number;
    running: boolean;
    last_started_at: string | null;
    last_finished_at: string | null;
    last_error: string | null;
  };
  downloaders: DownloaderStatus[];
  /** null until the sources are first listed after a start. */
  sources: Source[] | null;
  storage: { checked_at: string | null; problems: string[]; unchecked: string[] };
  jobs: Record<string, number>;
  mcp_enabled: boolean;
}

export interface Job {
  job_id: string;
  status: string;
  title: string;
  category: string;
  provider: string;
  progress: number | null;
  downloaded_bytes: number | null;
  size_bytes: number | null;
  speed_bytes_per_sec: number | null;
  path: string | null;
  rule: string | null;
  error: string | null;
  created_at: string;
  /** Present only for a job with several releases. */
  releases?: JobRelease[];
}

export interface JobRelease {
  title: string;
  status: string;
  provider: string;
  progress: number | null;
  size_bytes: number | null;
  path: string | null;
  rule: string | null;
  error: string | null;
}

export interface LogEntry {
  seq: number;
  time: string;
  level: Level;
  category: string | null;
  message: string;
  exception: string | null;
}

export interface LogPage {
  entries: LogEntry[];
  last_seq: number;
}

export type ColumnKind = "text" | "number" | "bool" | "time" | "unix_time" | "blob";

export interface DataColumn {
  name: string;
  kind: ColumnKind;
}

export interface DataTable {
  name: string;
  columns: DataColumn[];
  rows: number;
}

export interface DataPage {
  table: string;
  columns: DataColumn[];
  rows: unknown[][];
  total: number;
  offset: number;
  limit: number;
}

export type WebhookEvent = "linked" | "job_status" | "source_error";

export interface WebhookParam {
  place: "query" | "body";
  name: string;
  value: string;
}

export interface Webhook {
  /** Set for webhooks created in the UI; null for those from configuration.yml. */
  id: number | null;
  source: "ui" | "file";
  name: string | null;
  url: string;
  events: WebhookEvent[];
  names: string[];
  params: WebhookParam[];
  enabled: boolean;
}

export interface WebhookList {
  webhooks: Webhook[];
  events: WebhookEvent[];
  fields: Record<WebhookEvent, string[]>;
  rules: string[];
}

export interface WebhookInput {
  name: string | null;
  url: string;
  events: WebhookEvent[];
  names: string[];
  params: WebhookParam[];
  enabled: boolean;
}

export interface WebhookTestResult {
  ok: boolean;
  status_code: number | null;
  duration_ms: number;
  url: string;
  request_body: string;
  response_body: string | null;
  error: string | null;
}

export interface Rule {
  name: string | null;
  condition: string;
  path: string;
  properties: string[];
  /** Empty condition: the rule matches every item. */
  matches_everything: boolean;
  /** An earlier rule matches everything, so this one is never used. */
  unreachable: boolean;
}

export interface RulesView {
  path: string;
  exists: boolean;
  text: string | null;
  version: number;
  rules: Rule[];
  error: string | null;
  error_at: string | null;
}

export interface AboutPath {
  name: string;
  path: string | null;
  exists: boolean | null;
}

export interface AboutSetting {
  key: string;
  value: string | null;
  source: string;
  hidden: boolean;
}

export interface AboutSourceCategory {
  id: string;
  position: number;
  listed: boolean;
}

export interface AboutSearchSource {
  id: string;
  name: string;
  protocol: string;
  status: string;
  message: string | null;
  timeout_sec: number;
  downloader: string | null;
  categories: AboutSourceCategory[];
}

export interface AboutProxy {
  http: string | null;
  https: string | null;
  no_proxy: string | null;
  /** What could not be read: a proxy that is not an address, NO_PROXY entries. */
  problems: string[];
}

export interface AboutInfo {
  version: string;
  informational_version: string;
  commit: string | null;
  runtime: string;
  os: string;
  architecture: string;
  environment: string;
  content_root: string;
  started_at: string;
  now: string;
  ui_auth: "oidc" | "none" | "off";
  mcp_enabled: boolean;
  downloaders: string[];
  proxy: AboutProxy;
  paths: AboutPath[];
  /** null until the sources are first listed after a start. */
  search_sources: AboutSearchSource[] | null;
  search_warnings: string[];
  configuration_sources: AboutPath[];
  settings: AboutSetting[];
}

export class ApiError extends Error {
  readonly status: number;

  constructor(status: number, message: string) {
    super(message);
    this.status = status;
  }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(path, {
    credentials: "same-origin",
    ...init,
    headers: { Accept: "application/json", "X-Convy-UI": "1", ...init?.headers },
  });

  if (response.status === 401) {
    // The session ended; sign in again and come back.
    window.location.assign("/auth/login");
    throw new ApiError(401, "Signing in again…");
  }

  if (!response.ok) {
    let message = `${response.status} ${response.statusText}`;
    try {
      const body = await response.json();
      if (body && typeof body.error === "string") message = body.error;
      else if (body && typeof body.title === "string") message = body.title;
    } catch {
      // Not JSON; keep the status line.
    }
    throw new ApiError(response.status, message);
  }

  // Some answers (202, 204, an action without a result) have no body.
  const text = await response.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

function json(method: string, body: unknown): RequestInit {
  return { method, body: JSON.stringify(body), headers: { "Content-Type": "application/json" } };
}

function query(params: Record<string, string | number | boolean | null | undefined>): string {
  const search = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) {
    if (value !== null && value !== undefined && value !== "") search.set(key, String(value));
  }
  const text = search.toString();
  return text ? `?${text}` : "";
}

export const api = {
  me: () => request<UiUser>("/api/ui/me"),
  status: () => request<UiStatus>("/api/ui/status"),
  logs: (after: number, level: Level, q: string, limit = 500) =>
    request<LogPage>(`/api/ui/logs${query({ after, level, q, limit })}`),
  jobs: (status: string, limit = 200) => request<{ jobs: Job[] }>(`/api/ui/jobs${query({ status, limit })}`),
  cancelJob: (jobId: string) =>
    request<{ job_id: string; status: string; note: string }>(`/api/ui/jobs/${encodeURIComponent(jobId)}/cancel`, {
      method: "POST",
    }),
  sync: () => request<void>("/api/ui/sync", { method: "POST" }),
  tables: () => request<DataTable[]>("/api/ui/data"),
  table: (name: string, q: string, sort: string | null, desc: boolean, offset: number, limit: number) =>
    request<DataPage>(`/api/ui/data/${encodeURIComponent(name)}${query({ q, sort, desc, offset, limit })}`),
  signOut: () => request<{ redirect: string }>("/auth/logout", { method: "POST" }),
  webhooks: () => request<WebhookList>("/api/ui/webhooks"),
  createWebhook: (input: WebhookInput) => request<Webhook>("/api/ui/webhooks", json("POST", input)),
  updateWebhook: (id: number, input: WebhookInput) => request<Webhook>(`/api/ui/webhooks/${id}`, json("PUT", input)),
  deleteWebhook: (id: number) => request<void>(`/api/ui/webhooks/${id}`, { method: "DELETE" }),
  testWebhook: (webhook: WebhookInput, event: WebhookEvent) =>
    request<WebhookTestResult>("/api/ui/webhooks/test", json("POST", { webhook, event })),
  rules: () => request<RulesView>("/api/ui/rules"),
  about: () => request<AboutInfo>("/api/ui/about"),
};
