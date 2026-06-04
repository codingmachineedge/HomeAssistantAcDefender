const settingsState = {
  snapshot: null,
  stream: null
};

function settingById(id) {
  return document.getElementById(id);
}

async function settingsRequest(path, method = "GET", body = null) {
  const response = await fetch(path, {
    method,
    headers: body ? { "Content-Type": "application/json" } : undefined,
    body: body ? JSON.stringify(body) : undefined
  });

  const payload = await response.json();
  if (!response.ok) {
    throw new Error(payload?.lastError || `${response.status} ${response.statusText}`);
  }

  return payload;
}

function renderSettings(snapshot) {
  settingsState.snapshot = snapshot;
  if (!settingById("settingsScheduleEnabled")) {
    return;
  }

  const settings = snapshot.settings || {};
  settingById("settingsScheduleEnabled").checked = Boolean(settings.scheduleEnabled);
  settingById("settingsWeatherMode").value = settings.weatherActivationMode || "always";
  settingById("settingsBaseCooldown").value = settings.baseCooldownSeconds ?? 45;
  settingById("settingsMaxCooldown").value = settings.maxCooldownSeconds ?? 600;
  settingById("settingsTouchWindow").value = settings.touchFrequencyWindowMinutes ?? 30;
  settingById("settingsFanSaverEnabled").checked = Boolean(settings.fanEnergySaverEnabled);
  settingById("settingsFanThreshold").value = settings.fanEnergySaverThresholdCelsius ?? 0.6;
  settingById("settingsFanMode").value = settings.fanEnergySaverMode || "auto";
  settingById("settingsUpstairsEnabled").checked = settings.upstairsComfortEnabled !== false;
  settingById("settingsUpstairsEntities").value = settings.upstairsTemperatureEntityIds || "";
  settingById("settingsUpstairsMax").value = settings.upstairsMaxComfortCelsius ?? 24;
  settingById("settingsUpstairsTarget").value = settings.upstairsComfortTargetCelsius ?? 22;
  settingById("settingsUpstairsBoost").value = settings.upstairsComfortBoostCelsius ?? 1;
  settingById("settingsPresenceRequired").checked = Boolean(settings.homePresenceRequired);
  settingById("settingsPresenceEntities").value = settings.presenceEntityIds || "";

  if (!settingById("scheduleRows").dataset.dirty) {
    renderScheduleRows(snapshot.schedule || []);
  }
}

function renderScheduleRows(rows) {
  const container = settingById("scheduleRows");
  container.replaceChildren(...rows.map(createScheduleRow));
}

function createScheduleRow(row = {}) {
  const wrapper = document.createElement("div");
  wrapper.className = "schedule-row";
  wrapper.dataset.id = row.id || crypto.randomUUID();

  wrapper.innerHTML = `
    <label class="field"><span>Name</span><input data-field="name" type="text" /></label>
    <label class="field"><span>Days</span><input data-field="days" type="text" /></label>
    <label class="field"><span>Start</span><input data-field="startTime" type="time" /></label>
    <label class="field"><span>End</span><input data-field="endTime" type="time" /></label>
    <label class="field"><span>Target C</span><input data-field="targetTemperatureCelsius" type="number" min="10" max="35" step="0.1" /></label>
    <label class="field"><span>Weather rule</span><select data-field="weatherActivationMode">
      <option value="always">Always</option>
      <option value="room-above-outdoor">Room above outdoor</option>
      <option value="room-below-outdoor">Room below outdoor</option>
      <option value="outdoor-above-target">Outdoor above target</option>
      <option value="outdoor-below-target">Outdoor below target</option>
    </select></label>
    <label class="switch-row compact"><input data-field="enabled" type="checkbox" /><span>On</span></label>
    <button type="button" class="remove-row">Remove</button>
  `;

  wrapper.querySelector('[data-field="name"]').value = row.name || "Schedule";
  wrapper.querySelector('[data-field="days"]').value = row.days || "Mon,Tue,Wed,Thu,Fri,Sat,Sun";
  wrapper.querySelector('[data-field="startTime"]').value = row.startTime || "00:00";
  wrapper.querySelector('[data-field="endTime"]').value = row.endTime || "23:59";
  wrapper.querySelector('[data-field="targetTemperatureCelsius"]').value = row.targetTemperatureCelsius ?? 22;
  wrapper.querySelector('[data-field="weatherActivationMode"]').value = row.weatherActivationMode || "always";
  wrapper.querySelector('[data-field="enabled"]').checked = row.enabled !== false;
  wrapper.querySelector(".remove-row").addEventListener("click", () => {
    wrapper.remove();
    settingById("scheduleRows").dataset.dirty = "true";
  });
  wrapper.addEventListener("input", () => {
    settingById("scheduleRows").dataset.dirty = "true";
  });

  return wrapper;
}

