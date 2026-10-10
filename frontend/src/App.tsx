import { useEffect, useState } from "react";
import { api, type UiUser } from "./api";
import { Overview } from "./pages/Overview";
import { Jobs } from "./pages/Jobs";
import { Logs } from "./pages/Logs";
import { Data } from "./pages/Data";
import { Webhooks } from "./pages/Webhooks";
import { Rules } from "./pages/Rules";
import { readRoute, tabs } from "./route";

export function App() {
  const [route, setRoute] = useState(readRoute);
  const [user, setUser] = useState<UiUser>();

  useEffect(() => {
    const onHash = () => setRoute(readRoute());
    window.addEventListener("hashchange", onHash);
    return () => window.removeEventListener("hashchange", onHash);
  }, []);

  useEffect(() => {
    api.me().then(setUser, () => undefined);
  }, []);

  async function signOut() {
    try {
      const { redirect } = await api.signOut();
      window.location.assign(redirect);
    } catch {
      window.location.assign("/auth/signed-out");
    }
  }

  return (
    <div className="app">
      <header className="topbar">
        <div className="brand">
          <span className="logo" aria-hidden="true" />
          Convy
        </div>
        <nav className="tabs" aria-label="Sections">
          {tabs.map((t) => (
            <a key={t.id} href={`#/${t.id}`} className={route.tab === t.id ? "tab active" : "tab"}>
              {t.label}
            </a>
          ))}
        </nav>
        <div className="user">
          {user?.auth === "oidc" && (
            <>
              <span className="muted" title={user.groups.length ? `Groups: ${user.groups.join(", ")}` : undefined}>
                {user.name ?? "signed in"}
              </span>
              <button className="button ghost" onClick={signOut}>
                Sign out
              </button>
            </>
          )}
        </div>
      </header>
      <main className="content">
        {route.tab === "overview" && <Overview />}
        {route.tab === "jobs" && <Jobs status={route.params.get("status") ?? ""} />}
        {route.tab === "logs" && <Logs />}
        {route.tab === "data" && <Data table={route.params.get("table")} />}
        {route.tab === "webhooks" && <Webhooks />}
        {route.tab === "rules" && <Rules />}
      </main>
    </div>
  );
}
