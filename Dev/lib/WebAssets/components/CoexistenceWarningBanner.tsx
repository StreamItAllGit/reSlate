// @module: Foundation.UI.Web.CoexistenceWarningBanner
// @purpose: Shared sister-app coexistence detection & one-click recommended non-destructive settings banner (Standard 17).
// @depends-on: react, lucide-react
// @exports: CoexistenceWarningBanner, CoexistenceStatusDto

import React, { useEffect, useState } from 'react';
import { ShieldAlert, Sparkles, CheckCircle2, X } from 'lucide-react';

export interface CoexistenceDetectedAppDto {
  name: string;
  category: string;
  detectedVia: string;
  evidencePath: string;
}

export interface CoexistenceStatusDto {
  sisterAppsDetected: boolean;
  detectedApps: CoexistenceDetectedAppDto[];
  recommendationsApplied: boolean;
  bannerDismissed: boolean;
  recommendationsSummary: string[];
}

interface CoexistenceWarningBannerProps {
  onApplied?: () => void;
  fetchStatus?: () => Promise<CoexistenceStatusDto>;
  applyRecommendations?: (opts: { applyRecommendedSettings: boolean; dismissBanner: boolean }) => Promise<{ message: string; status: CoexistenceStatusDto }>;
}

async function defaultFetchCoexistenceStatus(): Promise<CoexistenceStatusDto> {
  const res = await fetch('/api/v1/coexistence/status');
  if (!res.ok) throw new Error(`HTTP ${res.status}`);
  return res.json();
}

async function defaultApplyCoexistenceRecommendations(opts: { applyRecommendedSettings: boolean; dismissBanner: boolean }): Promise<{ message: string; status: CoexistenceStatusDto }> {
  const res = await fetch('/api/v1/coexistence/apply-recommendations', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(opts)
  });
  if (!res.ok) throw new Error(`HTTP ${res.status}`);
  return res.json();
}

export const CoexistenceWarningBanner: React.FC<CoexistenceWarningBannerProps> = ({
  onApplied,
  fetchStatus = defaultFetchCoexistenceStatus,
  applyRecommendations = defaultApplyCoexistenceRecommendations,
}) => {
  const [status, setStatus] = useState<CoexistenceStatusDto | null>(null);
  const [busy, setBusy] = useState(false);
  const [toast, setToast] = useState<string>('');

  const load = async () => {
    try {
      const s = await fetchStatus();
      setStatus(s);
    } catch {
      // Ignore during startup
    }
  };

  useEffect(() => {
    load();
  }, []);

  if (!status || !status.sisterAppsDetected || status.bannerDismissed) {
    return toast ? (
      <div className="sl-coexistence-banner sl-coexistence-applied" data-testid="coexistence-applied-toast">
        <CheckCircle2 size={16} />
        <span>{toast}</span>
        <button type="button" className="sl-icon-btn" onClick={() => setToast('')} aria-label="Dismiss notification">
          <X size={14} />
        </button>
      </div>
    ) : null;
  }

  if (status.recommendationsApplied) {
    return null;
  }

  const handleApply = async () => {
    setBusy(true);
    try {
      const res = await applyRecommendations({
        applyRecommendedSettings: true,
        dismissBanner: false
      });
      setStatus(res.status);
      setToast(res.message);
      onApplied?.();
    } finally {
      setBusy(false);
    }
  };

  const handleDismiss = async () => {
    setBusy(true);
    try {
      const res = await applyRecommendations({
        applyRecommendedSettings: false,
        dismissBanner: true
      });
      setStatus(res.status);
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="sl-coexistence-banner" data-testid="coexistence-warning-banner">
      <div className="sl-coexistence-icon">
        <ShieldAlert size={18} />
      </div>
      <div className="sl-coexistence-body">
        <div className="sl-coexistence-title">
          Sister-App Coexistence Detected:{' '}
          <strong>{status.detectedApps.map((a) => `${a.name} (${a.category})`).join(', ')}</strong>
        </div>
        <div className="sl-coexistence-sub">
          Recommended non-destructive coexistence settings are available so both apps can safely share the same library root without duplicate moves or sidecar collisions.
        </div>
      </div>
      <div className="sl-coexistence-actions">
        <button
          type="button"
          className="sl-btn sl-btn-primary sl-btn-sm"
          disabled={busy}
          onClick={handleApply}
          data-testid="btn-apply-coexistence"
        >
          <Sparkles size={14} />
          <span>Apply Recommended Coexistence Settings</span>
        </button>
        <button
          type="button"
          className="sl-icon-btn"
          disabled={busy}
          onClick={handleDismiss}
          title="Dismiss coexistence banner"
          aria-label="Dismiss coexistence banner"
        >
          <X size={15} />
        </button>
      </div>
    </div>
  );
};
