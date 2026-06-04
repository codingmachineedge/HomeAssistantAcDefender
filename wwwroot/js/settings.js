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
  if (!rows.length) {
    const empty = document.createElement("div");
    empty.className = "schedule-empty";
    empty.textContent = "No schedule rules yet.";
    container.replaceChildren(empty);
    return;
  }

  container.replaceChildren(...rows.map(createScheduleRow));
}

function createScheduleId() {
  if (window.crypto?.randomUUID) {
    return window.crypto.randomUUID();
  }

  if (window.crypto?.getRandomValues) {
    const values = new Uint32Array(4);
    window.crypto.getRandomValues(values);
    return `schedule-${Array.from(values, (value) => value.toString(16)).join("")}`;
  }

  return `schedule-${Date.now()}-${Math.random().toString(16).slice(2)}`;
}

function createScheduleRow(row = {}) {
  const wrapper = document.createElement("div");
  wrapper.className = "schedule-row";
  wrapper.dataset.id = row.id || createScheduleId();
  const selectedDays = new Set((row.days || "Mon,Tue,Wed,Thu,Fri,Sat,Sun")
    .split(",")
    .map((day) => day.trim())
    .filter(Boolean));

  wrapper.innerHTML = `
    <div class="schedule-card-head">
      <label class="field schedule-name"><span>Name</span><input data-field="name" type="text" /></label>
      <label class="switch-row compact"><input data-field="enabled" type="checkbox" /><span>On</span></label>
      <button type="button" class="remove-row">Remove</button>
    </div>
    <div class="day-chip-row" aria-label="Days">
      <button type="button" class="day-chip" data-day="Mon">Mon</button>
      <button type="button" class="day-chip" data-day="Tue">Tue</button>
      <button type="button" class="day-chip" data-day="Wed">Wed</button>
      <button type="button" class="day-chip" data-day="Thu">Thu</button>
      <button type="button" class="day-chip" data-day="Fri">Fri</button>
      <button type="button" class="day-chip" data-day="Sat">Sat</button>
      <button type="button" class="day-chip" data-day="Sun">Sun</button>
    </div>
    <div class="schedule-controls">
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
    </div>
    <div class="schedule-summary" data-field="summary"></div>
  `;

  wrapper.querySelector('[data-field="name"]').value = row.name || "Schedule";
  wrapper.querySelector('[data-field="startTime"]').value = row.startTime || "00:00";
  wrapper.querySelector('[data-field="endTime"]').value = row.endTime || "23:59";
  wrapper.querySelector('[data-field="targetTemperatureCelsius"]').value = row.targetTemperatureCelsius ?? 22;
  wrapper.querySelector('[data-field="weatherActivationMode"]').value = row.weatherActivationMode || "always";
  wrapper.querySelector('[data-field="enabled"]').checked = row.enabled !== false;
  wrapper.querySelectorAll(".day-chip").forEach((button) => {
    button.classList.toggle("active", selectedDays.has(button.dataset.day));
    button.addEventListener("click", () => {
      button.classList.toggle("active");
      markScheduleDirty();
      updateScheduleSummary(wrapper);
    });
  });
  wrapper.querySelector(".remove-row").addEventListener("click", () => {
    wrapper.remove();
    if (!document.querySelectorAll(".schedule-row").length) {
      renderScheduleRows([]);
    }
    markScheduleDirty();
  });
  wrapper.addEventListener("input", () => {
    markScheduleDirty();
    updateScheduleSummary(wrapper);
  });
  wrapper.addEventListener("change", () => {
    markScheduleDirty();
    updateScheduleSummary(wrapper);
  });
  updateScheduleSummary(wrapper);

  return wrapper;
}

function collectScheduleRows() {
  return Array.from(document.querySelectorAll(".schedule-row")).map((row) => ({
    id: row.dataset.id,
    enabled: row.querySelector('[data-field="enabled"]').checked,
    name: row.querySelector('[data-field="name"]').value,
    days: Array.from(row.querySelectorAll(".day-chip.active"))
      .map((button) => button.dataset.day)
      .join(","),
    startTime: row.querySelector('[data-field="startTime"]').value,
    endTime: row.querySelector('[data-field="endTime"]').value,
    targetTemperatureCelsius: Number(row.querySelector('[data-field="targetTemperatureCelsius"]').value),
    weatherActivationMode: row.querySelector('[data-field="weatherActivationMode"]').value
  }));
}

function updateScheduleSummary(row) {
  const enabled = row.querySelector('[data-field="enabled"]').checked;
  const days = Array.from(row.querySelectorAll(".day-chip.active")).map((button) => button.dataset.day);
  const start = row.querySelector('[data-field="startTime"]').value || "--:--";
  const end = row.querySelector('[data-field="endTime"]').value || "--:--";
  const target = Number(row.querySelector('[data-field="targetTemperatureCelsius"]').value);
  const weatherMode = row.querySelector('[data-field="weatherActivationMode"]').selectedOptions[0]?.textContent || "Always";
  const dayText = days.length === 7 ? "Every day" : days.length ? days.join(", ") : "No days";
  row.querySelector('[data-field="summary"]').textContent =
    `${enabled ? "Active" : "Off"} / ${dayText} / ${start}-${end} / ${Number.isFinite(target) ? target.toFixed(1) : "--"} C / ${weatherMode}`;
}

function markScheduleDirty() {
  settingById("scheduleRows").dataset.dirty = "true";
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
    const container = settingById("scheduleRows");
    const existingEmpty = container.querySelector(".schedule-empty");
    if (existingEmpty) {
      existingEmpty.remove();
    }

    container.append(createScheduleRow({
      name: "Comfort",
      days: "Mon,Tue,Wed,Thu,Fri,Sat,Sun",
      startTime: "18:00",
      endTime: "23:00",
      targetTemperatureCelsius: 22,
      weatherActivationMode: "always",
      enabled: true
    }));
    markScheduleDirty();
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
