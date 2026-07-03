// Typed bridge to the Rust commands. The Rust side owns the HTTP session; the UI only
// ever sees JSON snapshots (the same shape the website's /api/status returns).
import { invoke } from "@tauri-apps/api/core";

export interface ThermostatSnapshot {
  currentTemperatureCelsius: number;
  setPointCelsius: number;
  hvacMode: string;
  hvacAction: string;
  fanMode?: string | null;
  availableFanModes: string[];
  updatedAt: string;
}

export interface AcRuntimeSnapshot {
  todayHours: number;
  monthHours: number;
  lifetimeHours: number;
  estimatedCostEnabled?: boolean;
  estimatedCostTodayDollars?: number;
  estimatedCostMonthDollars?: number;
  estimatedCostLifetimeDollars?: number;
  assumedKilowatts?: number;
}

export interface ElectricityBudgetSnapshot {
  enabled: boolean;
  monthlyBudgetCad: number;
  monthToDateAllInCad: number;
  proRatedTargetCad: number;
  overUnderCad: number;
  currentSetpointOffsetCelsius: number;
  effectiveBasis?: string;
  status: string;
}

export interface DefenderEvent {
  timestamp: string;
  level: string;
  message: string;
}

export interface DefenderSnapshot {
  targetTemperatureCelsius: number;
  defenderEnabled: boolean;
  connectionState: string;
  homeAssistantThermostat?: ThermostatSnapshot | null;
  nextAction: string;
  lastError?: string | null;
  acRuntime?: AcRuntimeSnapshot | null;
  electricityBudget?: ElectricityBudgetSnapshot | null;
  events: DefenderEvent[];
}

export interface SavedConfig {
  base_url: string;
  username: string;
  password?: string | null;
}

export const api = {
  loadConfig: () => invoke<SavedConfig>("load_config"),
  connect: (baseUrl: string, username: string, password: string, remember: boolean) =>
    invoke<DefenderSnapshot>("connect", { baseUrl, username, password, remember }),
  getStatus: () => invoke<DefenderSnapshot>("get_status"),
  setTarget: (temperature: number) => invoke<DefenderSnapshot>("set_target", { temperature }),
  setDefender: (enabled: boolean) => invoke<DefenderSnapshot>("set_defender", { enabled }),
  forceTarget: () => invoke<DefenderSnapshot>("force_target"),
  forceBoost: () => invoke<DefenderSnapshot>("force_boost"),
  refresh: () => invoke<DefenderSnapshot>("refresh_thermostat"),
  thermostatOff: () => invoke<DefenderSnapshot>("thermostat_off"),
  setFan: (fanMode: string) => invoke<DefenderSnapshot>("set_fan", { fanMode }),
  emergency: (protocol: string) => invoke<DefenderSnapshot>("emergency", { protocol }),
};
