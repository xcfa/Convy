import { Fragment, useState } from "react";
import { api, type Job } from "../api";
import { Empty, ErrorBanner, JobStatusBadge, Progress } from "../components";
import { bytes, dateTimeText, percent, speed } from "../format";
import { navigate } from "../route";
import { usePolling } from "../usePolling";

const statuses = ["", "queued", "downloading", "stalled", "placing", "completed", "failed", "cancelled"];
const cancellable = new Set(["queued", "downloading", "stalled"]);

export function Jobs({ status }: { status: string }) {
  const { data, error, loading, reload } = usePolling(() => api.jobs(status), 5000, [status]);
  const [actionError, setActionError] = useState<string>();
  const [busy, setBusy] = useState<string>();
  const [open, setOpen] = useState<Set<string>>(new Set());

  function toggle(jobId: string) {
    setOpen((current) => {
      const next = new Set(current);
      if (next.has(jobId)) next.delete(jobId);
      else next.add(jobId);
      return next;
    });
  }

  async function cancel(job: Job) {
    const what = job.releases ? `all ${job.releases.length} releases of “${job.title}”` : `“${job.title}”`;
    if (!window.confirm(`Stop downloading ${what}? Downloaded data and created links are kept.`)) return;
    setBusy(job.job_id);
    setActionError(undefined);
    try {
      await api.cancelJob(job.job_id);
      reload();
    } catch (e) {
      setActionError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(undefined);
    }
  }

  const jobs = data?.jobs ?? [];

  return (
    <>
      <div className="toolbar">
        <label>
          Status{" "}
          <select
            value={status}
            onChange={(e) => navigate("jobs", e.target.value ? { status: e.target.value } : undefined)}
          >
            {statuses.map((s) => (
              <option key={s} value={s}>
                {s || "all"}
              </option>
            ))}
          </select>
        </label>
        <span className="muted small">{data ? `${jobs.length} shown, newest first` : ""}</span>
      </div>
      <ErrorBanner message={actionError ?? error} />
      {!data && loading ? (
        <Empty>Loading…</Empty>
      ) : jobs.length === 0 ? (
        <Empty>{status ? `No ${status} jobs.` : "No jobs yet. The agent starts them through MCP."}</Empty>
      ) : (
        <div className="table-wrap card flush">
          <table>
            <thead>
              <tr>
                <th>Job</th>
                <th>Title</th>
                <th>Status</th>
                <th className="progress-col">Progress</th>
                <th className="num">Size</th>
                <th className="num">Speed</th>
                <th>Category</th>
                <th>Destination</th>
                <th>Created</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {jobs.map((job) => (
                <Fragment key={job.job_id}>
                <tr>
                  <td className="mono">{job.job_id}</td>
                  <td className="wrap title-cell">
                    {job.title}
                    {job.releases && (
                      <div>
                        <button className="link-button small" onClick={() => toggle(job.job_id)} aria-expanded={open.has(job.job_id)}>
                          {open.has(job.job_id) ? "▾" : "▸"} {job.releases.length} releases
                        </button>
                      </div>
                    )}
                    {job.error && <div className="error-text">{job.error}</div>}
                  </td>
                  <td>
                    <JobStatusBadge status={job.status} />
                  </td>
                  <td>
                    <div className="progress-cell">
                      <Progress value={job.progress} />
                      <span className="small">{percent(job.progress)}</span>
                    </div>
                  </td>
                  <td className="num" title={job.downloaded_bytes !== null ? `${bytes(job.downloaded_bytes)} downloaded` : undefined}>
                    {bytes(job.size_bytes)}
                  </td>
                  <td className="num">{speed(job.speed_bytes_per_sec)}</td>
                  <td>
                    {job.category}
                    <div className="muted small">{job.provider}</div>
                  </td>
                  <td className="wrap mono small">
                    {job.path ?? "—"}
                    {job.rule && <div className="muted">rule: {job.rule}</div>}
                  </td>
                  <td className="small">{dateTimeText(job.created_at)}</td>
                  <td>
                    {cancellable.has(job.status) && (
                      <button className="button danger small" disabled={busy === job.job_id} onClick={() => cancel(job)}>
                        Cancel
                      </button>
                    )}
                  </td>
                </tr>
                {job.releases && open.has(job.job_id) &&
                  job.releases.map((release, i) => (
                    <tr key={`${job.job_id}-${i}`} className="release-row">
                      <td />
                      <td className="wrap title-cell">
                        {release.title}
                        {release.error && <div className="error-text">{release.error}</div>}
                      </td>
                      <td>
                        <JobStatusBadge status={release.status} />
                      </td>
                      <td>
                        <div className="progress-cell">
                          <Progress value={release.progress} />
                          <span className="small">{percent(release.progress)}</span>
                        </div>
                      </td>
                      <td className="num">{bytes(release.size_bytes)}</td>
                      <td />
                      <td className="muted small">{release.provider}</td>
                      <td className="wrap mono small">
                        {release.path ?? "—"}
                        {release.rule && <div className="muted">rule: {release.rule}</div>}
                      </td>
                      <td />
                      <td />
                    </tr>
                  ))}
                </Fragment>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </>
  );
}
