// @module: Foundation.UI.Web.HybridPathPicker
// @purpose: Shared hybrid folder/file path picker with native OS folder dialog trigger + remote host filesystem browser modal.
// @depends-on: react, lucide-react, ./StyledTooltip
// @exports: HybridPathPicker, BrowseItemDto

import React, { useState } from 'react';
import { FolderOpen, HardDrive, ChevronUp, Folder, Check, X } from 'lucide-react';
import { StyledTooltip } from './StyledTooltip';

export interface BrowseItemDto {
  name: string;
  fullPath: string;
  isDrive: boolean;
}

export interface FoundationHybridPathPickerProps {
  label?: string;
  value: string;
  onChange: (nextPath: string) => void;
  mode?: 'folder' | 'file';
  placeholder?: string;
  helpText?: string;
  testId?: string;
  onBrowseHost?: (path?: string) => Promise<{ currentPath: string; parentPath?: string; items: BrowseItemDto[] }>;
  onOpenNativePicker?: (mode: 'folder' | 'file', title: string, initialDir?: string) => Promise<{ cancelled: boolean; path?: string; fallbackRequired?: boolean }>;
}

async function defaultBrowseHostDirectory(path?: string): Promise<{ currentPath: string; parentPath?: string; items: BrowseItemDto[] }> {
  const qs = path ? `?path=${encodeURIComponent(path)}` : '';
  const res = await fetch(`/api/v1/fs/browse${qs}`);
  if (!res.ok) throw new Error(`HTTP ${res.status}`);
  return res.json();
}

async function defaultOpenNativePicker(mode: 'folder' | 'file', title: string, initialDir?: string): Promise<{ cancelled: boolean; path?: string; fallbackRequired?: boolean }> {
  const res = await fetch('/api/v1/fs/native-picker', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ mode, title, initialDirectory: initialDir })
  });
  if (!res.ok) return { cancelled: true, fallbackRequired: true };
  return res.json();
}

export const HybridPathPicker: React.FC<FoundationHybridPathPickerProps> = ({
  label,
  value,
  onChange,
  mode = 'folder',
  placeholder = 'D:\\ROMs\\...',
  helpText,
  testId,
  onBrowseHost = defaultBrowseHostDirectory,
  onOpenNativePicker = defaultOpenNativePicker,
}) => {
  const [browserOpen, setBrowserOpen] = useState(false);
  const [currentBrowsePath, setCurrentBrowsePath] = useState<string>('');
  const [parentPath, setParentPath] = useState<string>('');
  const [items, setItems] = useState<BrowseItemDto[]>([]);
  const [loading, setLoading] = useState(false);

  const loadDirectory = async (target?: string) => {
    setLoading(true);
    try {
      const res = await onBrowseHost(target);
      setCurrentBrowsePath(res.currentPath || '');
      setParentPath(res.parentPath || '');
      setItems(res.items || []);
    } catch {
      setItems([]);
    } finally {
      setLoading(false);
    }
  };

  const handleNativeBrowse = async () => {
    setLoading(true);
    try {
      const res = await onOpenNativePicker(mode, label || 'Select Path', value);
      if (res.fallbackRequired) {
        setBrowserOpen(true);
        await loadDirectory(value || undefined);
        return;
      }
      if (!res.cancelled && res.path) {
        onChange(res.path);
      }
    } catch {
      setBrowserOpen(true);
      await loadDirectory(value || undefined);
    } finally {
      setLoading(false);
    }
  };

  const handleOpenWebBrowser = async () => {
    setBrowserOpen(true);
    await loadDirectory(value || undefined);
  };

  return (
    <div className="sl-path-picker">
      {label && <label className="sl-label">{label}</label>}
      <div className="sl-path-row">
        <input
          type="text"
          className="sl-input"
          value={value}
          onChange={(e) => onChange(e.target.value)}
          placeholder={placeholder}
          data-testid={testId}
        />
        <StyledTooltip
          title="Browse Host Path"
          body="Open native Windows Explorer picker on local host (falls back to remote tree over LAN/VPN)."
        >
          <button
            type="button"
            className="sl-btn sl-btn-secondary"
            onClick={handleNativeBrowse}
            disabled={loading}
          >
            <FolderOpen size={15} />
            <span>Browse...</span>
          </button>
        </StyledTooltip>
        <StyledTooltip
          title="Host Filesystem Browser"
          body="Browse host drives and folders directly inside the browser modal."
        >
          <button
            type="button"
            className="sl-btn sl-btn-ghost"
            onClick={handleOpenWebBrowser}
            disabled={loading}
          >
            <HardDrive size={15} />
          </button>
        </StyledTooltip>
      </div>
      {helpText && <div className="sl-help-text">{helpText}</div>}

      {browserOpen && (
        <div className="sl-modal-backdrop" onClick={() => setBrowserOpen(false)}>
          <div className="sl-modal sl-browser-modal" onClick={(e) => e.stopPropagation()}>
            <div className="sl-modal-header">
              <div className="sl-modal-title">
                <HardDrive size={18} />
                <span>Host Filesystem Browser {label ? `— ${label}` : ''}</span>
              </div>
              <button type="button" className="sl-icon-btn" onClick={() => setBrowserOpen(false)} aria-label="Close Host Filesystem Browser">
                <X size={16} />
              </button>
            </div>
            <div className="sl-browser-toolbar">
              <button
                type="button"
                className="sl-btn sl-btn-ghost sl-btn-sm"
                onClick={() => loadDirectory(undefined)}
              >
                Drives
              </button>
              {currentBrowsePath && (
                <button
                  type="button"
                  className="sl-btn sl-btn-ghost sl-btn-sm"
                  onClick={() => loadDirectory(parentPath || undefined)}
                >
                  <ChevronUp size={14} /> Up
                </button>
              )}
              <code className="sl-browser-current-path">{currentBrowsePath || 'My Computer (Logical Drives)'}</code>
            </div>
            <div className="sl-browser-list">
              {loading && <div className="sl-empty-hint">Reading host directory...</div>}
              {!loading && items.length === 0 && (
                <div className="sl-empty-hint">No subdirectories found in this folder.</div>
              )}
              {!loading &&
                items.map((item) => (
                  <button
                    key={item.fullPath}
                    type="button"
                    className="sl-browser-item"
                    onClick={() => loadDirectory(item.fullPath)}
                  >
                    {item.isDrive ? <HardDrive size={15} /> : <Folder size={15} />}
                    <span className="sl-browser-item-name">{item.name}</span>
                    <span className="sl-browser-item-sub">{item.fullPath}</span>
                  </button>
                ))}
            </div>
            <div className="sl-modal-footer">
              <button type="button" className="sl-btn sl-btn-ghost" onClick={() => setBrowserOpen(false)}>
                Cancel
              </button>
              <button
                type="button"
                className="sl-btn sl-btn-primary"
                disabled={!currentBrowsePath}
                onClick={() => {
                  if (currentBrowsePath) onChange(currentBrowsePath);
                  setBrowserOpen(false);
                }}
              >
                <Check size={15} />
                <span>Use "{currentBrowsePath || '...'}"</span>
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
};
