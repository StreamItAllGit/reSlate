// @module: Foundation.UI.WebAssets.TerminalJs
// @purpose: Self-contained Vanilla ES Module implementing the Standard 09 §4.5 Docked Resizable Bottom Terminal (ShadowLibrary Strict Standard) for FastAPI/Kestrel/Vanilla JS apps.
// @depends-on: foundation-terminal.css
// @exports: MIN_TERMINAL_HEIGHT, DEFAULT_TERMINAL_HEIGHT, MAX_TERMINAL_HEIGHT, shouldShowLogLine, isReplaceableProgressLine, coalesceTerminalLogBatch, logBadgeColor, isMobileTerminalViewport, clampTerminalDrawerHeight, computeTerminalDrawerPresetHeight, mountFoundationTerminalDrawer

export const MIN_TERMINAL_HEIGHT = 168;
export const DEFAULT_TERMINAL_HEIGHT = 280;
export const MAX_TERMINAL_HEIGHT = 600;

export function shouldShowLogLine(level, verbosity = 'Verbose') {
  const upper = String(level || 'INFO').toUpperCase();
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

export function isReplaceableProgressLine(line) {
  if (!line || !line.message) return false;
  const upper = String(line.level || 'INFO').toUpperCase();
  if (upper === 'ERROR' || upper === 'WARN' || upper === 'JOB') return false;
  const trimmed = String(line.message).trimStart();
  if (trimmed.startsWith('[HEARTBEAT]')) return true;
  if (trimmed.startsWith('[SQLite Commit] Upserting catalog entries:')) return true;
  if (trimmed.startsWith('Overall:') && trimmed.includes('Speed:')) return true;
  if (trimmed.includes('Hashing ') && trimmed.includes(' MB)...')) return true;
  if (/^\(\d+\s*\/\s*[1-9]\d*\)/.test(trimmed)) return true;
  return false;
}

export function coalesceTerminalLogBatch(prev = [], batch = [], cap = 250) {
  if (!Array.isArray(batch) || batch.length === 0) return prev;
  const effectiveCap = Math.max(50, Number(cap) || 250);
  const existingMax = prev.length > 0 ? prev[prev.length - 1].seq : 0;
  const fresh = batch.filter((l) => l && l.seq > existingMax);
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

export function logBadgeColor(level) {
  const upper = String(level || 'INFO').toUpperCase();
  if (upper === 'ERROR') return '#f87171';
  if (upper === 'WARN') return '#fbbf24';
  if (upper === 'JOB' || upper === 'PROGRESS' || upper === 'HEARTBEAT') return '#38bdf8';
  if (['NET', 'VERIFY', 'SYNC'].includes(upper)) return '#34d399';
  if (['DEBUG', 'TRACE', 'CMD', 'CACHE'].includes(upper)) return '#94a3b8';
  return '#c084fc';
}

export function isMobileTerminalViewport() {
  return typeof window !== 'undefined' && (window.innerWidth <= 820 || window.innerHeight <= 480);
}

export function clampTerminalDrawerHeight(px) {
  const isMobileLandscape = typeof window !== 'undefined' && window.innerWidth <= 900 && window.innerHeight <= 480;
  const minH = isMobileLandscape ? 140 : MIN_TERMINAL_HEIGHT;
  const reserve = isMobileLandscape ? 120 : 165;
  const avail = typeof window !== 'undefined' ? Math.max(minH + 20, window.innerHeight - reserve) : MAX_TERMINAL_HEIGHT;
  return Math.round(Math.max(minH, Math.min(Math.min(MAX_TERMINAL_HEIGHT, avail), Number(px) || DEFAULT_TERMINAL_HEIGHT)));
}

export function computeTerminalDrawerPresetHeight(preset, filtersOpenOnMobile) {
  if (typeof window === 'undefined') return DEFAULT_TERMINAL_HEIGHT;
  const isMobile = isMobileTerminalViewport();
  const isLandscape = window.innerHeight <= 480;
  if (isMobile && isLandscape) {
    const ratio = preset === 'S' ? 0.42 : preset === 'M' ? 0.54 : 0.66;
    return clampTerminalDrawerHeight(window.innerHeight * ratio);
  }
  const extra = isMobile && filtersOpenOnMobile ? 64 : 0;
  if (preset === 'S') return clampTerminalDrawerHeight(Math.max(178, window.innerHeight * 0.25) + extra);
  if (preset === 'M') return clampTerminalDrawerHeight(Math.max(275, window.innerHeight * 0.4) + extra);
  return clampTerminalDrawerHeight(Math.max(390, window.innerHeight * 0.58));
}

function escapeHtml(str) {
  return String(str ?? '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

export function mountFoundationTerminalDrawer(options = {}) {
  const container = typeof options.container === 'string' ? document.querySelector(options.container) : options.container;
  if (!container) throw new Error('mountFoundationTerminalDrawer requires a valid container element.');

  const prefix = options.storagePrefix || 'sl-terminal';
  const state = {
    expanded: (() => { try { return localStorage.getItem(`${prefix}-open`) === 'true'; } catch { return false; } })(),
    filtersOpen: !isMobileTerminalViewport(),
    heightPx: (() => {
      try {
        const saved = Number(localStorage.getItem(`${prefix}-height`));
        return Number.isFinite(saved) && saved >= 140 ? clampTerminalDrawerHeight(saved) : DEFAULT_TERMINAL_HEIGHT;
      } catch { return DEFAULT_TERMINAL_HEIGHT; }
    })(),
    activePreset: (() => {
      try {
        const s = localStorage.getItem(`${prefix}-preset`);
        return s === 'S' || s === 'M' || s === 'L' || s === 'custom' ? s : 'M';
      } catch { return 'M'; }
    })(),
    connected: options.connected !== undefined ? Boolean(options.connected) : true,
    jobStatus: options.jobStatus || null,
    logs: Array.isArray(options.logs) ? coalesceTerminalLogBatch([], options.logs, options.historyLimit || 250) : [],
    verbosity: options.verbosity || 'Verbose',
    historyLimit: Number(options.historyLimit) || 250,
    searchFilter: '',
    wrapLines: true,
    autoScroll: true,
    confirmKind: null,
  };

  const persist = () => {
    try {
      localStorage.setItem(`${prefix}-open`, String(state.expanded));
      localStorage.setItem(`${prefix}-height`, String(state.heightPx));
      localStorage.setItem(`${prefix}-preset`, state.activePreset);
    } catch { /* ignore */ }
  };

  const aside = document.createElement('aside');
  aside.className = 'sl-terminal-docked fn-terminal-docked';
  aside.setAttribute('aria-label', 'Docked Terminal and Active Job Pane');
  container.appendChild(aside);
  let ro = null;

  const getFilteredLogs = () => {
    const q = state.searchFilter.trim().toLowerCase();
    return state.logs.filter((l) => shouldShowLogLine(l.level, state.verbosity) && (!q || String(l.message).toLowerCase().includes(q) || String(l.level).toLowerCase().includes(q)));
  };

  const scrollToBottom = () => {
    const body = aside.querySelector('.fn-terminal-body');
    if (body && state.autoScroll) body.scrollTop = body.scrollHeight;
  };

  const render = () => {
    const filtered = getFilteredLogs();
    const latest = filtered[filtered.length - 1] || state.logs[state.logs.length - 1] || null;
    const job = state.jobStatus;
    const queuedCount = job?.queuedCount ?? (Array.isArray(job?.upNext) ? job.upNext.length : 0);
    const rawPct = job?.progressPercent ?? job?.percentComplete ?? null;
    const pct = job?.isRunning && typeof rawPct === 'number' ? Math.max(0, Math.min(100, Math.round(rawPct))) : null;
    const maxBound = typeof window !== 'undefined' ? Math.min(MAX_TERMINAL_HEIGHT, Math.max(240, window.innerHeight - 165)) : MAX_TERMINAL_HEIGHT;

    aside.style.height = state.expanded ? `${state.heightPx}px` : '';
    aside.innerHTML = `
      ${state.expanded ? `<div role="separator" aria-orientation="horizontal" aria-label="Resize docked terminal pane" aria-valuemin="${MIN_TERMINAL_HEIGHT}" aria-valuemax="${maxBound}" aria-valuenow="${state.heightPx}" tabindex="0" class="sl-terminal-resize-handle fn-terminal-resize-handle"></div>` : ''}
      <div class="sl-terminal-header fn-terminal-header" role="button" tabindex="0" aria-expanded="${state.expanded}">
        <div class="sl-row sl-terminal-title-row fn-terminal-title-row">
          <span class="sl-badge ${state.connected ? 'sl-badge-ok' : 'sl-badge-neutral'}">${state.connected ? 'LIVE' : 'POLL'}</span>
          <strong class="sl-mono" style="font-size:0.8rem;white-space:nowrap">Terminal<span class="sl-desktop-only"> (Ctrl+\`)</span></strong>
          ${job?.isRunning ? `
            <div class="sl-row sl-terminal-running-row fn-terminal-running-row">
              <span class="sl-badge sl-badge-pr sl-terminal-running-badge fn-terminal-running-badge">RUNNING: ${escapeHtml(job.jobName || 'Active Job')}${pct !== null ? ` (${pct}%)` : ''}${job.currentStep ? ` — ${escapeHtml(job.currentStep)}` : ''}</span>
              ${pct !== null ? `<div class="sl-progress-track sl-terminal-mini-progress fn-terminal-mini-progress"><div class="sl-progress-fill" style="width:${pct}%"></div></div>` : ''}
            </div>
          ` : latest ? `<span class="sl-mono sl-terminal-latest-preview fn-terminal-latest-preview">[${escapeHtml(latest.level)}] ${escapeHtml(latest.message)}</span>` : ''}
        </div>
        <div class="sl-row sl-terminal-actions-row fn-terminal-actions-row">
          ${state.expanded ? `<div class="sl-row" style="gap:0.2rem">${['S', 'M', 'L'].map((p) => `<button type="button" data-preset="${p}" class="sl-btn sl-btn-sm ${state.activePreset === p ? 'sl-btn-primary' : ''}">${p}</button>`).join('')}</div>` : ''}
          ${job?.isRunning ? `
            <button type="button" data-action="stop-current" class="sl-btn sl-btn-danger sl-btn-sm" title="Stop Current Job">⏹ Stop<span class="sl-desktop-only"> Current Job</span></button>
            <button type="button" data-action="stop-all" class="sl-btn sl-btn-danger sl-btn-sm" title="Stop All Jobs">⏹ <span class="sl-desktop-only">Stop </span>All<span class="sl-desktop-only"> Jobs</span>${queuedCount > 0 ? ` (${queuedCount})` : ''}</button>
          ` : ''}
          ${state.expanded ? `<button type="button" data-action="toggle-filters" aria-label="Toggle terminal filters" aria-expanded="${state.filtersOpen}" class="sl-btn sl-btn-sm ${state.filtersOpen ? 'sl-btn-primary' : ''}">⚙ <span class="sl-desktop-only">Filters</span><span class="sl-mobile-only">Flt</span></button>` : ''}
          <button type="button" data-action="clear" class="sl-btn sl-btn-sm"><span class="sl-desktop-only">Clear Logs</span><span class="sl-mobile-only">Clr</span></button>
          <button type="button" data-action="toggle-expand" class="sl-btn sl-btn-sm">${state.expanded ? '▼<span class="sl-desktop-only"> Hide</span>' : '▲<span class="sl-desktop-only"> Expand</span>'}</button>
        </div>
      </div>
      ${state.expanded && state.filtersOpen ? `
        <div class="sl-terminal-toolbar fn-terminal-toolbar">
          <div class="sl-row sl-terminal-toolbar-selects fn-terminal-toolbar-selects">
            <label class="sl-row sl-terminal-filter-field fn-terminal-filter-field"><span>Verbosity:</span><select data-field="verbosity" class="sl-select sl-btn-sm" aria-label="Terminal log verbosity">${['Quiet', 'Normal', 'Verbose', 'Trace'].map((v) => `<option value="${v}" ${state.verbosity === v ? 'selected' : ''}>${v}</option>`).join('')}</select></label>
            <label class="sl-row sl-terminal-filter-field fn-terminal-filter-field"><span>History:</span><select data-field="history" class="sl-select sl-btn-sm" aria-label="Live terminal history line cap">${[100, 250, 500, 1000].map((h) => `<option value="${h}" ${state.historyLimit === h ? 'selected' : ''}>${h} lines</option>`).join('')}</select></label>
            <input type="search" data-field="search" class="sl-input sl-btn-sm sl-terminal-search-input fn-terminal-search-input" placeholder="Filter logs..." aria-label="Filter terminal output" value="${escapeHtml(state.searchFilter)}" />
          </div>
          <div class="sl-row sl-terminal-toolbar-toggles fn-terminal-toolbar-toggles">
            <label class="sl-row" style="cursor:pointer;gap:4px"><input type="checkbox" data-field="wrap" ${state.wrapLines ? 'checked' : ''} /><span>Wrap lines</span></label>
            <label class="sl-row" style="cursor:pointer;gap:4px"><input type="checkbox" data-field="autoscroll" ${state.autoScroll ? 'checked' : ''} /><span>Auto-scroll</span></label>
            <span class="sl-mono" style="color:#94a3b8">${filtered.length}/${state.logs.length} lines</span>
          </div>
        </div>
      ` : ''}
      ${state.expanded ? `<div class="sl-terminal-body fn-terminal-body">${filtered.map((l) => `<div class="sl-log-line fn-log-line${state.wrapLines ? '' : ' nowrap'}"><span style="color:#64748b">${new Date(l.timestampUtc || Date.now()).toLocaleTimeString()} </span><span style="color:${logBadgeColor(l.level)};font-weight:700">[${escapeHtml(l.level)}]</span> <span>${escapeHtml(l.message)}</span></div>`).join('')}</div>` : ''}
      ${state.confirmKind ? `
        <div class="fn-terminal-modal-backdrop" role="dialog" aria-modal="true">
          <div class="fn-terminal-modal">
            <strong>${state.confirmKind === 'all' ? 'Stop Active Job & Clear Queue?' : 'Stop Current Job?'}</strong>
            <p style="margin:0;font-size:0.85rem;color:#94a3b8">${state.confirmKind === 'all' ? 'Terminate the active job and clear all queued items?' : 'Stop the current job while allowing queued jobs to proceed?'}</p>
            <div class="sl-row" style="justify-content:flex-end">
              <button type="button" data-action="confirm-cancel" class="sl-btn sl-btn-sm">Keep Running</button>
              <button type="button" data-action="confirm-proceed" class="sl-btn sl-btn-danger sl-btn-sm">${state.confirmKind === 'all' ? 'Stop All Jobs' : 'Stop Current Job'}</button>
            </div>
          </div>
        </div>
      ` : ''}
    `;
    wireEvents();
    if (state.expanded) requestAnimationFrame(scrollToBottom);
  };

  const wireEvents = () => {
    aside.querySelector('.fn-terminal-header')?.addEventListener('click', () => { state.expanded = !state.expanded; persist(); render(); });
    aside.querySelector('.fn-terminal-actions-row')?.addEventListener('click', (e) => e.stopPropagation());
    aside.querySelectorAll('[data-preset]').forEach((btn) => {
      btn.addEventListener('click', () => {
        const p = btn.getAttribute('data-preset');
        state.activePreset = p;
        state.heightPx = computeTerminalDrawerPresetHeight(p, state.filtersOpen);
        persist();
        render();
      });
    });
    aside.querySelector('[data-action="toggle-expand"]')?.addEventListener('click', () => { state.expanded = !state.expanded; persist(); render(); });
    aside.querySelector('[data-action="toggle-filters"]')?.addEventListener('click', () => {
      state.filtersOpen = !state.filtersOpen;
      if (state.activePreset === 'S' || state.activePreset === 'M' || state.activePreset === 'L') {
        state.heightPx = computeTerminalDrawerPresetHeight(state.activePreset, state.filtersOpen);
      }
      render();
    });
    aside.querySelector('[data-action="clear"]')?.addEventListener('click', async () => { if (options.onClearTerminal) await options.onClearTerminal(); state.logs = []; render(); });
    aside.querySelector('[data-action="stop-current"]')?.addEventListener('click', () => { state.confirmKind = 'current'; render(); });
    aside.querySelector('[data-action="stop-all"]')?.addEventListener('click', () => { state.confirmKind = 'all'; render(); });
    aside.querySelector('[data-action="confirm-cancel"]')?.addEventListener('click', () => { state.confirmKind = null; render(); });
    aside.querySelector('[data-action="confirm-proceed"]')?.addEventListener('click', () => {
      const kind = state.confirmKind;
      state.confirmKind = null;
      if (kind === 'all') options.onCancelAllJobs?.();
      else options.onCancelCurrentJob?.();
      render();
    });
    aside.querySelector('[data-field="verbosity"]')?.addEventListener('change', (e) => { state.verbosity = e.target.value; render(); });
    aside.querySelector('[data-field="history"]')?.addEventListener('change', (e) => { state.historyLimit = Number(e.target.value); render(); });
    aside.querySelector('[data-field="search"]')?.addEventListener('input', (e) => { state.searchFilter = e.target.value; render(); });
    aside.querySelector('[data-field="wrap"]')?.addEventListener('change', (e) => { state.wrapLines = e.target.checked; render(); });
    aside.querySelector('[data-field="autoscroll"]')?.addEventListener('change', (e) => { state.autoScroll = e.target.checked; render(); });

    const sep = aside.querySelector('[role="separator"]');
    sep?.addEventListener('pointerdown', (e) => {
      e.preventDefault();
      sep.setPointerCapture(e.pointerId);
      const startY = e.clientY;
      const startH = state.heightPx;
      const onMove = (me) => {
        state.heightPx = clampTerminalDrawerHeight(startH + (startY - me.clientY));
        state.activePreset = 'custom';
        aside.style.height = `${state.heightPx}px`;
      };
      const onUp = (ue) => {
        sep.releasePointerCapture(ue.pointerId);
        window.removeEventListener('pointermove', onMove);
        window.removeEventListener('pointerup', onUp);
        persist();
        render();
      };
      window.addEventListener('pointermove', onMove);
      window.addEventListener('pointerup', onUp);
    });

    const body = aside.querySelector('.fn-terminal-body');
    if (body && typeof ResizeObserver !== 'undefined') {
      ro?.disconnect();
      ro = new ResizeObserver(() => scrollToBottom());
      ro.observe(body);
    }
  };

  const onWindowResize = () => { state.heightPx = clampTerminalDrawerHeight(state.heightPx); state.filtersOpen = !isMobileTerminalViewport(); render(); };
  const onWindowKeyDown = (e) => { if (e.ctrlKey && e.key === '`') { e.preventDefault(); state.expanded = !state.expanded; persist(); render(); } };
  if (typeof window !== 'undefined') { window.addEventListener('resize', onWindowResize); window.addEventListener('keydown', onWindowKeyDown); }

  render();
  return {
    appendLogs(batch) { state.logs = coalesceTerminalLogBatch(state.logs, batch, state.historyLimit); render(); },
    setLogs(logs) { state.logs = coalesceTerminalLogBatch([], logs, state.historyLimit); render(); },
    setJobStatus(status) { state.jobStatus = status; render(); },
    setConnected(c) { state.connected = Boolean(c); render(); },
    setExpanded(open) { state.expanded = Boolean(open); persist(); render(); },
    destroy() { if (typeof window !== 'undefined') { window.removeEventListener('resize', onWindowResize); window.removeEventListener('keydown', onWindowKeyDown); } ro?.disconnect(); aside.remove(); },
  };
}

if (typeof window !== 'undefined') {
  window.FoundationTerminalDrawer = {
    MIN_TERMINAL_HEIGHT, DEFAULT_TERMINAL_HEIGHT, MAX_TERMINAL_HEIGHT, shouldShowLogLine,
    isReplaceableProgressLine, coalesceTerminalLogBatch, logBadgeColor, isMobileTerminalViewport,
    clampTerminalDrawerHeight, computeTerminalDrawerPresetHeight, mountFoundationTerminalDrawer,
  };
}
