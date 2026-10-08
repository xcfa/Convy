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
  sources: Source[];
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

  if (response.status === 202 || response.status === 204) {
    return undefined as T;
  }

  return (await response.json()) as T;
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
};
