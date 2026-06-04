const dashboardState = {
  snapshot: null,
  stream: null
};

const byId = (id) => document.getElementById(id);
const temp = (value) => Number.isFinite(value) ? `${value.toFixed(1)} C` : "--";
const shortTime = (value) => value ? new Date(value).toLocaleTimeString() : "--";
const dateTime = (value) => value ? new Date(value).toLocaleString() : "--";

function text(id, value) {
  const element = byId(id);
  if (element) {
    element.textContent = value;
  }
}

async function request(path, method = "GET", body = null) {
  const response = await fetch(path, {
    method,
    headers: body ? { "Content-Type": "application/json" } : undefined,
    body: body ? JSON.stringify(body) : undefined
  });

  const payload = await response.json().catch(() => null);
  if (!response.ok) {
    if (payload) {
      renderDashboard(payload);
    }

    throw new Error(payload?.lastError || `${response.status} ${response.statusText}`);
  }

  return payload;
}

function renderDashboard(snapshot) {
  if (!byId("targetTemp")) {
    return;
  }

  dashboardState.snapshot = snapshot;
  const target = Number(snapshot.targetTemperatureCelsius);
  const ha = snapshot.homeAssistantThermostat;
  const weather = snapshot.weather;
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
  text("haFan", ha?.fanMode || "--");
  text("outdoorTemp", weather?.outdoorTemperatureCelsius !== null && weather?.outdoorTemperatureCelsius !== undefined
    ? temp(Number(weather.outdoorTemperatureCelsius))
    : "--");
  text("weatherCondition", weather?.condition || "--");
  text("cooldown", Number(snapshot.cooldownSeconds) > 0 ? `${snapshot.cooldownSeconds}s` : "Ready");
  text("nextAction", snapshot.nextAction || "Waiting for defender status.");
  text("lastCommand", snapshot.lastCommand || "No commands yet");
  text("lastError", snapshot.lastError || "");

  text("boostOffset", Number.isFinite(Number(snapshot.boostOffsetCelsius))
    ? `${Number(snapshot.boostOffsetCelsius).toFixed(1)} C`
    : "--");
  text("haUpdated", ha ? shortTime(ha.updatedAt) : "--");
  text("realEntity", snapshot.homeAssistantEntityId || "--");
  text("touchCount", String((snapshot.thermostatChanges || []).length));

  renderFanSelect(ha);
  renderChangeLog(snapshot.thermostatChanges || []);
  renderEvents(snapshot.events || []);
}

function renderFanSelect(ha) {
  const select = byId("fanModeSelect");
  if (!select || document.activeElement === select) {
    return;
  }

  const modes = ha?.availableFanModes?.length
    ? ha.availableFanModes
    : [ha?.fanMode || "auto"].filter(Boolean);

  select.replaceChildren(...modes.map((mode) => {
    const option = document.createElement("option");
    option.value = mode;
    option.textContent = mode;
    option.selected = mode === ha?.fanMode;
    return option;
  }));
}

function renderChangeLog(changes) {
  const list = byId("changeLog");
  if (!list) {
    return;
  }

  if (!changes.length) {
    const empty = document.createElement("div");
    empty.className = "empty-log";
    empty.textContent = "No external thermostat changes detected.";
    list.replaceChildren(empty);
    return;
  }

  list.replaceChildren(...changes.map((change) => {
    const item = document.createElement("article");
    item.className = "change-item";
    item.setAttribute("role", "option");

    const title = document.createElement("strong");
    title.textContent = `${Number(change.previousSetPointCelsius).toFixed(1)} C -> ${Number(change.newSetPointCelsius).toFixed(1)} C`;

    const meta = document.createElement("span");
    meta.textContent = dateTime(change.timestamp);

    const context = document.createElement("small");
    const room = change.roomTemperatureCelsius !== null && change.roomTemperatureCelsius !== undefined
      ? `Room ${Number(change.roomTemperatureCelsius).toFixed(1)} C`
      : "Room --";
    const outdoor = change.outdoorTemperatureCelsius !== null && change.outdoorTemperatureCelsius !== undefined
      ? `Outdoor ${Number(change.outdoorTemperatureCelsius).toFixed(1)} C`
      : "Outdoor --";
    context.textContent = `${room} / ${outdoor} / ${change.weatherCondition || "weather --"}`;

    item.append(title, meta, context);
    return item;
  }));
}

function renderEvents(events) {
  const eventLog = byId("eventLog");
  if (!eventLog) {
    return;
  }

  eventLog.replaceChildren(...events.map((event) => {
    const item = document.createElement("li");
    item.className = event.level || "info";

    const time = document.createElement("time");
    time.dateTime = event.timestamp;
    time.textContent = dateTime(event.timestamp);

    const message = document.createElement("div");
    message.textContent = event.message;

    item.append(time, message);
    return item;
  }));
}

async function refreshDashboard() {
  renderDashboard(await request("/api/status"));
}

async function mutateDashboard(path, body = null) {
  renderDashboard(await request(path, "POST", body));
}

function showClientError(error) {
  text("lastError", error.message);
}

function connectDashboardStream() {
  if (!window.EventSource || dashboardState.stream) {
    return;
  }

  dashboardState.stream = new EventSource("/api/status/stream");
  dashboardState.stream.onmessage = (event) => {
    renderDashboard(JSON.parse(event.data));
  };
  dashboardState.stream.onerror = () => {
    text("headerStatus", "Reconnecting");
  };
}

function initializeDashboard() {
  if (!byId("generateTarget")) {
    return;
  }

  byId("generateTarget").addEventListener("click", () => {
    mutateDashboard("/api/target/generate").catch(showClientError);
  });

  byId("applyTarget").addEventListener("click", () => {
    mutateDashboard("/api/target", {
      temperatureCelsius: Number(byId("targetInput").value)
    }).catch(showClientError);
  });

  byId("defenderToggle").addEventListener("change", (event) => {
    mutateDashboard("/api/defender", { enabled: event.target.checked }).catch(showClientError);
  });

  byId("refreshReal").addEventListener("click", () => {
    mutateDashboard("/api/thermostat/refresh").catch(showClientError);
  });

  byId("forceTarget").addEventListener("click", () => {
    mutateDashboard("/api/thermostat/force-target").catch(showClientError);
  });

  byId("forceBoost").addEventListener("click", () => {
    mutateDashboard("/api/thermostat/force-boost").catch(showClientError);
  });

  byId("applyFanMode").addEventListener("click", () => {
    mutateDashboard("/api/thermostat/fan", { fanMode: byId("fanModeSelect").value }).catch(showClientError);
  });

  refreshDashboard().catch(showClientError);
  connectDashboardStream();
}

if (document.readyState === "loading") {
  document.addEventListener("DOMContentLoaded", initializeDashboard, { once: true });
} else {
  initializeDashboard();
}
