// @module: Foundation.UI.Web.TerminalToolbarAndFilters
// @purpose: Docked terminal header and collapsible filter toolbar with S/M/L height presets, Verbosity/History/Search/Wrap/Auto-scroll filters, and split Stop Current Job vs Stop All Jobs controls.
// @depends-on: react, lucide-react, ./StyledTooltip, ./terminalDrawerCore
// @exports: TerminalToolbarAndFilters, TerminalToolbarAndFiltersProps, filterTerminalLines, TerminalLogFilter

import React from 'react';
import { SlidersHorizontal } from 'lucide-react';
import { StyledTooltip } from './StyledTooltip';
import {
  filterTerminalLines,
  type TerminalActiveJobDto,
  type TerminalHeightPreset,
  type TerminalLogFilter,
  type TerminalLogLine,
  type TerminalVerbosityLevel,
} from './terminalDrawerCore';

export { filterTerminalLines, type TerminalLogFilter };

export interface TerminalToolbarAndFiltersProps {
  expanded: boolean;
  onToggleExpand: () => void;
  filtersOpen: boolean;
  onToggleFilters: () => void;
  connected: boolean;
  jobStatus: TerminalActiveJobDto | null;
  latestLine: TerminalLogLine | null;
  queuedCount: number;
  pct: number | null;
  activePreset: TerminalHeightPreset;
  onSelectPreset: (preset: 'S' | 'M' | 'L') => void;
  onOpenQueueModal?: () => void;
  onStopCurrentJob: (e: React.MouseEvent) => void;
  onStopAllJobs: (e: React.MouseEvent) => void;
  onClear: (e: React.MouseEvent) => void;
  verbosity: TerminalVerbosityLevel;
  onVerbosityChange: (v: TerminalVerbosityLevel) => void;
  historyLimit: number;
  onHistoryLimitChange: (limit: number) => void;
  searchFilter: string;
  onSearchFilterChange: (q: string) => void;
  wrapLines: boolean;
  onWrapLinesChange: (wrap: boolean) => void;
  autoScroll: boolean;
  onAutoScrollChange: (auto: boolean) => void;
  filteredCount: number;
  totalCount: number;
}

