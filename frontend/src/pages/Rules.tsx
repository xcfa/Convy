import { api } from "../api";
import { Badge, Card, Empty, ErrorBanner } from "../components";
import { dateTimeText } from "../format";
import { highlightCondition, highlightYaml, type Token } from "../highlight";
import { usePolling } from "../usePolling";

export function Rules() {
  const { data, error } = usePolling(api.rules, 10000);

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
      <div className="toolbar">
        <span className="mono small">{data.path}</span>
        <Badge tone="neutral">version {data.version}</Badge>
        <span className="muted small">
          {data.rules.length} rule{data.rules.length === 1 ? "" : "s"} in effect · first match wins · read-only, edit the file
          to change them
        </span>
      </div>
      {data.error && (
        <div className="banner bad" role="alert">
          The file as it is now could not be loaded{data.error_at ? ` (${dateTimeText(data.error_at)})` : ""}: {data.error}
          <div className="small">The rules in effect are still those of version {data.version}.</div>
        </div>
      )}

      <Card title="Rules in effect">
        {data.rules.length === 0 ? (
          <Empty>{data.exists ? "The file has no rules." : "There is no rules file; nothing is placed by rules."}</Empty>
        ) : (
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th className="num">#</th>
                  <th>Name</th>
                  <th>Condition</th>
                  <th>Path</th>
                  <th>Properties</th>
                </tr>
              </thead>
              <tbody>
                {data.rules.map((rule, i) => (
                  <tr key={i}>
                    <td className="num muted">{i + 1}</td>
                    <td>{rule.name ?? <span className="muted">—</span>}</td>
                    <td className="wrap code-inline">
                      <Tokens tokens={highlightCondition(rule.condition)} />
                    </td>
                    <td className="wrap mono small">{rule.path}</td>
                    <td className="wrap">
                      <div className="chips">
                        {rule.properties.map((p) => (
                          <span key={p} className="chip mono">
                            {p}
                          </span>
                        ))}
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Card>

      <Card title="File">
        {data.text === null ? (
          <Empty>{data.exists ? "The file is too large to show." : "The file does not exist."}</Empty>
        ) : (
          <pre className="code" aria-label="rules.yaml">
            {highlightYaml(data.text).map((line, i) => (
              <div key={i} className="code-line">
                <span className="ln">{i + 1}</span>
                <span>
                  <Tokens tokens={line} />
                  {line.length === 0 && " "}
                </span>
              </div>
            ))}
          </pre>
        )}
      </Card>
    </>
  );
}

function Tokens({ tokens }: { tokens: Token[] }) {
  return (
    <>
      {tokens.map((t, i) =>
        t.kind === "plain" ? (
          t.text
        ) : (
          <span key={i} className={`tok-${t.kind}`}>
            {t.text}
          </span>
        ),
      )}
    </>
  );
}
