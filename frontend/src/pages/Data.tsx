import { useEffect, useState } from "react";
import { api, type DataColumn, type DataTable } from "../api";
import { Empty, ErrorBanner } from "../components";
import { bytes, dateTimeText } from "../format";
import { href } from "../route";
import { usePolling } from "../usePolling";

const pageSize = 50;

export function Data({ table }: { table: string | null }) {
  const tables = usePolling(api.tables, 0);
  const selected = table ?? tables.data?.[0]?.name ?? null;

  return (
    <div className="data-page">
      <aside className="card flush table-list" aria-label="Tables">
        <ErrorBanner message={tables.error} />
        {tables.data?.map((t) => (
          <a key={t.name} href={href("data", { table: t.name })} className={t.name === selected ? "active" : undefined}>
            <span>{t.name}</span>
            <span className="muted small">{t.rows}</span>
          </a>
        ))}
      </aside>
      <section className="data-main">
        {selected ? (
          <TableView key={selected} name={selected} info={tables.data?.find((t) => t.name === selected)} />
        ) : (
          !tables.error && <Empty>Loading…</Empty>
        )}
      </section>
    </div>
  );
}

function TableView({ name, info }: { name: string; info: DataTable | undefined }) {
  const [search, setSearch] = useState("");
  const [query, setQuery] = useState("");
  const [sort, setSort] = useState<string | null>(null);
  const [desc, setDesc] = useState(true);
  const [offset, setOffset] = useState(0);
  const [detail, setDetail] = useState<{ column: string; value: string }>();

  useEffect(() => {
    const timer = window.setTimeout(() => {
      setQuery(search.trim());
      setOffset(0);
    }, 300);
    return () => window.clearTimeout(timer);
  }, [search]);

  const { data, error, loading, reload } = usePolling(
    () => api.table(name, query, sort, desc, offset, pageSize),
    0,
    [name, query, sort, desc, offset],
  );

  function toggleSort(column: string) {
    if (sort === column) setDesc(!desc);
    else {
      setSort(column);
      setDesc(false);
    }
    setOffset(0);
  }

  const columns = data?.columns ?? info?.columns ?? [];
  const total = data?.total ?? 0;

  return (
    <>
      <div className="toolbar">
        <h2 className="table-title">{name}</h2>
        <input
          type="search"
          placeholder="Search text columns"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          aria-label={`Search ${name}`}
        />
        <button className="button ghost" onClick={reload}>
          Refresh
        </button>
        {sort && (
          <button className="button ghost" onClick={() => (setSort(null), setDesc(true), setOffset(0))}>
            Newest first
          </button>
        )}
        <span className="spacer" />
        <Pager offset={offset} total={total} onChange={setOffset} />
      </div>
      <ErrorBanner message={error} />
      {!data && loading ? (
        <Empty>Loading…</Empty>
      ) : data && data.rows.length === 0 ? (
        <Empty>{query ? "No rows match." : "The table is empty."}</Empty>
      ) : (
        <div className="table-wrap card flush">
          <table className="data-table">
            <thead>
              <tr>
                {columns.map((c) => (
                  <th key={c.name} className={c.kind === "number" ? "num" : undefined}>
                    <button className="sort" onClick={() => toggleSort(c.name)} title={`Sort by ${c.name}`}>
                      {c.name}
                      {sort === c.name ? (desc ? " ↓" : " ↑") : ""}
                    </button>
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {data?.rows.map((row, i) => (
                <tr key={`${offset}-${i}`}>
                  {columns.map((c, j) => (
                    <Cell key={c.name} column={c} value={row[j]} onOpen={(value) => setDetail({ column: c.name, value })} />
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {detail && (
        <div className="dialog-backdrop" onClick={() => setDetail(undefined)}>
          <div className="dialog card" role="dialog" aria-label={detail.column} onClick={(e) => e.stopPropagation()}>
            <div className="card-head">
              <h2>{detail.column}</h2>
              <button className="button ghost" onClick={() => setDetail(undefined)}>
                Close
              </button>
            </div>
            <pre>{pretty(detail.value)}</pre>
          </div>
        </div>
      )}
    </>
  );
}

function Cell({ column, value, onOpen }: { column: DataColumn; value: unknown; onOpen: (value: string) => void }) {
  if (value === null || value === undefined) return <td className="null">null</td>;

  switch (column.kind) {
    case "bool":
      return <td>{value ? "true" : "false"}</td>;
    case "number":
      return <td className="num">{String(value)}</td>;
    case "blob":
      return <td className="muted">{bytes(Number(value))} blob</td>;
    case "time":
    case "unix_time":
      return (
        <td className="nowrap" title={String(value)}>
          {dateTimeText(String(value))}
        </td>
      );
    default: {
      const text = String(value);
      return text.length > 80 || text.includes("\n") ? (
        <td>
          <button className="cell-more" onClick={() => onOpen(text)} title="Show the whole value">
            {text.slice(0, 80)}…
          </button>
        </td>
      ) : (
        <td className="mono-ish">{text}</td>
      );
    }
  }
}

function Pager({ offset, total, onChange }: { offset: number; total: number; onChange: (offset: number) => void }) {
  if (total === 0) return null;
  const last = Math.min(offset + pageSize, total);
  return (
    <div className="pager">
      <button className="button ghost" disabled={offset === 0} onClick={() => onChange(Math.max(0, offset - pageSize))}>
        ‹ Prev
      </button>
      <span className="small">
        {offset + 1}–{last} of {total}
      </span>
      <button className="button ghost" disabled={last >= total} onClick={() => onChange(offset + pageSize)}>
        Next ›
      </button>
    </div>
  );
}

function pretty(value: string): string {
  const trimmed = value.trim();
  if ((trimmed.startsWith("{") || trimmed.startsWith("[")) && !trimmed.endsWith("…")) {
    try {
      return JSON.stringify(JSON.parse(trimmed), null, 2);
    } catch {
      // Not JSON after all.
    }
  }
  return value;
}