export const TerminalToolbarAndFilters: React.FC<TerminalToolbarAndFiltersProps> = ({
  expanded,
  onToggleExpand,
  filtersOpen,
  onToggleFilters,
  connected,
  jobStatus,
  latestLine,
  queuedCount,
  pct,
  activePreset,
  onSelectPreset,
  onOpenQueueModal,
  onStopCurrentJob,
  onStopAllJobs,
  onClear,
  verbosity,
  onVerbosityChange,
  historyLimit,
  onHistoryLimitChange,
  searchFilter,
  onSearchFilterChange,
  wrapLines,
  onWrapLinesChange,
  autoScroll,
  onAutoScrollChange,
  filteredCount,
  totalCount,
}) => {
  const processed = jobStatus?.processedItems ?? jobStatus?.currentItemIndex ?? null;
  const total = jobStatus?.totalItems ?? jobStatus?.totalItemCount ?? null;
  const stepText = jobStatus?.currentStep || jobStatus?.phase || '';

  return (
    <>
      <div
        className="sl-terminal-header fn-terminal-header"
        onClick={onToggleExpand}
        role="button"
        tabIndex={0}
        aria-expanded={expanded}
        onKeyDown={(e) => {
          if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); onToggleExpand(); }
        }}
      >
        <div className="sl-row sl-terminal-title-row fn-terminal-title-row">
          <span className={`sl-badge ${connected ? 'sl-badge-ok' : 'sl-badge-neutral'}`}>
            {connected ? 'LIVE' : 'POLL'}
          </span>
          <strong className="sl-mono" style={{ fontSize: '0.8rem', whiteSpace: 'nowrap' }}>
            Terminal<span className="sl-desktop-only"> (Ctrl+`)</span>
          </strong>

          {jobStatus?.isRunning ? (
            <div className="sl-row sl-terminal-running-row fn-terminal-running-row">
              <span className="sl-badge sl-badge-pr sl-terminal-running-badge fn-terminal-running-badge">
                RUNNING: {jobStatus.jobName || 'Active Job'}
                {typeof processed === 'number' && typeof total === 'number' && total > 0
                  ? ` [${processed.toLocaleString()}/${total.toLocaleString()}]`
                  : ''}
                {pct !== null ? ` (${pct}%)` : ''}
                {stepText ? ` — ${stepText}` : ''}
              </span>
              {pct !== null ? (
                <div className="sl-progress-track sl-terminal-mini-progress fn-terminal-mini-progress">
                  <div className="sl-progress-fill" style={{ width: `${pct}%` }} />
                </div>
              ) : null}
            </div>
          ) : latestLine ? (
            <span className="sl-mono sl-terminal-latest-preview fn-terminal-latest-preview">
              [{latestLine.level}] {latestLine.message}
            </span>
          ) : null}
        </div>

        <div className="sl-row sl-terminal-actions-row fn-terminal-actions-row" onClick={(e) => e.stopPropagation()}>
          {expanded ? (
            <div className="sl-row" style={{ gap: '0.2rem' }}>
              {(['S', 'M', 'L'] as const).map((p) => (
                <StyledTooltip
                  key={p}
                  title={`Terminal Size Preset ${p}`}
                  body={`Sets docked terminal height to ${p === 'S' ? '25%' : p === 'M' ? '40%' : '60%'} of workspace height.`}
                >
                  <button
                    type="button"
                    className={`sl-btn sl-btn-sm ${activePreset === p ? 'sl-btn-primary' : ''}`}
                    style={{ minHeight: '1.5rem', padding: '0.1rem 0.42rem' }}
                    onClick={() => onSelectPreset(p)}
                  >
                    {p}
                  </button>
                </StyledTooltip>
              ))}
            </div>
          ) : null}

          {(queuedCount > 0 || jobStatus?.isRunning) && onOpenQueueModal ? (
            <button type="button" className="sl-btn sl-btn-sm" onClick={onOpenQueueModal}>
              <span><span className="sl-desktop-only">Queued </span><span className="sl-mobile-only">Q</span>({queuedCount})</span>
            </button>
          ) : null}

          {jobStatus?.isRunning ? (
            <>
              <StyledTooltip
                title="Stop Current Job"
                body="Cancels the active job while allowing remaining queued jobs in the FIFO queue to proceed."
              >
                <button
                  type="button"
                  className="sl-btn sl-btn-danger sl-btn-sm"
                  data-testid="btn-cancel-current-job"
                  onClick={onStopCurrentJob}
                >
                  <span>⏹ Stop<span className="sl-desktop-only"> Current Job</span></span>
                </button>
              </StyledTooltip>
              <StyledTooltip
                title="Stop All Jobs"
                body="Terminates the active job and clears all waiting items in the FIFO queue."
              >
                <button
                  type="button"
                  className="sl-btn sl-btn-danger sl-btn-sm"
                  data-testid="btn-cancel-all-jobs"
                  onClick={onStopAllJobs}
                >
                  <span>
                    ⏹ <span className="sl-desktop-only">Stop </span>All<span className="sl-desktop-only"> Jobs</span>
                    {queuedCount > 0 ? ` (${queuedCount})` : ''}
                  </span>
                </button>
              </StyledTooltip>
            </>
          ) : null}

          {expanded ? (
            <StyledTooltip
              title="Toggle Terminal Filters"
              body="Shows or hides verbosity, history cap, search filter, and line-wrap controls."
            >
              <button
                type="button"
                className={`sl-btn sl-btn-sm ${filtersOpen ? 'sl-btn-primary' : ''}`}
                aria-expanded={filtersOpen}
                aria-label="Toggle terminal filters"
                onClick={onToggleFilters}
              >
                <SlidersHorizontal size={12} />
                <span className="sl-desktop-only">Filters</span>
              </button>
            </StyledTooltip>
          ) : null}

          <button type="button" className="sl-btn sl-btn-sm" data-testid="btn-clear-terminal-logs" onClick={onClear}>
            <span><span className="sl-desktop-only">Clear Logs</span><span className="sl-mobile-only">Clr</span></span>
          </button>
          <button type="button" className="sl-btn sl-btn-sm" onClick={onToggleExpand}>
            {expanded ? <span>▼<span className="sl-desktop-only"> Hide</span></span> : <span>▲<span className="sl-desktop-only"> Expand</span></span>}
          </button>
        </div>
      </div>

      {expanded && filtersOpen ? (
        <div className="sl-terminal-toolbar fn-terminal-toolbar" onClick={(e) => e.stopPropagation()}>
          <div className="sl-row sl-terminal-toolbar-selects fn-terminal-toolbar-selects">
            <label className="sl-row sl-terminal-filter-field fn-terminal-filter-field">
              <span>Verbosity:</span>
              <select
                className="sl-select sl-btn-sm"
                aria-label="Terminal log verbosity"
                value={verbosity}
                onChange={(e) => onVerbosityChange(e.target.value as TerminalVerbosityLevel)}
              >
                <option value="Quiet">Quiet (Errors &amp; Warnings)</option>
                <option value="Normal">Normal (+ Jobs &amp; Progress)</option>
                <option value="Verbose">Verbose (Default — + Info &amp; Verify)</option>
                <option value="Trace">Trace (+ Cache, Cmd &amp; Debug)</option>
              </select>
            </label>

            <label className="sl-row sl-terminal-filter-field fn-terminal-filter-field">
              <span>History:</span>
              <select
                className="sl-select sl-btn-sm"
                aria-label="Live terminal history line cap"
                value={historyLimit}
                onChange={(e) => onHistoryLimitChange(Number(e.target.value))}
              >
                <option value={100}>100 lines</option>
                <option value={250}>250 lines (Default)</option>
                <option value={500}>500 lines</option>
                <option value={1000}>1,000 lines</option>
              </select>
            </label>

            <input
              type="search"
              className="sl-input sl-btn-sm sl-terminal-search-input fn-terminal-search-input"
              placeholder="Filter logs..."
              aria-label="Filter terminal output"
              value={searchFilter}
              onChange={(e) => onSearchFilterChange(e.target.value)}
            />
          </div>

          <div className="sl-row sl-terminal-toolbar-toggles fn-terminal-toolbar-toggles">
            <label className="sl-row" style={{ cursor: 'pointer', gap: 4 }}>
              <input type="checkbox" checked={wrapLines} onChange={(e) => onWrapLinesChange(e.target.checked)} />
              <span>Wrap lines</span>
            </label>
            <label className="sl-row" style={{ cursor: 'pointer', gap: 4 }}>
              <input type="checkbox" checked={autoScroll} onChange={(e) => onAutoScrollChange(e.target.checked)} />
              <span>Auto-scroll</span>
            </label>
            <span className="sl-mono" style={{ color: '#94a3b8' }}>
              {filteredCount}/{totalCount} lines
            </span>
          </div>
        </div>
      ) : null}
    </>
  );
};
