import { useRef, useState } from "react";
import {
  api,
  type AvailableExtension,
  type InstalledExtension,
  type ListedSource,
  type SourceSetting,
  type SourceSettingsInput,
  type SourceUnit,
  type SourcesView,
} from "../api";
import { Badge, Card, Empty, ErrorBanner, type Tone } from "../components";
import { usePolling } from "../usePolling";

const kindLabel: Record<SourceUnit["kind"], string> = {
  gallery: "site",
  torrent: "torrents",
  soulseek: "Soulseek",
  files: "books",
  provider: "provider",
};

const statusTone: Record<string, Tone> = { ok: "ok", error: "bad", disabled: "neutral", unknown: "neutral" };

function errorText(e: unknown): string {
  return e instanceof Error ? e.message : String(e);
}

export function Sources() {
  const view = usePolling(api.sources, 10000);
  const [error, setError] = useState<string>();
  const [note, setNote] = useState<string>();
  const [busy, setBusy] = useState<string>();
  const [editing, setEditing] = useState<SourceUnit>();
  const upload = useRef<HTMLInputElement>(null);

  /** Runs an action of the page; true when it succeeded (its error is shown otherwise). */
  async function act(label: string, action: () => Promise<unknown>, done?: string): Promise<boolean> {
    setBusy(label);
    setError(undefined);
    setNote(undefined);
    try {
      await action();
      if (done) setNote(done);
      view.reload();
      return true;
    } catch (e) {
      setError(errorText(e));
      return false;
    } finally {
      setBusy(undefined);
    }
  }

  async function uploadFile(file: File | undefined) {
    if (!file) return;
    await act("upload", async () => {
      const installed = await api.uploadSource(file);
      setNote(`Installed ${installed.package} ${installed.version}.`);
    });
    if (upload.current) upload.current.value = "";
  }

  const data = view.data;

  return (
    <>
      <div className="toolbar">
        <button className="button" onClick={() => upload.current?.click()} disabled={busy !== undefined}>
          Install from file…
        </button>
        <input
          ref={upload}
          type="file"
          accept=".zip,.dll"
          hidden
          onChange={(e) => uploadFile(e.target.files?.[0])}
          aria-label="Package file"
        />
        <span className="muted small">
          Every source is an extension package{data ? <> in <span className="mono">{data.path}</span></> : null}: sites
          (the same packages as Shizuka's), Prowlarr, slskd, OPDS catalogs. Settings saved here override configuration.yml.
        </span>
      </div>
      <ErrorBanner message={error ?? view.error} />
      {note && <div className="banner ok">{note}</div>}
      {!data ? (
        !view.error && <Empty>Loading…</Empty>
      ) : (
        <>
          {data.problems.length > 0 && (
            <Card title="Packages that did not load">
              <ul className="plain-list">
                {data.problems.map((p) => (
                  <li key={p.file}>
                    <span className="mono">{p.file}</span>: <span className="error-text">{p.message}</span>{" "}
                    <button
                      className="button danger small"
                      disabled={busy !== undefined}
                      onClick={() =>
                        window.confirm(`Delete ${p.file}?`) && act(`problem:${p.file}`, () => api.removeBrokenSource(p.file))
                      }
                    >
                      Delete file
                    </button>
                  </li>
                ))}
              </ul>
            </Card>
          )}
          <InstalledCard
            extensions={data.extensions}
            busy={busy}
            onEdit={setEditing}
            onToggle={(id, enabled) => act(`toggle:${id}`, () => api.saveSourceSettings(id, { enabled }))}
            onRemove={(pkg) =>
              window.confirm(`Remove ${pkg}? Its sources go away; their settings are kept.`) &&
              act(`remove:${pkg}`, () => api.removeSource(pkg), `Removed ${pkg}.`)
            }
          />
          <AvailableCard
            busy={busy}
            hasIndexes={data.indexes.length > 0}
            onInstall={(e) =>
              act(`install:${e.package}`, async () => {
                const installed = await api.installSource(e.index, e.package);
                setNote(`Installed ${installed.package} ${installed.version}.`);
              })
            }
          />
          <IndexesCard view={data} busy={busy} act={act} />
        </>
      )}
      {editing && (
        <SettingsDialog
          key={editing.id}
          unit={editing}
          onClose={() => setEditing(undefined)}
          onSaved={() => {
            setEditing(undefined);
            setNote(`Saved the settings of ${editing.name}.`);
            view.reload();
          }}
        />
      )}
    </>
  );
}

function StatusBadge({ status, message }: { status: string; message: string | null }) {
  return (
    <Badge tone={statusTone[status] ?? "neutral"} title={message ?? undefined}>
      {status}
    </Badge>
  );
}

function InstalledCard({
  extensions,
  busy,
  onEdit,
  onToggle,
  onRemove,
}: {
  extensions: InstalledExtension[];
  busy: string | undefined;
  onEdit: (unit: SourceUnit) => void;
  onToggle: (id: string, enabled: boolean) => void;
  onRemove: (pkg: string) => void;
}) {
  return (
    <Card title="Installed">
      {extensions.length === 0 ? (
        <Empty>No extensions yet: install one from an index below or from a file.</Empty>
      ) : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Source</th>
                <th>Kind</th>
                <th>State</th>
                <th>On</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {extensions.map((e) => (
                <PackageRows key={e.package} extension={e} busy={busy} onEdit={onEdit} onToggle={onToggle} onRemove={onRemove} />
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Card>
  );
}

function PackageRows({
  extension,
  busy,
  onEdit,
  onToggle,
  onRemove,
}: {
  extension: InstalledExtension;
  busy: string | undefined;
  onEdit: (unit: SourceUnit) => void;
  onToggle: (id: string, enabled: boolean) => void;
  onRemove: (pkg: string) => void;
}) {
  return (
    <>
      <tr className="group-row">
        <td colSpan={4} className="wrap">
          <strong>{extension.package}</strong> <span className="muted small">{extension.version}</span>{" "}
          <span className="muted small mono">
            {extension.file} · API {extension.api}
            {extension.convy_api && ` · Convy API ${extension.convy_api}`}
          </span>
        </td>
        <td className="actions">
          <button className="button danger small" disabled={busy !== undefined} onClick={() => onRemove(extension.package)}>
            Remove
          </button>
        </td>
      </tr>
      {extension.units.map((u) => (
        <UnitRows key={u.id} unit={u} busy={busy} onEdit={onEdit} onToggle={onToggle} />
      ))}
    </>
  );
}

function UnitRows({
  unit,
  busy,
  onEdit,
  onToggle,
}: {
  unit: SourceUnit;
  busy: string | undefined;
  onEdit: (unit: SourceUnit) => void;
  onToggle: (id: string, enabled: boolean) => void;
}) {
  const needsSetup = unit.settings.some((s) => s.kind === "url" && !s.set);
  return (
    <>
      <tr>
        <td className="wrap">
          <div>
            {unit.name} {unit.nsfw && <Badge tone="warn">18+</Badge>}
          </div>
          <div className="mono small muted">{unit.id}</div>
          {unit.message && <div className="small error-text">{unit.message}</div>}
        </td>
        <td>{kindLabel[unit.kind]}</td>
        <td>
          {needsSetup && unit.enabled ? <Badge tone="warn">not set up</Badge> : <StatusBadge status={unit.status} message={unit.message} />}
        </td>
        <td>
          <input
            type="checkbox"
            checked={unit.enabled}
            disabled={busy !== undefined}
            onChange={(e) => onToggle(unit.id, e.target.checked)}
            aria-label={`${unit.name} on`}
          />
        </td>
        <td className="actions">
          <button className="button ghost small" onClick={() => onEdit(unit)}>
            Settings
          </button>
        </td>
      </tr>
      {unit.sources?.map((s) => <ListedRow key={s.id} source={s} busy={busy} onToggle={onToggle} />)}
      {unit.kind === "provider" && unit.sources?.length === 0 && (
        <tr>
          <td colSpan={5} className="muted small nested">
            No sources listed yet{unit.enabled ? "" : ": the provider is off"}.
          </td>
        </tr>
      )}
    </>
  );
}

function ListedRow({ source, busy, onToggle }: { source: ListedSource; busy: string | undefined; onToggle: (id: string, enabled: boolean) => void }) {
  return (
    <tr>
      <td className="wrap nested">
        <div>{source.name}</div>
        <div className="mono small muted">{source.id}</div>
      </td>
      <td>{source.kind}</td>
      <td>
        <StatusBadge status={source.status} message={source.message} />
      </td>
      <td>
        <input
          type="checkbox"
          checked={source.enabled}
          disabled={busy !== undefined}
          onChange={(e) => onToggle(source.id, e.target.checked)}
          aria-label={`${source.name} on`}
        />
      </td>
      <td />
    </tr>
  );
}

function AvailableCard({
  busy,
  hasIndexes,
  onInstall,
}: {
  busy: string | undefined;
  hasIndexes: boolean;
  onInstall: (extension: AvailableExtension) => Promise<unknown>;
}) {
  const [fresh, setFresh] = useState(0);
  const available = usePolling(() => api.availableSources(fresh > 0), 0, [fresh, busy === undefined]);
  const data = available.data;

  return (
    <Card
      title="Available"
      actions={
        <button className="button ghost small" onClick={() => setFresh((n) => n + 1)} disabled={available.loading}>
          Refresh
        </button>
      }
    >
      <ErrorBanner message={available.error} />
      {data?.errors.map((e) => (
        <div key={e.index} className="banner bad">
          <span className="mono">{e.index}</span>: {e.message}
        </div>
      ))}
      {!hasIndexes ? (
        <Empty>No indexes: add one below.</Empty>
      ) : !data ? (
        !available.error && <Empty>Loading…</Empty>
      ) : data.extensions.length === 0 ? (
        <Empty>The indexes have nothing for Convy.</Empty>
      ) : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Package</th>
                <th>Sources</th>
                <th>Version</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {data.extensions.map((e) => (
                <tr key={`${e.index}|${e.package}`}>
                  <td className="wrap">
                    <div>
                      {e.name} {e.nsfw && <Badge tone="warn">18+</Badge>}
                    </div>
                    <div className="mono small muted">{e.package}</div>
                  </td>
                  <td className="wrap small">{e.sources.map((s) => s.name).join(", ")}</td>
                  <td className="small">
                    {e.version}
                    {e.installed && <div className="muted">installed {e.installed}</div>}
                  </td>
                  <td className="actions">
                    {e.incompatible ? (
                      <Badge tone="warn" title={e.incompatible}>
                        incompatible
                      </Badge>
                    ) : e.installed && !e.update ? (
                      <Badge tone="ok">installed</Badge>
                    ) : (
                      <button className="button small" disabled={busy !== undefined} onClick={() => onInstall(e)}>
                        {busy === `install:${e.package}` ? "Installing…" : e.update ? "Update" : "Install"}
                      </button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Card>
  );
}

function IndexesCard({
  view,
  busy,
  act,
}: {
  view: SourcesView;
  busy: string | undefined;
  act: (label: string, action: () => Promise<unknown>, done?: string) => Promise<boolean>;
}) {
  const [url, setUrl] = useState("");
  const [auth, setAuth] = useState<"none" | "basic" | "bearer">("none");
  const [username, setUsername] = useState("");
  const [secret, setSecret] = useState("");

  async function add() {
    const added = await act(
      "index:add",
      () =>
        api.addSourceIndex({
          url,
          username: auth === "basic" ? username : undefined,
          password: auth === "basic" ? secret : undefined,
          token: auth === "bearer" ? secret : undefined,
        }),
      "Index added.",
    );
    // A refused index keeps what was typed, the token or password included.
    if (!added) return;
    setUrl("");
    setUsername("");
    setSecret("");
  }

  return (
    <Card title="Indexes">
      {view.indexes.length > 0 && (
        <ul className="plain-list">
          {view.indexes.map((i) => (
            <li key={i.url}>
              <span className="mono">{i.url}</span> {i.source === "file" && <Badge tone="neutral">configuration.yml</Badge>}{" "}
              {i.auth !== "none" && <Badge tone="info">{i.auth}</Badge>}{" "}
              {i.source === "ui" && (
                <button
                  className="button ghost small"
                  disabled={busy !== undefined}
                  onClick={() => act(`index:${i.url}`, () => api.removeSourceIndex(i.url))}
                >
                  Remove
                </button>
              )}
            </li>
          ))}
        </ul>
      )}
      <div className="form">
        <label>
          <span>Index</span>
          <input type="url" value={url} onChange={(e) => setUrl(e.target.value)} placeholder="https://…/index.json" />
        </label>
        <label>
          <span>Sign-in</span>
          <select value={auth} onChange={(e) => setAuth(e.target.value as typeof auth)}>
            <option value="none">none</option>
            <option value="basic">user name and password</option>
            <option value="bearer">token</option>
          </select>
        </label>
        {auth === "basic" && (
          <label>
            <span>User name</span>
            <input type="text" value={username} onChange={(e) => setUsername(e.target.value)} autoComplete="off" />
          </label>
        )}
        {auth !== "none" && (
          <label>
            <span>{auth === "basic" ? "Password" : "Token"}</span>
            <input type="password" value={secret} onChange={(e) => setSecret(e.target.value)} autoComplete="new-password" />
          </label>
        )}
        <div className="form-row">
          <span />
          <div>
            <button className="button" onClick={add} disabled={!url || busy !== undefined}>
              Add index
            </button>
          </div>
        </div>
      </div>
    </Card>
  );
}

function SettingsDialog({ unit, onClose, onSaved }: { unit: SourceUnit; onClose: () => void; onSaved: () => void }) {
  const [values, setValues] = useState<Record<string, string>>({});
  const [proxy, setProxy] = useState(unit.proxy ?? "");
  const [timeout, setTimeoutValue] = useState(String(unit.timeout_sec));
  const [error, setError] = useState<string>();
  const [saving, setSaving] = useState(false);
  const [login, setLogin] = useState<Record<string, string>>({});
  const [loginNote, setLoginNote] = useState<string>();

  async function save() {
    setSaving(true);
    setError(undefined);
    try {
      const input: SourceSettingsInput = { values };
      if (proxy !== (unit.proxy ?? "")) input.proxy = proxy;
      if (Number(timeout) !== unit.timeout_sec) input.timeout_sec = Number(timeout);
      await api.saveSourceSettings(unit.id, input);
      onSaved();
    } catch (e) {
      setError(errorText(e));
    } finally {
      setSaving(false);
    }
  }

  async function signIn() {
    setSaving(true);
    setError(undefined);
    setLoginNote(undefined);
    try {
      await api.signInSource(unit.id, login);
      setLogin({});
      setLoginNote("Signed in: the cookies are saved.");
    } catch (e) {
      setError(errorText(e));
    } finally {
      setSaving(false);
    }
  }

  return (
    <div className="dialog-backdrop" onClick={onClose}>
      <div className="dialog card editor" role="dialog" aria-label={`${unit.name} settings`} onClick={(e) => e.stopPropagation()}>
        <div className="card-head">
          <h2>{unit.name}</h2>
          <button className="button ghost" onClick={onClose}>
            Close
          </button>
        </div>
        <div className="editor-body">
          {unit.domains.length > 0 && (
            <p className="muted small">
              Sends requests to <span className="mono">{unit.domains.join(", ")}</span>
              {unit.settings.some((s) => s.kind === "url") && " and the addresses set below"}.
            </p>
          )}
          {unit.kind === "provider" && <p className="muted small">The sources it lists take these settings.</p>}
          <div className="form">
            {unit.settings.map((s) => (
              <SettingField key={s.key} setting={s} value={values[s.key]} onChange={(v) =>
                  setValues((c) => {
                    const next = { ...c };
                    if (v === undefined) delete next[s.key];
                    else next[s.key] = v;
                    return next;
                  })
                } />
            ))}
            <label>
              <span>Proxy</span>
              <input type="text" value={proxy} onChange={(e) => setProxy(e.target.value)} placeholder="socks5://tor:9050 (needs a restart)" />
            </label>
            <label>
              <span>Timeout, s</span>
              <input type="text" inputMode="numeric" value={timeout} onChange={(e) => setTimeoutValue(e.target.value)} />
            </label>
          </div>
          <ErrorBanner message={error} />
          <div className="toolbar">
            <button className="button" onClick={save} disabled={saving}>
              Save
            </button>
            <button className="button ghost" onClick={onClose}>
              Cancel
            </button>
          </div>
          {unit.login && (
            <Card title="Sign in">
              {unit.login.description && <p className="muted small">{unit.login.description}</p>}
              <div className="form">
                {unit.login.fields.map((f) => (
                  <label key={f.key}>
                    <span>{f.label}</span>
                    <input
                      type={f.kind === "secret" ? "password" : "text"}
                      value={login[f.key] ?? ""}
                      onChange={(e) => setLogin((c) => ({ ...c, [f.key]: e.target.value }))}
                      autoComplete="off"
                    />
                  </label>
                ))}
              </div>
              <div className="toolbar">
                <button className="button ghost" onClick={signIn} disabled={saving}>
                  Sign in
                </button>
                <span className="muted small">What is typed here is not stored, only the cookies the site gives.</span>
              </div>
              {loginNote && <div className="banner ok">{loginNote}</div>}
            </Card>
          )}
        </div>
      </div>
    </div>
  );
}

function SettingField({ setting, value, onChange }: { setting: SourceSetting; value: string | undefined; onChange: (value: string | undefined) => void }) {
  const secret = setting.kind === "secret" || setting.kind === "cookies";
  const current = value ?? (secret ? "" : (setting.value ?? ""));
  const hint = setting.description && <span className="muted small">{setting.description}</span>;

  if (setting.kind === "flag") {
    const checked = (value ?? setting.value ?? setting.default ?? "false") === "true";
    return (
      <div className="form-row">
        <span>{setting.label}</span>
        <div className="stack">
          <label className="check">
            <input type="checkbox" checked={checked} onChange={(e) => onChange(e.target.checked ? "true" : "false")} /> on
          </label>
          {hint}
        </div>
      </div>
    );
  }

  if (setting.kind === "choice") {
    return (
      <div className="form-row">
        <span>{setting.label}</span>
        <div className="stack">
          <select value={value ?? setting.value ?? setting.default ?? ""} onChange={(e) => onChange(e.target.value)}>
            <option value="">—</option>
            {setting.options.map((o) => (
              <option key={o} value={o}>
                {o}
              </option>
            ))}
          </select>
          {hint}
        </div>
      </div>
    );
  }

  return (
    <div className="form-row">
      <span>{setting.label}</span>
      <div className="stack">
        <input
          type={secret ? "password" : setting.kind === "url" ? "url" : "text"}
          value={current}
          // An erased secret keeps the saved one: only "clear" clears it.
          onChange={(e) => onChange(secret && e.target.value === "" ? undefined : e.target.value)}
          placeholder={secret ? (setting.set ? "saved: type to replace" : "not set") : (setting.default ?? "")}
          autoComplete={secret ? "new-password" : "off"}
        />
        {secret && setting.set && (
          <label className="check">
            <input type="checkbox" checked={value === ""} onChange={(e) => onChange(e.target.checked ? "" : undefined)} /> clear
          </label>
        )}
        {hint}
      </div>
    </div>
  );
}
