import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { api, type Level, type LogEntry } from "../api";
import { Empty, ErrorBanner } from "../components";
import { dateTimeText, timeText } from "../format";

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

export function Logs() {
  const [level, setLevel] = useState<Level>("Information");
  const [search, setSearch] = useState("");
  const [query, setQuery] = useState("");
  const [follow, setFollow] = useState(true);
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
  }, [level, query]);

  // …then only entries newer than the last one shown are fetched.
  useEffect(() => {
    let stopped = false;

    async function poll() {
      try {
        const page = await api.logs(lastSeq.current, level, query);
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
  }, [level, query, follow]);

  // Keep the newest line in view unless the user scrolled up to read.
  useLayoutEffect(() => {
    const element = list.current;
    if (element && stickToBottom.current) element.scrollTop = element.scrollHeight;
  }, [entries]);

  function onScroll() {
    const element = list.current;
    if (element) stickToBottom.current = element.scrollHeight - element.scrollTop - element.clientHeight < 40;
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
        <input
          type="search"
          placeholder="Filter text, logger or exception"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          aria-label="Filter log entries"
        />
        <label className="check">
          <input type="checkbox" checked={follow} onChange={(e) => setFollow(e.target.checked)} /> Follow
        </label>
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
        ) : (
          entries.map((e) => <LogLine key={e.seq} entry={e} />)
        )}
      </div>
    </div>
  );
}

function LogLine({ entry }: { entry: LogEntry }) {
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
