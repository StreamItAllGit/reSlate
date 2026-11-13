// @module: Foundation.UI.Web.Index
// @purpose: Barrel export for @foundation/ui v0.2.0 shared React 19 components and ShadowLibrary Strict Standard terminal drawer across sister applications.
// @depends-on: ./components/*
// @exports: StyledTooltip, StyledTooltipProps, ModalDialog, ModalDialogProps, ConfirmActionModal, ConfirmActionModalProps, ResponsiveTable, ResponsiveTableProps, UniversalDataTable, UniversalColumnDef, UniversalDataTableProps, SchemaSettingsRenderer, SettingFieldType, SettingFieldDescriptor, SchemaSettingsRendererProps, GlobalTerminalDrawer, GlobalTerminalDrawerProps, TerminalJobStatusDto, TerminalToolbarAndFilters, TerminalToolbarAndFiltersProps, TerminalVerbosityLevel, TerminalHeightPreset, TerminalLogFilter, TerminalLogLine, TerminalActiveJobDto, MIN_TERMINAL_HEIGHT, DEFAULT_TERMINAL_HEIGHT, MAX_TERMINAL_HEIGHT, shouldShowLogLine, isReplaceableProgressLine, coalesceTerminalLogBatch, logBadgeColor, isMobileTerminalViewport, clampTerminalDrawerHeight, computeTerminalDrawerPresetHeight, filterTerminalLines

export { StyledTooltip, type StyledTooltipProps } from './components/StyledTooltip';
export {
  ModalDialog,
  type ModalDialogProps,
  ConfirmActionModal,
  type ConfirmActionModalProps,
} from './components/ModalDialog';
export { ResponsiveTable, type ResponsiveTableProps } from './components/ResponsiveTable';
export {
  UniversalDataTable,
  type UniversalColumnDef,
  type UniversalDataTableProps,
} from './components/UniversalDataTable';
export {
  SchemaSettingsRenderer,
  type SettingFieldType,
  type SettingFieldDescriptor,
  type SchemaSettingsRendererProps,
} from './components/SchemaSettingsRenderer';
export {
  GlobalTerminalDrawer,
  type GlobalTerminalDrawerProps,
  type TerminalJobStatusDto,
} from './components/GlobalTerminalDrawer';
export {
  TerminalToolbarAndFilters,
  type TerminalToolbarAndFiltersProps,
} from './components/TerminalToolbarAndFilters';
export {
  type TerminalVerbosityLevel,
  type TerminalHeightPreset,
  type TerminalLogFilter,
  type TerminalLogLine,
  type TerminalActiveJobDto,
  MIN_TERMINAL_HEIGHT,
  DEFAULT_TERMINAL_HEIGHT,
  MAX_TERMINAL_HEIGHT,
  shouldShowLogLine,
  isReplaceableProgressLine,
  coalesceTerminalLogBatch,
  logBadgeColor,
  isMobileTerminalViewport,
  clampTerminalDrawerHeight,
  computeTerminalDrawerPresetHeight,
  filterTerminalLines,
} from './components/terminalDrawerCore';
