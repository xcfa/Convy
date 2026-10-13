import { Fragment, useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { api, type Level, type LogEntry } from "../api";
import { Empty, ErrorBanner } from "../components";
import { dateTimeText, timeText } from "../format";
import { href, navigate } from "../route";

const levels: Level[] = ["Verbose", "Debug", "Information", "Warning", "Error", "Fatal"];
const keep = 2000;
const shortLevel: Record<Level, string> = {
  Verbose: "VRB",
  Debug: "DBG",
  Information: "INF",
  Warning: "WRN",
  Error: "ERR",
  Fatal: "FTL",
};

type View = "lines" | "text" | "json";
const views: { id: View; label: string }[] = [
  { id: "lines", label: "Lines" },
  { id: "text", label: "Text" },
  { id: "json", label: "JSON" },
];

/** Entries of one job (or of none), in the order they were written. */
interface Group {
  jobId: string | null;
  entries: LogEntry[];
}

export function Logs({ job }: { job: string }) {
  const [level, setLevel] = useState<Level>("Information");
  const [search, setSearch] = useState("");
  const [query, setQuery] = useState("");
  const [follow, setFollow] = useState(true);
  const [view, setView] = useState<View>("lines");
  const [grouped, setGrouped] = useState(false);
  const [copied, setCopied] = useState(false);
  const [entries, setEntries] = useState<LogEntry[]>([]);
  const [error, setError] = useState<string>();
  const [loaded, setLoaded] = useState(false);
  const lastSeq = useRef(0);
  const list = useRef<HTMLDivElement>(null);
  const stickToBottom = useRef(true);

  // Debounce typing into the search box.
  useEffect(() => {
    const timer = window.setTimeout(() => setQuery(search.trim()), 300);
    return () => window.clearTimeout(timer);
  }, [search]);

  // A new filter starts from scratch…
  useEffect(() => {
    lastSeq.current = 0;
    setEntries([]);
    setLoaded(false);
  }, [level, query, job]);

  // …then only entries newer than the last one shown are fetched.
  useEffect(() => {
    let stopped = false;

    async function poll() {
      try {
        const page = await api.logs(lastSeq.current, level, query, job);
        if (stopped) return;
        lastSeq.current = page.last_seq;
        if (page.entries.length > 0) {
          setEntries((current) => [...current, ...page.entries].slice(-keep));
        }
        setError(undefined);
      } catch (e) {
        if (!stopped) setError(e instanceof Error ? e.message : String(e));
      } finally {
        if (!stopped) setLoaded(true);
      }
    }

    void poll();
    if (!follow) return () => void (stopped = true);

    const timer = window.setInterval(() => {
      if (document.visibilityState === "visible") void poll();
    }, 2000);
    return () => {
      stopped = true;
      window.clearInterval(timer);
    };
  }, [level, query, job, follow]);

  const groups = useMemo(() => (grouped ? groupByJob(entries) : [{ jobId: null, entries }]), [entries, grouped]);

  // Jobs seen in the log, newest first, for the job filter.
  const jobs = useMemo(() => {
    const seen = [...new Set(entries.map((e) => e.job_id).filter((id): id is string => id !== null))].reverse();
    return job && !seen.includes(job) ? [job, ...seen] : seen;
  }, [entries, job]);

  // Keep the newest line in view unless the user scrolled up to read.
  useLayoutEffect(() => {
    const element = list.current;
    if (element && stickToBottom.current) element.scrollTop = element.scrollHeight;
  }, [entries, view, grouped]);

  function onScroll() {
    const element = list.current;
    if (element) stickToBottom.current = element.scrollHeight - element.scrollTop - element.clientHeight < 40;
  }

  async function copy() {
    try {
      await navigator.clipboard.writeText(render(groups, view === "json" ? "json" : "text", grouped));
      setCopied(true);
      window.setTimeout(() => setCopied(false), 2000);
    } catch {
      // Clipboard not allowed (plain HTTP); the Text view can be selected by hand.
    }
  }

  return (
    <div className="logs-page">
      <div className="toolbar">
        <label>
          Level{" "}
          <select value={level} onChange={(e) => setLevel(e.target.value as Level)}>
            {levels.map((l) => (
              <option key={l} value={l}>
                {l} and above
              </option>
            ))}
          </select>
        </label>
        <label>
          Job{" "}
          <select
            value={job}
            onChange={(e) => navigate("logs", e.target.value ? { job: e.target.value } : undefined)}
            aria-label="Only the entries of a job"
          >
            <option value="">all</option>
            {jobs.map((id) => (
              <option key={id} value={id}>
                {id}
              </option>
            ))}
          </select>
        </label>
        <input
          type="search"
          placeholder="Filter text, logger, exception or job"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          aria-label="Filter log entries"
        />
        <label className="check">
          <input type="checkbox" checked={grouped} onChange={(e) => setGrouped(e.target.checked)} /> Group by job
        </label>
        <label className="check">
          <input type="checkbox" checked={follow} onChange={(e) => setFollow(e.target.checked)} /> Follow
        </label>
        <div className="segmented" role="group" aria-label="View">
          {views.map((v) => (
            <button key={v.id} className={view === v.id ? "on" : undefined} onClick={() => setView(v.id)}>
              {v.label}
            </button>
          ))}
        </div>
        <button className="button ghost" onClick={copy} disabled={entries.length === 0}>
          {copied ? "Copied" : view === "json" ? "Copy JSON" : "Copy text"}
        </button>
        <button className="button ghost" onClick={() => setEntries([])}>
          Clear view
        </button>
        <span className="muted small">
          {entries.length} lines · Convy keeps the last 5000 entries in memory; the full log is in the console and log files
        </span>
      </div>
      <ErrorBanner message={error} />
      <div className="log-list card flush" ref={list} onScroll={onScroll}>
        {entries.length === 0 ? (
          <Empty>{loaded ? "No entries match." : "Loading…"}</Empty>
        ) : view === "lines" ? (
          groups.map((group) => (
            <Fragment key={group.jobId ?? "none"}>
              {grouped && <GroupHeader group={group} />}
              {group.entries.map((e) => (
                <LogLine key={e.seq} entry={e} showJob={!grouped} />
              ))}
            </Fragment>
          ))
        ) : (
          <pre className="log-text">{render(groups, view, grouped)}</pre>
        )}
      </div>
    </div>
  );
}

function GroupHeader({ group }: { group: Group }) {
  const worst = group.entries.reduce((max, e) => Math.max(max, levels.indexOf(e.level)), 0);
  const last = group.entries[group.entries.length - 1];
  return (
    <div className={`log-group ${levels[worst].toLowerCase()}`}>
      {group.jobId ? <a href={href("logs", { job: group.jobId })}>{group.jobId}</a> : <span>Not about a job</span>}
      <span className="muted">
        {group.entries.length} line{group.entries.length === 1 ? "" : "s"} · last {timeText(last.time)}
        {worst >= levels.indexOf("Warning") && ` · ${shortLevel[levels[worst]]}`}
      </span>
    </div>
  );
}

function LogLine({ entry, showJob }: { entry: LogEntry; showJob: boolean }) {
  const category = entry.category?.split(".").pop();
  return (
    <div className={`log-line ${entry.level.toLowerCase()}`}>
      <span className="log-time" title={dateTimeText(entry.time)}>
        {timeText(entry.time)}
      </span>
      <span className="log-level">{shortLevel[entry.level]}</span>
      <span className="log-category" title={entry.category ?? undefined}>
        {category ?? ""}
      </span>
      <span className="log-message">
        {showJob && entry.job_id && (
          <a className="log-job" href={href("logs", { job: entry.job_id })} title="Only this job's entries">
            {entry.job_id}
          </a>
        )}
        {entry.message}
        {entry.exception && (
          <details>
            <summary>exception</summary>
            <pre>{entry.exception}</pre>
          </details>
        )}
      </span>
    </div>
  );
}

/** The entries by job, each group in the order written; groups ordered by their latest entry, those without a job first. */
function groupByJob(entries: LogEntry[]): Group[] {
  const byJob = new Map<string | null, LogEntry[]>();
  for (const entry of entries) {
    const list = byJob.get(entry.job_id);
    if (list) list.push(entry);
    else byJob.set(entry.job_id, [entry]);
  }

  return [...byJob.entries()]
    .map(([jobId, list]) => ({ jobId, entries: list }))
    .sort((a, b) => {
      if (a.jobId === null) return -1;
      if (b.jobId === null) return 1;
      return a.entries[a.entries.length - 1].seq - b.entries[b.entries.length - 1].seq;
    });
}

/** Plain text (one line per entry, as in the log files) or JSON Lines with the structured properties. */
function render(groups: Group[], format: "text" | "json", grouped: boolean): string {
  const lines: string[] = [];
  for (const group of groups) {
    if (grouped && format === "text") {
      lines.push(`── ${group.jobId ?? "not about a job"} ──`);
    }

    for (const entry of group.entries) {
      lines.push(format === "json" ? JSON.stringify(toJson(entry)) : toText(entry));
    }

    if (grouped && format === "text") {
      lines.push("");
    }
  }

  return lines.join("\n");
}

function toText(entry: LogEntry): string {
  const job = entry.job_id ? ` [${entry.job_id}]` : "";
  const line = `${stamp(entry.time)} ${shortLevel[entry.level]} ${entry.category ?? "-"}${job}: ${entry.message}`;
  return entry.exception ? `${line}\n${entry.exception}` : line;
}

function toJson(entry: LogEntry): Record<string, unknown> {
  return Object.fromEntries(
    Object.entries({
      time: entry.time,
      level: entry.level,
      category: entry.category,
      job_id: entry.job_id,
      message: entry.message,
      properties: entry.properties,
      exception: entry.exception,
    }).filter(([, value]) => value !== null && value !== undefined),
  );
}

/** Local time with milliseconds, e.g. 2026-10-14 12:03:05.123. */
function stamp(value: string): string {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  const pad = (n: number, width = 2) => String(n).padStart(width, "0");
  return (
    `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ` +
    `${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}.${pad(date.getMilliseconds(), 3)}`
  );
}
