const state = {
  snapshot: null
};

const byId = (id) => document.getElementById(id);
const temp = (value) => Number.isFinite(value) ? `${value.toFixed(1)} C` : "--";
const shortTime = (value) => value ? new Date(value).toLocaleTimeString() : "--";
const text = (id, value) => {
  const element = byId(id);
  if (element) {
    element.textContent = value;
  }
};

async function request(path, method = "GET", body = null) {
  const response = await fetch(path, {
    method,
    headers: body ? { "Content-Type": "application/json" } : undefined,
    body: body ? JSON.stringify(body) : undefined
  });

  const payload = await response.json().catch(() => null);
  if (!response.ok) {
    if (payload) {
      render(payload);
    }

    throw new Error(payload?.lastError || `${response.status} ${response.statusText}`);
  }

  return payload;
}

function render(snapshot) {
  state.snapshot = snapshot;
  const target = Number(snapshot.targetTemperatureCelsius);
  const ha = snapshot.homeAssistantThermostat;
  const connection = snapshot.connectionState || "unavailable";
  const hasHomeAssistant = connection === "home-assistant" && ha;

  text("targetTemp", Number.isFinite(target) ? target.toFixed(1) : "--");
  if (document.activeElement !== byId("targetInput")) {
    byId("targetInput").value = Number.isFinite(target) ? target.toFixed(1) : "";
  }

  byId("defenderToggle").checked = snapshot.defenderEnabled;
  text("connectionState", connection);
  text("headerStatus", snapshot.defenderEnabled
    ? hasHomeAssistant ? "Defender active" : "Home Assistant unavailable"
    : "Paused");

  text("haEntity", snapshot.homeAssistantEntityId || "No entity");
  text("haCurrent", ha ? temp(Number(ha.currentTemperatureCelsius)) : "--");
  text("haSetpoint", ha ? temp(Number(ha.setPointCelsius)) : "--");
  text("haMode", ha?.hvacMode || "--");
  text("haAction", ha?.hvacAction || "--");
  text("lastCommand", snapshot.lastCommand || "No commands yet");
  text("lastError", snapshot.lastError || "");

  text("boostOffset", Number.isFinite(Number(snapshot.boostOffsetCelsius))
    ? `${Number(snapshot.boostOffsetCelsius).toFixed(1)} C`
    : "--");
  text("haUpdated", ha ? shortTime(ha.updatedAt) : "--");
  text("realEntity", snapshot.homeAssistantEntityId || "--");

  const events = byId("eventLog");
  events.replaceChildren(...(snapshot.events || []).map((event) => {
    const item = document.createElement("li");
    item.className = event.level || "info";

    const time = document.createElement("time");
    time.dateTime = event.timestamp;
    time.textContent = new Date(event.timestamp).toLocaleString();

    const message = document.createElement("div");
    message.textContent = event.message;

    item.append(time, message);
    return item;
  }));
}

async function refresh() {
  render(await request("/api/status"));
}

async function mutate(path, body = null) {
  render(await request(path, "POST", body));
}

function showClientError(error) {
  text("lastError", error.message);
}

function initializeDashboard() {
  byId("generateTarget").addEventListener("click", () => {
    mutate("/api/target/generate").catch(showClientError);
  });

  byId("applyTarget").addEventListener("click", () => {
    mutate("/api/target", {
      temperatureCelsius: Number(byId("targetInput").value)
    }).catch(showClientError);
  });

  byId("defenderToggle").addEventListener("change", (event) => {
    mutate("/api/defender", { enabled: event.target.checked }).catch(showClientError);
  });

  byId("refreshReal").addEventListener("click", () => {
    mutate("/api/thermostat/refresh").catch(showClientError);
  });

  byId("forceTarget").addEventListener("click", () => {
    mutate("/api/thermostat/force-target").catch(showClientError);
  });

  byId("forceBoost").addEventListener("click", () => {
    mutate("/api/thermostat/force-boost").catch(showClientError);
  });

  refresh().catch(showClientError);
  setInterval(() => refresh().catch(showClientError), 1000);
}

if (document.readyState === "loading") {
  document.addEventListener("DOMContentLoaded", initializeDashboard, { once: true });
} else {
  initializeDashboard();
}