function collectScheduleRows() {
  return Array.from(document.querySelectorAll(".schedule-row")).map((row) => ({
    id: row.dataset.id,
    enabled: row.querySelector('[data-field="enabled"]').checked,
    name: row.querySelector('[data-field="name"]').value,
    days: row.querySelector('[data-field="days"]').value,
    startTime: row.querySelector('[data-field="startTime"]').value,
    endTime: row.querySelector('[data-field="endTime"]').value,
    targetTemperatureCelsius: Number(row.querySelector('[data-field="targetTemperatureCelsius"]').value),
    weatherActivationMode: row.querySelector('[data-field="weatherActivationMode"]').value
  }));
}

async function saveSettings() {
  const payload = {
    scheduleEnabled: settingById("settingsScheduleEnabled").checked,
    weatherActivationMode: settingById("settingsWeatherMode").value,
    baseCooldownSeconds: Number(settingById("settingsBaseCooldown").value),
    maxCooldownSeconds: Number(settingById("settingsMaxCooldown").value),
    touchFrequencyWindowMinutes: Number(settingById("settingsTouchWindow").value),
    fanEnergySaverEnabled: settingById("settingsFanSaverEnabled").checked,
    fanEnergySaverThresholdCelsius: Number(settingById("settingsFanThreshold").value),
    fanEnergySaverMode: settingById("settingsFanMode").value,
    upstairsComfortEnabled: settingById("settingsUpstairsEnabled").checked,
    upstairsTemperatureEntityIds: settingById("settingsUpstairsEntities").value,
    upstairsMaxComfortCelsius: Number(settingById("settingsUpstairsMax").value),
    upstairsComfortTargetCelsius: Number(settingById("settingsUpstairsTarget").value),
    upstairsComfortBoostCelsius: Number(settingById("settingsUpstairsBoost").value),
    homePresenceRequired: settingById("settingsPresenceRequired").checked,
    presenceEntityIds: settingById("settingsPresenceEntities").value,
    schedule: collectScheduleRows()
  };

  renderSettings(await settingsRequest("/api/settings", "POST", payload));
  delete settingById("scheduleRows").dataset.dirty;
  settingById("settingsStatus").textContent = `Saved ${new Date().toLocaleTimeString()}`;
}

function connectSettingsStream() {
  if (!window.EventSource || settingsState.stream) {
    return;
  }

  settingsState.stream = new EventSource("/api/status/stream");
  settingsState.stream.onmessage = (event) => {
    renderSettings(JSON.parse(event.data));
  };
}

function initializeSettings() {
  if (!settingById("saveSettings")) {
    return;
  }

  settingById("addSchedule").addEventListener("click", () => {
    settingById("scheduleRows").append(createScheduleRow());
    settingById("scheduleRows").dataset.dirty = "true";
  });
  settingById("saveSettings").addEventListener("click", () => {
    saveSettings().catch((error) => {
      settingById("settingsStatus").textContent = error.message;
    });
  });

  settingsRequest("/api/settings").then(renderSettings);
  connectSettingsStream();
}

if (document.readyState === "loading") {
  document.addEventListener("DOMContentLoaded", initializeSettings, { once: true });
} else {
  initializeSettings();
}
