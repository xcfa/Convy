// The route lives in the hash: #/jobs?status=failed → tab "jobs", params "status=failed".

export const tabs = [
  { id: "overview", label: "Overview" },
  { id: "jobs", label: "Jobs" },
  { id: "logs", label: "Logs" },
  { id: "data", label: "Database" },
  { id: "webhooks", label: "Webhooks" },
  { id: "rules", label: "Rules" },
] as const;

export type TabId = (typeof tabs)[number]["id"];

export function readRoute(): { tab: TabId; params: URLSearchParams } {
  const [path, search = ""] = window.location.hash.replace(/^#\/?/, "").split("?");
  const tab = tabs.find((t) => t.id === path)?.id ?? "overview";
  return { tab, params: new URLSearchParams(search) };
}

export function href(tab: TabId, params?: Record<string, string>): string {
  const search = params ? new URLSearchParams(params).toString() : "";
  return `#/${tab}${search ? `?${search}` : ""}`;
}

export function navigate(tab: TabId, params?: Record<string, string>) {
  window.location.hash = href(tab, params).slice(1);
}
