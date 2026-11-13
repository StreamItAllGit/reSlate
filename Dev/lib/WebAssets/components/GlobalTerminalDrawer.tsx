// @module: Foundation.UI.Web.GlobalTerminalDrawer
// @purpose: Standard 09 §4.5 docked resizable bottom terminal pane (168px-600px, S/M/L presets, collapsible mobile filters, ResizeObserver + bottom sentinel, ConfirmActionModal dual cancellation).
// @depends-on: react, ./ModalDialog, ./TerminalToolbarAndFilters, ./terminalDrawerCore
// @exports: GlobalTerminalDrawer, GlobalTerminalDrawerProps, TerminalJobStatusDto, isReplaceableProgressLine

import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { ConfirmActionModal } from './ModalDialog';
import { TerminalToolbarAndFilters } from './TerminalToolbarAndFilters';
import {
  clampTerminalDrawerHeight, coalesceTerminalLogBatch, computeTerminalDrawerPresetHeight,
  DEFAULT_TERMINAL_HEIGHT, isMobileTerminalViewport, isReplaceableProgressLine, logBadgeColor,
  MAX_TERMINAL_HEIGHT, MIN_TERMINAL_HEIGHT, shouldShowLogLine,
  type TerminalActiveJobDto, type TerminalHeightPreset, type TerminalLogLine, type TerminalVerbosityLevel,
} from './terminalDrawerCore';

export { isReplaceableProgressLine };

export interface TerminalJobStatusDto {
  isRunning: boolean;
  jobName: string;
  phase: string;
  currentItem: string;
  processedItems: number;
  totalItems: number;
  percentComplete: number;
  lastSummary: string;
}

export interface GlobalTerminalDrawerProps {
  jobStatus?: TerminalActiveJobDto | null;
  job?: TerminalJobStatusDto | null;
  logs?: TerminalLogLine[];
  logLines?: string[];
  connected?: boolean;
  storagePrefix?: string;
  configuredVerbosity?: TerminalVerbosityLevel;
  configuredHistoryLimit?: number;
  onJobUpdated?: (status: TerminalActiveJobDto) => void;
  onFetchInitialState?: (historyLimit: number) => Promise<{ logs: TerminalLogLine[]; activeJob?: TerminalActiveJobDto | null }>;
  onClearTerminal?: () => Promise<TerminalLogLine[] | void> | void;
  onClearLogs?: () => void;
  onAppendLogLine?: (line: string) => void;
  onCancelCurrentJob?: () => Promise<unknown> | void;
  onCancelAllJobs?: () => Promise<unknown> | void;
  onOpenQueueModal?: () => void;
  onPreferencesChanged?: (verbosity: TerminalVerbosityLevel, historyLimit: number) => void;
  onUpdatePreferences?: (prefs: { verbosity?: TerminalVerbosityLevel; historyLimit?: number }) => Promise<unknown> | void;
}

function parseLegacyStringLines(lines: string[]): TerminalLogLine[] {
  return lines.map((raw, idx) => {
    const m = /^\[([A-Za-z]+)\]\s*(.*)$/.exec(raw.trim());
    return { seq: idx + 1, timestampUtc: new Date().toISOString(), level: m ? m[1].toUpperCase() : 'INFO', message: m ? m[2] : raw };
  });
}

