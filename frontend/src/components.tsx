import type { ReactNode } from "react";

export function Card({ title, actions, children }: { title: string; actions?: ReactNode; children: ReactNode }) {
  return (
    <section className="card">
      <div className="card-head">
        <h2>{title}</h2>
        {actions && <div className="card-actions">{actions}</div>}
      </div>
      {children}
    </section>
  );
}

export type Tone = "ok" | "warn" | "bad" | "info" | "neutral";

export function Badge({ tone, children, title }: { tone: Tone; children: ReactNode; title?: string }) {
  return (
    <span className={`badge ${tone}`} title={title}>
      {children}
    </span>
  );
}

const jobTones: Record<string, Tone> = {
  queued: "neutral",
  downloading: "info",
  stalled: "warn",
  placing: "info",
  completed: "ok",
  failed: "bad",
  cancelled: "neutral",
};

export function JobStatusBadge({ status }: { status: string }) {
  return <Badge tone={jobTones[status] ?? "neutral"}>{status}</Badge>;
}

export function ErrorBanner({ message }: { message: string | undefined }) {
  return message ? (
    <div className="banner bad" role="alert">
      {message}
    </div>
  ) : null;
}

export function Progress({ value }: { value: number | null }) {
  const clamped = Math.min(1, Math.max(0, value ?? 0));
  return (
    <div className="progress" role="progressbar" aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.round(clamped * 100)}>
      <div style={{ width: `${clamped * 100}%` }} />
    </div>
  );
}

export function Empty({ children }: { children: ReactNode }) {
  return <div className="empty">{children}</div>;
}
