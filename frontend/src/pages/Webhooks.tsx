import { useState } from "react";
import {
  api,
  type Webhook,
  type WebhookEvent,
  type WebhookInput,
  type WebhookList,
  type WebhookParam,
  type WebhookTestResult,
} from "../api";
import { Badge, Card, Empty, ErrorBanner } from "../components";
import { usePolling } from "../usePolling";

const eventInfo: Record<WebhookEvent, string> = {
  linked: "once per sync cycle with everything placed",
  job_status: "on every job status change",
  source_error: "when a search source starts failing",
};

type Editing = { id: number | null; input: WebhookInput; readOnly: boolean };

function toInput(webhook: Webhook): WebhookInput {
  return {
    name: webhook.name,
    url: webhook.url,
    events: webhook.events,
    names: webhook.names,
    params: webhook.params,
    enabled: webhook.enabled,
  };
}

const empty: WebhookInput = { name: "", url: "", events: ["linked"], names: [], params: [], enabled: true };

export function Webhooks() {
  const list = usePolling(api.webhooks, 0);
  const [editing, setEditing] = useState<Editing>();
  const [actionError, setActionError] = useState<string>();

  async function toggle(webhook: Webhook) {
    setActionError(undefined);
    try {
      await api.updateWebhook(webhook.id!, { ...toInput(webhook), enabled: !webhook.enabled });
      list.reload();
    } catch (e) {
      setActionError(e instanceof Error ? e.message : String(e));
    }
  }

  async function remove(webhook: Webhook) {
    if (!window.confirm(`Delete the webhook “${webhook.name || webhook.url}”?`)) return;
    setActionError(undefined);
    try {
      await api.deleteWebhook(webhook.id!);
      list.reload();
    } catch (e) {
      setActionError(e instanceof Error ? e.message : String(e));
    }
  }

  const data = list.data;

  return (
    <>
      <div className="toolbar">
        <button className="button" onClick={() => setEditing({ id: null, input: empty, readOnly: false })} disabled={!data}>
          Add webhook
        </button>
        <span className="muted small">
          Webhooks from configuration.yml are shown read-only; those added here are stored in the database. Both are used.
        </span>
      </div>
      <ErrorBanner message={actionError ?? list.error} />
      {!data ? (
        !list.error && <Empty>Loading…</Empty>
      ) : data.webhooks.length === 0 ? (
        <Empty>No webhooks yet.</Empty>
      ) : (
        <div className="table-wrap card flush">
          <table>
            <thead>
              <tr>
                <th>Webhook</th>
                <th>Events</th>
                <th>Rules</th>
                <th>Body</th>
                <th>State</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {data.webhooks.map((w, i) => (
                <tr key={w.id ?? `file-${i}`}>
                  <td className="wrap">
                    <div>
                      {w.name || <span className="muted">unnamed</span>}{" "}
                      {w.source === "file" && <Badge tone="neutral">configuration.yml</Badge>}
                    </div>
                    <div className="mono small muted url-cell">{w.url}</div>
                  </td>
                  <td>
                    <div className="chips">
                      {w.events.map((e) => (
                        <span key={e} className="chip">
                          {e}
                        </span>
                      ))}
                    </div>
                  </td>
                  <td className="wrap small">{w.names.length ? w.names.join(", ") : <span className="muted">all</span>}</td>
                  <td className="small">
                    {w.params.length ? `${w.params.length} parameter${w.params.length === 1 ? "" : "s"}` : "whole body"}
                  </td>
                  <td>
                    {w.source === "file" ? (
                      <Badge tone="ok">on</Badge>
                    ) : (
                      <label className="check">
                        <input type="checkbox" checked={w.enabled} onChange={() => toggle(w)} /> {w.enabled ? "on" : "off"}
                      </label>
                    )}
                  </td>
                  <td className="actions">
                    <button
                      className="button ghost small"
                      onClick={() => setEditing({ id: w.id, input: toInput(w), readOnly: w.source === "file" })}
                    >
                      {w.source === "file" ? "View & test" : "Edit & test"}
                    </button>
                    {w.source === "ui" && (
                      <button className="button danger small" onClick={() => remove(w)}>
                        Delete
                      </button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {editing && data && (
        <WebhookEditor
          key={editing.id ?? "new"}
          editing={editing}
          list={data}
          onClose={() => setEditing(undefined)}
          onSaved={() => {
            setEditing(undefined);
            list.reload();
          }}
        />
      )}
    </>
  );
}

function WebhookEditor({
  editing,
  list,
  onClose,
  onSaved,
}: {
  editing: Editing;
  list: WebhookList;
  onClose: () => void;
  onSaved: () => void;
}) {
  const [input, setInput] = useState<WebhookInput>(editing.input);
  const [error, setError] = useState<string>();
  const [saving, setSaving] = useState(false);
  const readOnly = editing.readOnly;

  const set = (change: Partial<WebhookInput>) => setInput((current) => ({ ...current, ...change }));
  const fields = [...new Set((input.events.length ? input.events : (["linked"] as WebhookEvent[])).flatMap((e) => list.fields[e]))];
  const usesRules = input.events.length === 0 || input.events.some((e) => e !== "source_error");

  function toggleEvent(event: WebhookEvent) {
    set({ events: input.events.includes(event) ? input.events.filter((e) => e !== event) : [...input.events, event] });
  }

  function toggleRule(rule: string) {
    set({ names: input.names.includes(rule) ? input.names.filter((n) => n !== rule) : [...input.names, rule] });
  }

  function setParam(index: number, change: Partial<WebhookParam>) {
    set({ params: input.params.map((p, i) => (i === index ? { ...p, ...change } : p)) });
  }

  async function save() {
    setSaving(true);
    setError(undefined);
    try {
      if (editing.id === null) await api.createWebhook(input);
      else await api.updateWebhook(editing.id, input);
      onSaved();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setSaving(false);
    }
  }

  const extraRules = input.names.filter((n) => !list.rules.includes(n));

  return (
    <div className="dialog-backdrop" onClick={onClose}>
      <div className="dialog card editor" role="dialog" aria-label="Webhook" onClick={(e) => e.stopPropagation()}>
        <div className="card-head">
          <h2>{readOnly ? "Webhook from configuration.yml" : editing.id === null ? "New webhook" : "Edit webhook"}</h2>
          <button className="button ghost" onClick={onClose}>
            Close
          </button>
        </div>
        <div className="editor-body">
          <fieldset disabled={readOnly} className="form">
            <label>
              <span>Name</span>
              <input type="text" value={input.name ?? ""} onChange={(e) => set({ name: e.target.value })} placeholder="optional" />
            </label>
            <label>
              <span>URL</span>
              <input
                type="url"
                value={input.url}
                onChange={(e) => set({ url: e.target.value })}
                placeholder="https://n8n.example.com/webhook/media"
                required
              />
            </label>

            <div className="form-row">
              <span>Events</span>
              <div className="stack">
                {list.events.map((event) => (
                  <label key={event} className="check">
                    <input type="checkbox" checked={input.events.includes(event)} onChange={() => toggleEvent(event)} />
                    <span className="mono">{event}</span> <span className="muted small">{eventInfo[event]}</span>
                  </label>
                ))}
                {input.events.length === 0 && <span className="muted small">None ticked: linked only.</span>}
              </div>
            </div>

            {usesRules && (
              <div className="form-row">
                <span>Rules</span>
                <div className="stack">
                  <div className="chips">
                    {[...list.rules, ...extraRules].map((rule) => (
                      <button
                        key={rule}
                        type="button"
                        className={input.names.includes(rule) ? "chip toggle on" : "chip toggle"}
                        onClick={() => toggleRule(rule)}
                      >
                        {rule}
                      </button>
                    ))}
                    {list.rules.length === 0 && <span className="muted small">No named rules.</span>}
                  </div>
                  <span className="muted small">
                    {input.names.length
                      ? "Only items and jobs placed by these rules are sent."
                      : "None selected: every rule. source_error ignores this."}
                  </span>
                </div>
              </div>
            )}

            <div className="form-row">
              <span>Parameters</span>
              <div className="stack">
                {input.params.length === 0 && <span className="muted small">None: the whole body is sent.</span>}
                {input.params.map((param, i) => (
                  <div key={i} className="param-row">
                    <select value={param.place} onChange={(e) => setParam(i, { place: e.target.value as WebhookParam["place"] })}>
                      <option value="body">body</option>
                      <option value="query">query</option>
                    </select>
                    <input
                      type="text"
                      value={param.name}
                      onChange={(e) => setParam(i, { name: e.target.value })}
                      placeholder="name"
                      aria-label="Parameter name"
                    />
                    <span className="muted">=</span>
                    <input
                      type="text"
                      value={param.value}
                      onChange={(e) => setParam(i, { value: e.target.value })}
                      placeholder="field"
                      list="webhook-fields"
                      aria-label="Field"
                    />
                    {!readOnly && (
                      <button
                        type="button"
                        className="button ghost small"
                        onClick={() => set({ params: input.params.filter((_, j) => j !== i) })}
                        aria-label="Remove parameter"
                      >
                        ✕
                      </button>
                    )}
                  </div>
                ))}
                {!readOnly && (
                  <button
                    type="button"
                    className="button ghost small add"
                    onClick={() => set({ params: [...input.params, { place: "body", name: "", value: "" }] })}
                  >
                    Add parameter
                  </button>
                )}
                <datalist id="webhook-fields">
                  {fields.map((f) => (
                    <option key={f} value={f} />
                  ))}
                </datalist>
              </div>
            </div>

            {!readOnly && (
              <label className="check">
                <input type="checkbox" checked={input.enabled} onChange={(e) => set({ enabled: e.target.checked })} /> Enabled
              </label>
            )}
          </fieldset>

          <ErrorBanner message={error} />
          {!readOnly && (
            <div className="toolbar">
              <button className="button" onClick={save} disabled={saving}>
                {editing.id === null ? "Create" : "Save"}
              </button>
              <button className="button ghost" onClick={onClose}>
                Cancel
              </button>
            </div>
          )}

          <WebhookTester input={input} events={list.events} />
        </div>
      </div>
    </div>
  );
}

function WebhookTester({ input, events }: { input: WebhookInput; events: WebhookEvent[] }) {
  const [event, setEvent] = useState<WebhookEvent>(input.events[0] ?? "linked");
  const [result, setResult] = useState<WebhookTestResult>();
  const [error, setError] = useState<string>();
  const [sending, setSending] = useState(false);

  async function send() {
    setSending(true);
    setError(undefined);
    setResult(undefined);
    try {
      setResult(await api.testWebhook(input, event));
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setSending(false);
    }
  }

  return (
    <Card title="Test">
      <div className="toolbar">
        <label>
          Event{" "}
          <select value={event} onChange={(e) => setEvent(e.target.value as WebhookEvent)}>
            {events.map((e) => (
              <option key={e} value={e}>
                {e}
              </option>
            ))}
          </select>
        </label>
        <button className="button ghost" onClick={send} disabled={sending || !input.url}>
          {sending ? "Sending…" : "Send test"}
        </button>
        <span className="muted small">Sends sample data as the form is now (saved or not), with an X-Convy-Test: true header.</span>
      </div>
      <ErrorBanner message={error} />
      {result && (
        <div className="test-result">
          <div className="toolbar">
            {result.ok ? (
              <Badge tone="ok">{result.status_code}</Badge>
            ) : result.status_code ? (
              <Badge tone="bad">{result.status_code}</Badge>
            ) : (
              <Badge tone="bad">no answer</Badge>
            )}
            <span className="muted small">{result.duration_ms} ms</span>
            {result.error && <span className="error-text">{result.error}</span>}
          </div>
          <div className="mono small url-cell">POST {result.url}</div>
          <h3>Request body</h3>
          <pre className="code small">{pretty(result.request_body)}</pre>
          {result.response_body !== null && (
            <>
              <h3>Response</h3>
              <pre className="code small">{pretty(result.response_body) || "(empty)"}</pre>
            </>
          )}
        </div>
      )}
    </Card>
  );
}

function pretty(text: string): string {
  try {
    return JSON.stringify(JSON.parse(text), null, 2);
  } catch {
    return text;
  }
}
