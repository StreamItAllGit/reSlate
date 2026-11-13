// @module: Foundation.UI.Web.TerminalDrawerCore
// @purpose: Shared pure types, constants, log coalescing, verbosity filters, and responsive height geometry for Standard 09 §4.5 docked terminal drawers.
// @depends-on: none
// @exports: TerminalVerbosityLevel, TerminalHeightPreset, TerminalLogFilter, TerminalLogLine, TerminalActiveJobDto, MIN_TERMINAL_HEIGHT, DEFAULT_TERMINAL_HEIGHT, MAX_TERMINAL_HEIGHT, shouldShowLogLine, isReplaceableProgressLine, coalesceTerminalLogBatch, logBadgeColor, isMobileTerminalViewport, clampTerminalDrawerHeight, computeTerminalDrawerPresetHeight, filterTerminalLines

export type TerminalVerbosityLevel = 'Quiet' | 'Normal' | 'Verbose' | 'Trace';
export type TerminalHeightPreset = 'S' | 'M' | 'L' | 'custom';
export type TerminalLogFilter = 'all' | 'error' | 'warn' | 'job';

export interface TerminalLogLine {
  seq: number;
  timestampUtc: string;
  level: string;
  message: string;
}

export interface TerminalActiveJobDto {
  isRunning: boolean;
  jobName?: string | null;
  currentStep?: string | null;
  phase?: string | null;
  currentItem?: string | null;
  currentItemIndex?: number | null;
  totalItemCount?: number | null;
  processedItems?: number | null;
  totalItems?: number | null;
  progressPercent?: number | null;
  percentComplete?: number | null;
  queuedCount?: number | null;
  upNext?: string[] | null;
  lastSummary?: string | null;
}

export const MIN_TERMINAL_HEIGHT = 168;
export const DEFAULT_TERMINAL_HEIGHT = 280;
export const MAX_TERMINAL_HEIGHT = 600;

export function shouldShowLogLine(level: string, verbosity: TerminalVerbosityLevel): boolean {
  const upper = (level || 'INFO').toUpperCase();
  if (upper === 'ERROR' || upper === 'WARN') return true;
  if (upper === 'JOB' || upper === 'PROGRESS' || upper === 'HEARTBEAT') {
    return verbosity === 'Normal' || verbosity === 'Verbose' || verbosity === 'Trace';
  }
  if (['INFO', 'VERBOSE', 'NET', 'VERIFY', 'SYNC'].includes(upper)) {
    return verbosity === 'Verbose' || verbosity === 'Trace';
  }
  if (['DEBUG', 'TRACE', 'CMD', 'CACHE'].includes(upper)) {
    return verbosity === 'Trace';
  }
  return true;
}

export function isReplaceableProgressLine(line: TerminalLogLine | null | undefined): boolean {
  if (!line || !line.message) return false;
  const upper = (line.level || 'INFO').toUpperCase();
  if (upper === 'ERROR' || upper === 'WARN' || upper === 'JOB') return false;
  const trimmed = line.message.trimStart();
  if (trimmed.startsWith('[HEARTBEAT]')) return true;
  if (trimmed.startsWith('[SQLite Commit] Upserting catalog entries:')) return true;
  if (trimmed.startsWith('Overall:') && trimmed.includes('Speed:')) return true;
  if (trimmed.includes('Hashing ') && trimmed.includes(' MB)...')) return true;
  if (/^\(\d+\s*\/\s*[1-9]\d*\)/.test(trimmed)) return true;
  return false;
}

export function coalesceTerminalLogBatch(
  prev: TerminalLogLine[],
  batch: TerminalLogLine[],
  cap = 250
): TerminalLogLine[] {
  if (!Array.isArray(batch) || batch.length === 0) return prev;
  const effectiveCap = Math.max(50, cap);
  const existingMax = prev.length > 0 ? prev[prev.length - 1].seq : 0;
  const fresh = batch.filter((l) => l.seq > existingMax);
  if (fresh.length === 0) return prev;
  const next = [...prev];
  for (const item of fresh) {
    const last = next.length > 0 ? next[next.length - 1] : null;
    if (last && isReplaceableProgressLine(item) && isReplaceableProgressLine(last)) {
      next[next.length - 1] = item;
    } else {
      next.push(item);
    }
  }
  return next.length > effectiveCap ? next.slice(next.length - effectiveCap) : next;
}

export function logBadgeColor(level: string): string {
  const upper = (level || 'INFO').toUpperCase();
  if (upper === 'ERROR') return '#f87171';
  if (upper === 'WARN') return '#fbbf24';
  if (upper === 'JOB' || upper === 'PROGRESS' || upper === 'HEARTBEAT') return '#38bdf8';
  if (['NET', 'VERIFY', 'SYNC'].includes(upper)) return '#34d399';
  if (['DEBUG', 'TRACE', 'CMD', 'CACHE'].includes(upper)) return '#94a3b8';
  return '#c084fc';
}

export function isMobileTerminalViewport(): boolean {
  return typeof window !== 'undefined' && (window.innerWidth <= 820 || window.innerHeight <= 480);
}

export function clampTerminalDrawerHeight(px: number): number {
  const isMobileLandscape =
    typeof window !== 'undefined' && window.innerWidth <= 900 && window.innerHeight <= 480;
  const minH = isMobileLandscape ? 140 : MIN_TERMINAL_HEIGHT;
  const reserve = isMobileLandscape ? 120 : 165;
  const avail =
    typeof window !== 'undefined'
      ? Math.max(minH + 20, window.innerHeight - reserve)
      : MAX_TERMINAL_HEIGHT;
  return Math.round(Math.max(minH, Math.min(Math.min(MAX_TERMINAL_HEIGHT, avail), px)));
}

export function computeTerminalDrawerPresetHeight(
  preset: 'S' | 'M' | 'L',
  filtersOpenOnMobile: boolean
): number {
  if (typeof window === 'undefined') return DEFAULT_TERMINAL_HEIGHT;
  const isMobile = isMobileTerminalViewport();
  const isLandscape = window.innerHeight <= 480;
  if (isMobile && isLandscape) {
    const ratio = preset === 'S' ? 0.42 : preset === 'M' ? 0.54 : 0.66;
    return clampTerminalDrawerHeight(window.innerHeight * ratio);
  }
  const extraMobileFilterChrome = isMobile && filtersOpenOnMobile ? 64 : 0;
  if (preset === 'S') {
    return clampTerminalDrawerHeight(Math.max(178, window.innerHeight * 0.25) + extraMobileFilterChrome);
  }
  if (preset === 'M') {
    return clampTerminalDrawerHeight(Math.max(275, window.innerHeight * 0.4) + extraMobileFilterChrome);
  }
  return clampTerminalDrawerHeight(Math.max(390, window.innerHeight * 0.58));
}

export function filterTerminalLines(lines: string[], filter: TerminalLogFilter): string[] {
  if (filter === 'all') return lines;
  return lines.filter((line) => {
    const upper = line.toUpperCase();
    if (filter === 'error') {
      return (
        upper.includes('[ERROR]') ||
        upper.includes(' ERROR ') ||
        upper.includes('CRITICAL') ||
        upper.includes('FAILED')
      );
    }
    if (filter === 'warn') {
      return upper.includes('[WARN]') || upper.includes(' WARNING ');
    }
    if (filter === 'job') {
      return (
        upper.includes('[JOB]') ||
        upper.includes('SCAN ') ||
        upper.includes('FIX &') ||
        upper.includes('VERIFY ') ||
        upper.includes('ORGANIZ') ||
        upper.includes('BATCH ')
      );
    }
    return true;
  });
}
