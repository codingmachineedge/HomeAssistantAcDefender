import { useCallback, useEffect, useRef, useState } from "react";
import { api, DefenderSnapshot } from "./api";
import "./App.css";

type Phase = "login" | "connecting" | "live";

function fmtHours(hours?: number): string {
  if (hours === undefined || hours === null) return "--";
  if (hours >= 10) return `${hours.toFixed(1)}h`;
  const h = Math.floor(hours);
  const m = Math.round((hours % 1) * 60);
  return `${h}h ${m}m`;
}

function fmtMoney(dollars?: number): string {
  return dollars === undefined || dollars === null ? "--" : `≈ $${dollars.toFixed(2)}`;
}

export default function App() {
  const [phase, setPhase] = useState<Phase>("login");
  const [baseUrl, setBaseUrl] = useState("http://192.168.50.242:8888");
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [remember, setRemember] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [snapshot, setSnapshot] = useState<DefenderSnapshot | null>(null);
  const [pendingTarget, setPendingTarget] = useState<number | null>(null);
  const [busy, setBusy] = useState(false);
  const pollRef = useRef<ReturnType<typeof setInterval> | null>(null);

  // Prefill from the saved connection file (password only if "remember" was on).
  useEffect(() => {
    api
      .loadConfig()
      .then((config) => {
        if (config.base_url) setBaseUrl(config.base_url);
        if (config.username) setUsername(config.username);
        if (config.password) setPassword(config.password);
      })
      .catch(() => undefined);
  }, []);

  const applySnapshot = useCallback((next: DefenderSnapshot) => {
    setSnapshot(next);
    setError(null);
  }, []);

  const stopPolling = useCallback(() => {
    if (pollRef.current) {
      clearInterval(pollRef.current);
      pollRef.current = null;
    }
  }, []);

  const startPolling = useCallback(() => {
    stopPolling();
    pollRef.current = setInterval(async () => {
      try {
        applySnapshot(await api.getStatus());
      } catch (e) {
        setError(String(e));
      }
    }, 2000);
  }, [applySnapshot, stopPolling]);

  useEffect(() => stopPolling, [stopPolling]);

  async function connect() {
    setPhase("connecting");
    setError(null);
    try {
      applySnapshot(await api.connect(baseUrl, username, password, remember));
      setPhase("live");
      startPolling();
    } catch (e) {
      setError(String(e));
      setPhase("login");
    }
  }

  async function run(action: () => Promise<DefenderSnapshot>) {
    setBusy(true);
    try {
      applySnapshot(await action());
    } catch (e) {
      setError(String(e));
    } finally {
      setBusy(false);
    }
  }

  if (phase !== "live") {
    return (
      <main className="login">
        <div className="login__card">
          <div className="login__badge">🛡️</div>
          <h1>AC Defender</h1>
          <p className="sub">Sign in to the defender that guards your AC.</p>
          <label>
            Defender address
            <input value={baseUrl} onChange={(e) => setBaseUrl(e.target.value)} placeholder="http://192.168.50.242:8888" />
          </label>
          <label>
            Username
            <input value={username} onChange={(e) => setUsername(e.target.value)} autoCapitalize="off" />
          </label>
          <label>
            Password
            <input type="password" value={password} onChange={(e) => setPassword(e.target.value)} onKeyDown={(e) => e.key === "Enter" && connect()} />
          </label>
          <label className="check">
            <input type="checkbox" checked={remember} onChange={(e) => setRemember(e.target.checked)} />
            Remember password on this computer
          </label>
          {error && <div className="error">{error}</div>}
          <button className="primary" disabled={phase === "connecting"} onClick={connect}>
            {phase === "connecting" ? "Connecting…" : "Connect"}
          </button>
        </div>
      </main>
    );
  }

  const thermostat = snapshot?.homeAssistantThermostat;
  const runtime = snapshot?.acRuntime;
  const budget = snapshot?.electricityBudget;
  const target = pendingTarget ?? snapshot?.targetTemperatureCelsius ?? 22;
  const haOnline = snapshot?.connectionState === "connected" || !!thermostat;

  return (
    <main className="app">
      <header className="topbar">
        <div className="topbar__brand">🛡️ AC DEFENDER</div>
        <div className={`pill ${haOnline ? "pill--ok" : "pill--warn"}`}>{haOnline ? "HA ONLINE" : "HA OFFLINE"}</div>
        <div className={`pill ${snapshot?.defenderEnabled ? "pill--ok" : "pill--off"}`}>
          {snapshot?.defenderEnabled ? "DEFENDING" : "STOOD DOWN"}
        </div>
        <div className="spacer" />
        <button className="ghost" disabled={busy} onClick={() => run(api.refresh)}>⟳ Refresh</button>
      </header>

      {error && <div className="error error--bar">{error}</div>}

      <section className="grid">
        <div className="card card--hero">
          <div className="card__label">LIVE WALL UNIT</div>
          <div className="hero">
            <div className="hero__temp">{thermostat ? thermostat.currentTemperatureCelsius.toFixed(1) : "--"}<span>°C room</span></div>
            <div className="hero__meta">
              <div>Setpoint <strong>{thermostat ? thermostat.setPointCelsius.toFixed(1) : "--"} °C</strong></div>
              <div>Mode <strong>{thermostat?.hvacMode ?? "--"}</strong></div>
              <div>Action <strong className={thermostat?.hvacAction === "cooling" ? "cooling" : ""}>{thermostat?.hvacAction ?? "--"}</strong></div>
              <div>Fan <strong>{thermostat?.fanMode ?? "--"}</strong></div>
            </div>
          </div>
          <div className="next-action">{snapshot?.nextAction}</div>
        </div>

        <div className="card">
          <div className="card__label">TEMP I WANT — MY NUMBER</div>
          <div className="target">
            <button className="step" disabled={busy} onClick={() => setPendingTarget(Math.round((target - 0.5) * 10) / 10)}>−</button>
            <div className="target__value">{target.toFixed(1)}<span>°C</span></div>
            <button className="step" disabled={busy} onClick={() => setPendingTarget(Math.round((target + 0.5) * 10) / 10)}>+</button>
          </div>
          <button
            className="primary"
            disabled={busy || pendingTarget === null}
            onClick={() => run(async () => { const s = await api.setTarget(target); setPendingTarget(null); return s; })}
          >
            APPLY MY TEMP
          </button>
          <div className="row">
            <button className="ghost" disabled={busy} onClick={() => run(api.forceTarget)}>Step toward my temp</button>
            <button className="ghost" disabled={busy} onClick={() => run(api.forceBoost)}>Force cooling</button>
          </div>
        </div>

        <div className="card">
          <div className="card__label">MASTER SWITCH</div>
          <button
            className={snapshot?.defenderEnabled ? "big-toggle big-toggle--on" : "big-toggle"}
            disabled={busy}
            onClick={() => run(() => api.setDefender(!snapshot?.defenderEnabled))}
          >
            {snapshot?.defenderEnabled ? "🛡️ DEFENDING — tap to stand down" : "😴 STOOD DOWN — tap to defend"}
          </button>
          <button className="danger" disabled={busy} onClick={() => run(api.thermostatOff)}>Turn thermostat OFF</button>
          <div className="row">
            <button className="ghost" disabled={busy} onClick={() => run(() => api.emergency("too-cold"))}>🥶 Too cold</button>
            <button className="ghost" disabled={busy} onClick={() => run(() => api.emergency("brother-mad"))}>🙇 Brother mad</button>
          </div>
        </div>

        <div className="card">
          <div className="card__label">AC RUNTIME — HOURS COOLING</div>
          <div className="stats">
            {([
              ["TODAY", runtime?.todayHours, runtime?.estimatedCostTodayDollars],
              ["THIS MONTH", runtime?.monthHours, runtime?.estimatedCostMonthDollars],
              ["LIFETIME", runtime?.lifetimeHours, runtime?.estimatedCostLifetimeDollars],
            ] as const).map(([label, hours, cost]) => (
              <div className="stat" key={label}>
                <div className="stat__value">{fmtHours(hours)}</div>
                {runtime?.estimatedCostEnabled && <div className="stat__cost">{fmtMoney(cost)}</div>}
                <div className="stat__label">{label}</div>
              </div>
            ))}
          </div>
          {budget?.enabled && (
            <div className="budget">💰 {budget.status}</div>
          )}
        </div>

        <div className="card card--wide">
          <div className="card__label">RECENT ACTIVITY</div>
          <ul className="events">
            {(snapshot?.events ?? []).slice(0, 8).map((event, index) => (
              <li key={index} className={`event event--${event.level}`}>
                <span className="event__time">{new Date(event.timestamp).toLocaleTimeString([], { hour12: false })}</span>
                {event.message}
              </li>
            ))}
          </ul>
        </div>
      </section>
    </main>
  );
}
