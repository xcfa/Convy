import { useCallback, useEffect, useRef, useState } from "react";

export interface Polled<T> {
  data: T | undefined;
  error: string | undefined;
  loading: boolean;
  reload: () => void;
}

/**
 * Loads `load()` now and then every `intervalMs` while the tab is visible. A change in `deps`
 * starts over. A tick is skipped while a request is still running, so a slow endpoint is
 * never cut off by the next tick; only the newest request may update the state.
 */
export function usePolling<T>(load: () => Promise<T>, intervalMs: number, deps: unknown[] = []): Polled<T> {
  const [data, setData] = useState<T>();
  const [error, setError] = useState<string>();
  const [loading, setLoading] = useState(true);
  const latest = useRef(0);
  const inFlight = useRef(0);
  const loadRef = useRef(load);
  loadRef.current = load;

  const run = useCallback(async () => {
    const current = ++latest.current;
    inFlight.current++;
    try {
      const result = await loadRef.current();
      if (current !== latest.current) return;
      setData(result);
      setError(undefined);
    } catch (e) {
      if (current !== latest.current) return;
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      inFlight.current--;
      if (current === latest.current) setLoading(false);
    }
  }, []);

  useEffect(() => {
    setLoading(true);
    void run();

    if (intervalMs <= 0) {
      return () => void latest.current++;
    }

    const tick = () => {
      if (document.visibilityState === "visible" && inFlight.current === 0) void run();
    };
    const timer = window.setInterval(tick, intervalMs);
    document.addEventListener("visibilitychange", tick);

    return () => {
      window.clearInterval(timer);
      document.removeEventListener("visibilitychange", tick);
      // Drop answers to requests made for the old parameters.
      latest.current++;
    };
  }, [intervalMs, run, ...deps]);

  return { data, error, loading, reload: () => void run() };
}
