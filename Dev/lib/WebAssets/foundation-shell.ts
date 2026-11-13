// @module: Foundation.UI.WebAssets.Shell
// @purpose: TypeScript contracts and helper logic for the Collapsible Sidebar Rail, Docked Resizable Terminal (Ctrl+`), SchemaSettingsRenderer, and 2-Line Tooltips.
// @depends-on: Standard DOM APIs
// @exports: TerminalPreset, TerminalLayoutState, SchemaSettingDescriptorDto, TableColumnSchemaDto, computeTerminalPresetHeight, clampTerminalHeight

export type TerminalPreset = "S" | "M" | "L";

export interface TerminalLayoutState {
  isOpen: boolean;
  heightPx: number;
  preset: TerminalPreset;
  autoScroll: boolean;
  levelFilter: "ALL" | "INFO" | "WARN" | "ERROR";
}

export interface SchemaSettingDescriptorDto {
  key: string;
  label: string;
  inputType: "boolean" | "text" | "number" | "select" | "path";
  defaultValue: unknown;
  tooltipTitle: string;
  tooltipBody: string;
  hotkey?: string;
  minValue?: number;
  maxValue?: number;
  allowedOptions?: string[];
}

export interface TableColumnSchemaDto {
  jsonKey: string;
  titleCaseHeader: string;
  isNumericTabular?: boolean;
  isSortable?: boolean;
  statusBadgeMap?: string;
}

export const DEFAULT_TERMINAL_HEIGHT_PX = 280;
export const MIN_TERMINAL_HEIGHT_PX = 120;
export const MIN_MAIN_VIEWPORT_RESERVE_PX = 160;
export const TOOLTIP_HOVER_DELAY_MS = 250;

export function clampTerminalHeight(requestedHeightPx: number, bodyColumnHeightPx: number): number {
  const maxAllowed = Math.max(MIN_TERMINAL_HEIGHT_PX, bodyColumnHeightPx - MIN_MAIN_VIEWPORT_RESERVE_PX);
  return Math.max(MIN_TERMINAL_HEIGHT_PX, Math.min(Math.round(requestedHeightPx), maxAllowed));
}

export function computeTerminalPresetHeight(preset: TerminalPreset, bodyColumnHeightPx: number): number {
  const ratio = preset === "S" ? 0.25 : preset === "M" ? 0.40 : 0.60;
  return clampTerminalHeight(bodyColumnHeightPx * ratio, bodyColumnHeightPx);
}

export function isTerminalToggleShortcut(event: KeyboardEvent): boolean {
  return (event.ctrlKey || event.metaKey) && event.key === "`";
}

export function isSidebarToggleShortcut(event: KeyboardEvent): boolean {
  return (event.ctrlKey || event.metaKey) && (event.key === "b" || event.key === "B");
}
