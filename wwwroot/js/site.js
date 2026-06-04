const state = {
  snapshot: null
};

const byId = (id) => document.getElementById(id);
const temp = (value) => Number.isFinite(value) ? `${value.toFixed(1)} C` : "--";
const time = (value) => value ? new Date(value).toLocaleTimeString() : "--";
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

  if (!response.ok) {
    throw new Error(`${response.status} ${response.statusText}`);
  }

  return await response.json();
}

function render(snapshot) {
  state.snapshot = snapshot;
  const target = Number(snapshot.targetTemperatureCelsius);
  const dummy = snapshot.dummyThermostat;
  const ha = snapshot.homeAssistantThermostat;

  text("targetTemp", Number.isFinite(target) ? target.toFixed(1) : "--");
  if (document.activeElement !== byId("targetInput")) {
    byId("targetInput").value = Number.isFinite(target) ? target.toFixed(1) : "";
  }
  byId("defenderToggle").checked = snapshot.defenderEnabled;
  text("activeSource", snapshot.activeSource || "--");
  text("headerStatus", snapshot.defenderEnabled ? "Defender active" : "Paused");

  text("haEntity", snapshot.homeAssistantEntityId || "Dummy mode");
  text("haCurrent", ha ? temp(Number(ha.currentTemperatureCelsius)) : "--");
  text("haSetpoint", ha ? temp(Number(ha.setPointCelsius)) : "--");
  text("haMode", ha?.hvacMode || "--");
  text("haAction", ha?.hvacAction || "--");
  text("lastCommand", snapshot.lastCommand || "No commands yet");
  text("lastError", snapshot.lastError || "");

  text("dummyCurrent", temp(Number(dummy.currentTemperatureCelsius)));
  text("dummySetpoint", temp(Number(dummy.setPointCelsius)));
  text("dummyAction", dummy.hvacAction || "--");
  text("dummyModeReadout", dummy.hvacMode || "--");
  text("dummyUpdated", time(dummy.updatedAt));
  if (document.activeElement !== byId("dummyMode")) {
    byId("dummyMode").value = dummy.hvacMode || "cool";
  }

  const events = byId("eventLog");
  events.replaceChildren(...snapshot.events.map((event) => {
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

function dummyChange(setPointDelta, currentDelta) {
  const dummy = state.snapshot?.dummyThermostat;
  if (!dummy) {
    return;
  }

  mutate("/api/dummy", {
    setPointCelsius: Number(dummy.setPointCelsius) + setPointDelta,
    currentTemperatureCelsius: Number(dummy.currentTemperatureCelsius) + currentDelta,
    hvacMode: byId("dummyMode").value
  }).catch(showClientError);
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

  byId("dummyMode").addEventListener("change", () => dummyChange(0, 0));
  byId("dummySetDown").addEventListener("click", () => dummyChange(-1, 0));
  byId("dummySetUp").addEventListener("click", () => dummyChange(1, 0));
  byId("dummyRoomDown").addEventListener("click", () => dummyChange(0, -1));
  byId("dummyRoomUp").addEventListener("click", () => dummyChange(0, 1));

  refresh().catch(showClientError);
  setInterval(() => refresh().catch(showClientError), 1000);
}

if (document.readyState === "loading") {
  document.addEventListener("DOMContentLoaded", initializeDashboard, { once: true });
} else {
  initializeDashboard();
}
