// Display helpers shared by the pages.

const units = ["B", "KB", "MB", "GB", "TB"];

export function bytes(value: number | null | undefined): string {
  if (value === null || value === undefined) return "—";
  let size = value;
  let unit = 0;
  while (size >= 1024 && unit < units.length - 1) {
    size /= 1024;
    unit++;
  }
  return `${size >= 100 || unit === 0 ? Math.round(size) : size.toFixed(1)} ${units[unit]}`;
}

export function speed(value: number | null | undefined): string {
  return value ? `${bytes(value)}/s` : "—";
}

export function percent(value: number | null | undefined): string {
  return value === null || value === undefined ? "—" : `${Math.round(value * 100)}%`;
}

const dateTime = new Intl.DateTimeFormat(undefined, {
  year: "numeric",
  month: "2-digit",
  day: "2-digit",
  hour: "2-digit",
  minute: "2-digit",
  second: "2-digit",
});

const timeOnly = new Intl.DateTimeFormat(undefined, { hour: "2-digit", minute: "2-digit", second: "2-digit" });

export function dateTimeText(value: string | null | undefined): string {
  if (!value) return "—";
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? value : dateTime.format(date);
}

export function timeText(value: string): string {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? value : timeOnly.format(date);
}

/** "12 s ago", "5 min ago", "3 h ago", "2 d ago"; relative to the server's clock when given. */
export function ago(value: string | null | undefined, now?: string): string {
  if (!value) return "never";
  const reference = now ? new Date(now).getTime() : Date.now();
  const seconds = Math.max(0, Math.round((reference - new Date(value).getTime()) / 1000));
  if (seconds < 60) return `${seconds} s ago`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)} min ago`;
  if (seconds < 86400) return `${Math.floor(seconds / 3600)} h ago`;
  return `${Math.floor(seconds / 86400)} d ago`;
}

export function duration(seconds: number): string {
  if (seconds < 60) return `${Math.round(seconds)} s`;
  if (seconds < 3600) return `${Math.round(seconds / 60)} min`;
  return `${(seconds / 3600).toFixed(seconds % 3600 === 0 ? 0 : 1)} h`;
}
