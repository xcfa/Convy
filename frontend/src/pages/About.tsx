import { useMemo, useState } from "react";
import { api, type AboutInfo, type AboutPath } from "../api";
import { Badge, Card, Empty, ErrorBanner } from "../components";
import { ago, dateTimeText } from "../format";
import { usePolling } from "../usePolling";

const authLabel: Record<AboutInfo["ui_auth"], string> = {
  oidc: "OIDC sign-in",
  none: "open (no sign-in)",
  off: "off",
};

export function About() {
  const { data, error } = usePolling(api.about, 0);
  const [filter, setFilter] = useState("");
  const [copied, setCopied] = useState(false);

  const settings = useMemo(() => {
    const text = filter.trim().toLowerCase();
    const all = data?.settings ?? [];
    return text
      ? all.filter((s) => `${s.key} ${s.value ?? ""} ${s.source}`.toLowerCase().includes(text))
      : all;
  }, [data, filter]);

  if (!data) {
    return (
      <>
        <ErrorBanner message={error} />
        {!error && <Empty>Loading…</Empty>}
      </>
    );
  }

  async function copy() {
    try {
      await navigator.clipboard.writeText(report(data!));
      setCopied(true);
      window.setTimeout(() => setCopied(false), 2000);
    } catch {
      // Clipboard not allowed (plain HTTP); nothing to do.
    }
  }

  let lastSection = "";

  return (
    <>
      <div className="grid">
        <Card
          title="Version"
          actions={
            <button className="button ghost small" onClick={copy}>
              {copied ? "Copied" : "Copy for a bug report"}
            </button>
          }
        >
          <div className="version">{data.version}</div>
          <dl className="facts">
            <dt>Commit</dt>
            <dd className="mono small wrap-anywhere">{data.commit ?? "—"}</dd>
            <dt>Runtime</dt>
            <dd>{data.runtime}</dd>
            <dt>System</dt>
            <dd>
              {data.os} ({data.architecture})
            </dd>
            <dt>Environment</dt>
            <dd>{data.environment}</dd>
            <dt>Running since</dt>
            <dd title={dateTimeText(data.started_at)}>
              {dateTimeText(data.started_at)} ({ago(data.started_at, data.now)})
            </dd>
          </dl>
        </Card>
        <Card title="Features">
          <dl className="facts">
            <dt>Web UI</dt>
            <dd>{authLabel[data.ui_auth]}</dd>
            <dt>MCP endpoint</dt>
            <dd>{data.mcp_enabled ? <Badge tone="ok">enabled</Badge> : <Badge tone="neutral">off</Badge>}</dd>
            <dt>Downloaders</dt>
            <dd>{data.downloaders.join(", ") || "—"}</dd>
            <dt>Content root</dt>
            <dd className="mono small wrap-anywhere">{data.content_root}</dd>
          </dl>
        </Card>
      </div>

      <Card title="Paths">
        <PathTable paths={data.paths} />
      </Card>

      <Card title="Configuration sources">
        <p className="muted small">Applied in this order; a later source overrides an earlier one for the same setting.</p>
        <PathTable paths={data.sources} numbered />
      </Card>

      <Card
        title="Settings"
        actions={
          <input
            type="search"
            placeholder="Filter settings"
            value={filter}
            onChange={(e) => setFilter(e.target.value)}
            aria-label="Filter settings"
          />
        }
      >
        <p className="muted small">
          Convy's own settings as they are in effect, with the source each comes from. Passwords, keys and secrets are
          hidden. Settings left at their defaults are not listed.
        </p>
        {settings.length === 0 ? (
          <Empty>{filter ? "No setting matches." : "Nothing is configured."}</Empty>
        ) : (
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Setting</th>
                  <th>Value</th>
                  <th>Source</th>
                </tr>
              </thead>
              <tbody>
                {settings.map((s) => {
                  const section = s.key.split(":")[0];
                  const header = section.toLowerCase() !== lastSection.toLowerCase();
                  lastSection = section;
                  return (
                    <SettingRows key={s.key} header={header ? section : null} setting={s} />
                  );
                })}
              </tbody>
            </table>
          </div>
        )}
      </Card>
    </>
  );
}

function SettingRows({ header, setting }: { header: string | null; setting: AboutInfo["settings"][number] }) {
  return (
    <>
      {header && (
        <tr className="section-row">
          <td colSpan={3}>{header}</td>
        </tr>
      )}
      <tr>
        <td className="mono small">{setting.key.split(":").slice(1).join(":") || setting.key}</td>
        <td className="mono small wrap-anywhere">
          {setting.value}
          {setting.hidden && (
            <>
              {" "}
              <Badge tone="neutral" title="Hidden: passwords, keys and secrets are never shown">
                hidden
              </Badge>
            </>
          )}
        </td>
        <td>
          <span className="chip">{setting.source}</span>
        </td>
      </tr>
    </>
  );
}

function PathTable({ paths, numbered = false }: { paths: AboutPath[]; numbered?: boolean }) {
  return (
    <div className="table-wrap">
      <table>
        <tbody>
          {paths.map((p, i) => (
            <tr key={`${p.name}-${i}`}>
              {numbered && <td className="num muted">{i + 1}</td>}
              <td>{p.name}</td>
              <td className="mono small wrap-anywhere">{p.path ?? ""}</td>
              <td>
                {p.exists === null ? null : p.exists ? <Badge tone="ok">exists</Badge> : <Badge tone="warn">missing</Badge>}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

/** Plain text for a bug report: version, system and settings (secrets already hidden by the server). */
function report(about: AboutInfo): string {
  const lines = [
    `Convy ${about.informational_version}`,
    `${about.runtime}, ${about.os} (${about.architecture}), ${about.environment}`,
    `Web UI: ${about.ui_auth}; MCP: ${about.mcp_enabled ? "on" : "off"}; downloaders: ${about.downloaders.join(", ")}`,
    "",
    "Sources:",
    ...about.sources.map((s, i) => `  ${i + 1}. ${s.name}${s.path ? ` (${s.path}${s.exists === false ? ", missing" : ""})` : ""}`),
    "",
    "Settings:",
    ...about.settings.map((s) => `  ${s.key} = ${s.value ?? ""}  [${s.source}]`),
  ];
  return lines.join("\n");
}
