import { useState } from "react";
import { api, type UiStatus } from "../api";
import { Badge, Card, Empty, ErrorBanner } from "../components";
import { ago, dateTimeText, duration } from "../format";
import { href } from "../route";
import { usePolling } from "../usePolling";

const jobOrder = ["queued", "downloading", "stalled", "placing", "completed", "failed", "cancelled"];

export function Overview() {
  const { data, error, reload } = usePolling(api.status, 5000);
  const [syncMessage, setSyncMessage] = useState<string>();

  async function syncNow() {
    try {
      await api.sync();
      setSyncMessage("Sync queued.");
      window.setTimeout(reload, 1000);
    } catch (e) {
      setSyncMessage(e instanceof Error ? e.message : String(e));
    }
  }

  if (!data) {
    return (
      <>
        <ErrorBanner message={error} />
        {!error && <Empty>Loading…</Empty>}
      </>
    );
  }

  return (
    <>
      <ErrorBanner message={error} />
      <div className="grid">
        <SyncCard status={data} onSync={syncNow} message={syncMessage} />
        <JobsCard jobs={data.jobs} />
        <StorageCard status={data} />
        <ServiceCard status={data} />
      </div>
      <DownloadersCard status={data} />
      <SourcesCard status={data} />
    </>
  );
}

function SyncCard({ status, onSync, message }: { status: UiStatus; onSync: () => void; message?: string }) {
  const { sync } = status;
  return (
    <Card
      title="Sync"
      actions={
        <button className="button" onClick={onSync}>
          Sync now
        </button>
      }
    >
      <dl className="facts">
        <dt>State</dt>
        <dd>{sync.running ? <Badge tone="info">running</Badge> : <Badge tone="neutral">idle</Badge>}</dd>
        <dt>Schedule</dt>
        <dd>{sync.auto_sync ? `every ${duration(sync.interval_seconds)}` : "manual only"}</dd>
        <dt>Last cycle</dt>
        <dd title={dateTimeText(sync.last_finished_at)}>
          {sync.last_started_at ? ago(sync.last_finished_at ?? sync.last_started_at, status.now) : "not yet since start"}
          {sync.last_error && (
            <>
              {" "}
              <Badge tone="bad" title={sync.last_error}>
                failed
              </Badge>
            </>
          )}
        </dd>
      </dl>
      {message && <p className="muted small">{message}</p>}
    </Card>
  );
}

function JobsCard({ jobs }: { jobs: Record<string, number> }) {
  const total = Object.values(jobs).reduce((sum, n) => sum + n, 0);
  return (
    <Card title="Jobs" actions={<a href={href("jobs")}>All jobs</a>}>
      {total === 0 ? (
        <p className="muted">No jobs yet. The agent starts them through MCP.</p>
      ) : (
        <div className="counts">
          {jobOrder
            .filter((s) => (jobs[s] ?? 0) > 0)
            .map((s) => (
              <a key={s} className={`count ${s}`} href={href("jobs", { status: s })}>
                <strong>{jobs[s]}</strong>
                <span>{s}</span>
              </a>
            ))}
        </div>
      )}
    </Card>
  );
}

function StorageCard({ status }: { status: UiStatus }) {
  const { storage } = status;
  return (
    <Card title="Storage layout">
      {storage.problems.length > 0 ? (
        <ul className="problems">
          {storage.problems.map((p) => (
            <li key={p}>{p}</li>
          ))}
        </ul>
      ) : !storage.checked_at ? (
        <p className="muted">Not checked yet.</p>
      ) : storage.unchecked.length > 0 ? (
        <p>
          <Badge tone="warn">incomplete</Badge> <span className="muted">no problems found so far</span>
        </p>
      ) : (
        <p>
          <Badge tone="ok">ok</Badge> <span className="muted">hard links can be created</span>
        </p>
      )}
      {storage.unchecked.length > 0 && (
        <p className="muted small">
          Not checked yet, downloader unreachable: {storage.unchecked.join(", ")}. Convy checks again on the next sync.
        </p>
      )}
      {storage.checked_at && <p className="muted small">Checked {ago(storage.checked_at, status.now)}</p>}
    </Card>
  );
}

function ServiceCard({ status }: { status: UiStatus }) {
  return (
    <Card title="Service">
      <dl className="facts">
        <dt>Version</dt>
        <dd className="mono">{status.version}</dd>
        <dt>Running since</dt>
        <dd title={dateTimeText(status.started_at)}>{ago(status.started_at, status.now)}</dd>
        <dt>MCP endpoint</dt>
        <dd>
          {status.mcp_enabled ? <Badge tone="ok">enabled</Badge> : <Badge tone="neutral" title="Set MCP__APIKEY to enable /mcp">off</Badge>}
        </dd>
      </dl>
    </Card>
  );
}

function DownloadersCard({ status }: { status: UiStatus }) {
  return (
    <Card title="Downloaders">
      {status.downloaders.length === 0 ? (
        <Empty>No downloader is configured.</Empty>
      ) : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Downloader</th>
                <th>Protocol</th>
                <th>State</th>
                <th>Last read</th>
                <th className="num">Items</th>
                <th className="num">Handled</th>
                <th>Error</th>
              </tr>
            </thead>
            <tbody>
              {status.downloaders.map((d) => (
                <tr key={d.provider}>
                  <td>{d.provider}</td>
                  <td>{d.protocol}</td>
                  <td>
                    {d.ok === null ? (
                      <Badge tone="neutral">waiting</Badge>
                    ) : d.ok ? (
                      <Badge tone="ok">ok</Badge>
                    ) : (
                      <Badge tone="bad">unreachable</Badge>
                    )}
                  </td>
                  <td title={dateTimeText(d.last_sync_at)}>{ago(d.last_sync_at, status.now)}</td>
                  <td className="num">{d.ok ? d.items : "—"}</td>
                  <td className="num">{d.ok ? d.processed : "—"}</td>
                  <td className="wrap">{d.error ?? ""}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Card>
  );
}

function SourcesCard({ status }: { status: UiStatus }) {
  return (
    <Card title="Search sources">
      {status.sources.length === 0 ? (
        <Empty>No source is configured (Prowlarr, slskd).</Empty>
      ) : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Source</th>
                <th>Id</th>
                <th>Protocol</th>
                <th>Status</th>
                <th>Message</th>
              </tr>
            </thead>
            <tbody>
              {status.sources.map((s) => (
                <tr key={s.id}>
                  <td>{s.name}</td>
                  <td className="mono">{s.id}</td>
                  <td>{s.protocol}</td>
                  <td>
                    <Badge tone={s.status === "ok" ? "ok" : s.status === "disabled" ? "neutral" : "bad"}>{s.status}</Badge>
                  </td>
                  <td className="wrap">{s.message ?? ""}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Card>
  );
}