export const GlobalTerminalDrawer: React.FC<GlobalTerminalDrawerProps> = ({
  jobStatus, job, logs: controlledLogs, logLines, connected = true, storagePrefix = 'sl-terminal',
  configuredVerbosity, configuredHistoryLimit, onJobUpdated, onFetchInitialState, onClearTerminal,
  onClearLogs, onAppendLogLine, onCancelCurrentJob, onCancelAllJobs, onOpenQueueModal,
  onPreferencesChanged, onUpdatePreferences,
}) => {
  const effectiveJob: TerminalActiveJobDto | null = useMemo(() => {
    if (jobStatus !== undefined) return jobStatus;
    if (!job) return null;
    return {
      isRunning: job.isRunning, jobName: job.jobName, currentStep: job.phase,
      processedItems: job.processedItems, totalItems: job.totalItems,
      progressPercent: job.percentComplete, lastSummary: job.lastSummary,
    };
  }, [jobStatus, job]);

  const [expanded, setExpanded] = useState<boolean>(() => {
    try { return localStorage.getItem(`${storagePrefix}-open`) === 'true'; } catch { return false; }
  });
  const [filtersOpen, setFiltersOpen] = useState<boolean>(() => !isMobileTerminalViewport());
  const [heightPx, setHeightPx] = useState<number>(() => {
    try {
      const saved = Number(localStorage.getItem(`${storagePrefix}-height`));
      return Number.isFinite(saved) && saved >= 140 ? clampTerminalDrawerHeight(saved) : DEFAULT_TERMINAL_HEIGHT;
    } catch { return DEFAULT_TERMINAL_HEIGHT; }
  });
  const [activePreset, setActivePreset] = useState<TerminalHeightPreset>(() => {
    try {
      const saved = localStorage.getItem(`${storagePrefix}-preset`) as TerminalHeightPreset | null;
      return saved === 'S' || saved === 'M' || saved === 'L' || saved === 'custom' ? saved : 'M';
    } catch { return 'M'; }
  });
  const [dragging, setDragging] = useState(false);
  const [confirmKind, setConfirmKind] = useState<'current' | 'all' | null>(null);
  const [internalLogs, setInternalLogs] = useState<TerminalLogLine[]>([]);
  const [verbosity, setVerbosity] = useState<TerminalVerbosityLevel>(configuredVerbosity ?? 'Verbose');
  const [historyLimit, setHistoryLimit] = useState<number>(configuredHistoryLimit ?? 250);
  const [searchFilter, setSearchFilter] = useState('');
  const [wrapLines, setWrapLines] = useState<boolean>(true);
  const [autoScroll, setAutoScroll] = useState(true);

  const bodyRef = useRef<HTMLDivElement | null>(null);
  const bottomSentinelRef = useRef<HTMLDivElement | null>(null);
  const historyLimitRef = useRef<number>(historyLimit);
  historyLimitRef.current = historyLimit;

  const activeLogs: TerminalLogLine[] = useMemo(() => {
    if (controlledLogs) return coalesceTerminalLogBatch([], controlledLogs, historyLimit);
    if (logLines) return coalesceTerminalLogBatch([], parseLegacyStringLines(logLines), historyLimit);
    return internalLogs;
  }, [controlledLogs, logLines, internalLogs, historyLimit]);

  const persistLayout = useCallback((open: boolean, h: number, preset: TerminalHeightPreset) => {
    try {
      localStorage.setItem(`${storagePrefix}-open`, String(open));
      localStorage.setItem(`${storagePrefix}-height`, String(h));
      localStorage.setItem(`${storagePrefix}-preset`, preset);
    } catch { /* ignore */ }
  }, [storagePrefix]);

  const toggleExpanded = useCallback(() => {
    setExpanded((prev) => { const next = !prev; persistLayout(next, heightPx, activePreset); return next; });
  }, [heightPx, activePreset, persistLayout]);

  useEffect(() => { if (configuredVerbosity) setVerbosity(configuredVerbosity); }, [configuredVerbosity]);
  useEffect(() => { if (configuredHistoryLimit && configuredHistoryLimit >= 50) setHistoryLimit(configuredHistoryLimit); }, [configuredHistoryLimit]);

  useEffect(() => {
    if (!onFetchInitialState) return;
    let disposed = false;
    void onFetchInitialState(historyLimitRef.current).then((res) => {
      if (disposed) return;
      if (Array.isArray(res.logs)) setInternalLogs(coalesceTerminalLogBatch([], res.logs, historyLimitRef.current));
      if (res.activeJob && onJobUpdated) onJobUpdated(res.activeJob);
    }).catch(() => {});
    return () => { disposed = true; };
  }, [onFetchInitialState, onJobUpdated]);

  useEffect(() => {
    const onKeyDown = (e: KeyboardEvent) => { if (e.ctrlKey && e.key === '`') { e.preventDefault(); toggleExpanded(); } };
    const onResize = () => { setHeightPx((prev) => clampTerminalDrawerHeight(prev)); setFiltersOpen(!isMobileTerminalViewport()); };
    window.addEventListener('keydown', onKeyDown);
    window.addEventListener('resize', onResize);
    return () => { window.removeEventListener('keydown', onKeyDown); window.removeEventListener('resize', onResize); };
  }, [toggleExpanded]);

  const filteredLogs = useMemo(() => {
    const q = searchFilter.trim().toLowerCase();
    return activeLogs.filter((line) => shouldShowLogLine(line.level, verbosity) && (!q || line.message.toLowerCase().includes(q) || line.level.toLowerCase().includes(q)));
  }, [activeLogs, verbosity, searchFilter]);

  const lastEntryKey = filteredLogs.length > 0 ? `${filteredLogs[filteredLogs.length - 1].seq}-${filteredLogs.length}` : 'empty';
  const scrollToBottom = useCallback(() => { if (autoScroll && bodyRef.current) bodyRef.current.scrollTop = bodyRef.current.scrollHeight; }, [autoScroll]);

  useEffect(() => {
    if (expanded && autoScroll) { const raf = window.requestAnimationFrame(scrollToBottom); return () => window.cancelAnimationFrame(raf); }
  }, [lastEntryKey, expanded, autoScroll, heightPx, filtersOpen, scrollToBottom]);

  useEffect(() => {
    if (!expanded || !bodyRef.current || typeof ResizeObserver === 'undefined') return;
    const ro = new ResizeObserver(() => { if (autoScroll) scrollToBottom(); });
    ro.observe(bodyRef.current);
    return () => ro.disconnect();
  }, [expanded, autoScroll, scrollToBottom]);

  const handleSelectPreset = (preset: 'S' | 'M' | 'L') => {
    const nextH = computeTerminalDrawerPresetHeight(preset, filtersOpen);
    setHeightPx(nextH); setActivePreset(preset); persistLayout(true, nextH, preset);
  };

  const handleToggleFilters = () => {
    setFiltersOpen((prev) => {
      const next = !prev;
      if (activePreset === 'S' || activePreset === 'M' || activePreset === 'L') setHeightPx(computeTerminalDrawerPresetHeight(activePreset, next));
      return next;
    });
  };

  const handleDragPointerDown = (e: React.PointerEvent<HTMLDivElement>) => {
    e.preventDefault();
    const target = e.currentTarget;
    target.setPointerCapture(e.pointerId);
    setDragging(true);
    const startY = e.clientY;
    const startH = heightPx;
    let latestH = startH;
    const onMove = (me: PointerEvent) => { latestH = clampTerminalDrawerHeight(startH + (startY - me.clientY)); setHeightPx(latestH); setActivePreset('custom'); };
    const onUp = (ue: PointerEvent) => {
      target.releasePointerCapture(ue.pointerId); setDragging(false);
      window.removeEventListener('pointermove', onMove); window.removeEventListener('pointerup', onUp);
      persistLayout(expanded, latestH, 'custom');
    };
    window.addEventListener('pointermove', onMove); window.addEventListener('pointerup', onUp);
  };

  const handleSeparatorKeyDown = (e: React.KeyboardEvent) => {
    let next = heightPx;
    if (e.key === 'ArrowUp') next = clampTerminalDrawerHeight(heightPx + 24);
    else if (e.key === 'ArrowDown') next = clampTerminalDrawerHeight(heightPx - 24);
    else if (e.key === 'PageUp') next = clampTerminalDrawerHeight(heightPx + 96);
    else if (e.key === 'PageDown') next = clampTerminalDrawerHeight(heightPx - 96);
    else if (e.key === 'Home') next = clampTerminalDrawerHeight(MAX_TERMINAL_HEIGHT);
    else if (e.key === 'End') next = MIN_TERMINAL_HEIGHT;
    else return;
    e.preventDefault();
    setHeightPx(next); setActivePreset('custom'); persistLayout(expanded, next, 'custom');
  };

  const maxBound = typeof window !== 'undefined' ? Math.min(MAX_TERMINAL_HEIGHT, Math.max(240, window.innerHeight - 165)) : MAX_TERMINAL_HEIGHT;
  const queuedCount = effectiveJob?.queuedCount ?? effectiveJob?.upNext?.length ?? 0;
  const rawPct = effectiveJob?.progressPercent ?? effectiveJob?.percentComplete ?? null;
  const pct = effectiveJob?.isRunning && typeof rawPct === 'number' ? Math.max(0, Math.min(100, Math.round(rawPct))) : null;

  return (
    <aside className="sl-terminal-docked fn-terminal-docked" style={expanded ? { height: `${heightPx}px` } : undefined} aria-label="Docked Terminal and Active Job Pane" data-testid="global-terminal-drawer">
      {expanded ? (
        <div
          role="separator" aria-orientation="horizontal" aria-label="Resize docked terminal pane"
          aria-valuemin={MIN_TERMINAL_HEIGHT} aria-valuemax={maxBound} aria-valuenow={heightPx} tabIndex={0}
          className={`sl-terminal-resize-handle fn-terminal-resize-handle${dragging ? ' dragging' : ''}`}
          onPointerDown={handleDragPointerDown} onKeyDown={handleSeparatorKeyDown} onDoubleClick={() => handleSelectPreset('M')}
        />
      ) : null}

      <TerminalToolbarAndFilters
        expanded={expanded} onToggleExpand={toggleExpanded} filtersOpen={filtersOpen} onToggleFilters={handleToggleFilters}
        connected={connected} jobStatus={effectiveJob}
        latestLine={filteredLogs[filteredLogs.length - 1] ?? activeLogs[activeLogs.length - 1] ?? null}
        queuedCount={queuedCount} pct={pct} activePreset={activePreset} onSelectPreset={handleSelectPreset}
        onOpenQueueModal={onOpenQueueModal}
        onStopCurrentJob={(e) => { e.stopPropagation(); setConfirmKind('current'); }}
        onStopAllJobs={(e) => { e.stopPropagation(); setConfirmKind('all'); }}
        onClear={async (e) => {
          e.stopPropagation();
          if (onClearTerminal) { const res = await onClearTerminal(); setInternalLogs(Array.isArray(res) ? res : []); }
          else { onClearLogs?.(); setInternalLogs([]); }
        }}
        verbosity={verbosity}
        onVerbosityChange={(next) => { setVerbosity(next); onPreferencesChanged?.(next, historyLimit); void onUpdatePreferences?.({ verbosity: next }); }}
        historyLimit={historyLimit}
        onHistoryLimitChange={(nextLimit) => { setHistoryLimit(nextLimit); onPreferencesChanged?.(verbosity, nextLimit); void onUpdatePreferences?.({ historyLimit: nextLimit }); }}
        searchFilter={searchFilter} onSearchFilterChange={setSearchFilter}
        wrapLines={wrapLines} onWrapLinesChange={setWrapLines}
        autoScroll={autoScroll} onAutoScrollChange={setAutoScroll}
        filteredCount={filteredLogs.length} totalCount={activeLogs.length}
      />

      {expanded ? (
        <div
          ref={bodyRef} className="sl-terminal-body fn-terminal-body"
          onScroll={(e) => {
            const el = e.currentTarget;
            const isAtBottom = el.scrollHeight - el.scrollTop - el.clientHeight <= 35;
            if (!isAtBottom && autoScroll) setAutoScroll(false);
            else if (isAtBottom && !autoScroll) setAutoScroll(true);
          }}
        >
          {filteredLogs.map((l) => (
            <div key={l.seq} className={`sl-log-line fn-log-line${wrapLines ? '' : ' nowrap'}`}>
              <span style={{ color: '#64748b' }}>{new Date(l.timestampUtc).toLocaleTimeString()} </span>
              <span style={{ color: logBadgeColor(l.level), fontWeight: 700 }}>[{l.level}]</span>{' '}
              <span>{l.message}</span>
            </div>
          ))}
          <div ref={bottomSentinelRef} data-last-entry-key={lastEntryKey} />
        </div>
      ) : null}

      <ConfirmActionModal
        open={confirmKind !== null}
        title={confirmKind === 'all' ? 'Stop Active Job & Clear Queue?' : 'Stop Current Job?'}
        message={
          confirmKind === 'all'
            ? `Are you sure you want to stop "${effectiveJob?.jobName || 'Active Job'}" and cancel all ${queuedCount} waiting job(s) in the FIFO queue?`
            : `Are you sure you want to stop "${effectiveJob?.jobName || 'Active Job'}"? Any remaining queued jobs will still proceed.`
        }
        confirmLabel={confirmKind === 'all' ? 'Stop All Jobs' : 'Stop Current Job'}
        cancelLabel="Keep Running"
        onConfirm={() => {
          if (confirmKind === 'all') {
            if (onCancelAllJobs) void onCancelAllJobs();
            else void fetch('/api/v1/jobs/cancel-all', { method: 'POST' });
            onAppendLogLine?.('[JOB] Cancel All requested.');
          } else {
            if (onCancelCurrentJob) void onCancelCurrentJob();
            else void fetch('/api/v1/jobs/cancel-current', { method: 'POST' });
            onAppendLogLine?.('[JOB] Active job cancellation requested.');
          }
          setConfirmKind(null);
        }}
        onCancel={() => setConfirmKind(null)}
      />
    </aside>
  );
};
