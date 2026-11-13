// @module: Foundation.UI.Web.SidebarNavRail
// @purpose: Shared collapsible left navigation rail with live badge counters, job activity indicator, and build provenance footer.
// @depends-on: react, lucide-react
// @exports: SidebarNavRail, SidebarSummaryBadgeDto

import React from 'react';
import {
  LayoutDashboard,
  Library,
  ScanSearch,
  FolderSync,
  Database,
  ShieldAlert,
  Wrench,
  Settings,
  Disc3,
  ChevronLeft,
  ChevronRight
} from 'lucide-react';

export interface SidebarSummaryBadgeDto {
  version: string;
  totalEntries: number;
  unverifiedEntries: number;
  pendingConflicts: number;
  quarantinedFiles: number;
  job?: {
    isRunning: boolean;
  };
}

interface SidebarNavRailProps<TTab extends string = string> {
  activeTab: TTab;
  onSelectTab: (tab: TTab) => void;
  collapsed: boolean;
  onToggleCollapse: () => void;
  summary: SidebarSummaryBadgeDto | null;
  appTitle?: string;
  appSubtitle?: string;
}

export const SidebarNavRail = <TTab extends string = string>({
  activeTab,
  onSelectTab,
  collapsed,
  onToggleCollapse,
  summary,
  appTitle = 'ShadowLibrary',
  appSubtitle = 'ROM Preservation Vault'
}: SidebarNavRailProps<TTab>) => {
  const navItems: {
    id: TTab;
    label: string;
    icon: React.ReactNode;
    badge?: number;
    badgeVariant?: 'info' | 'warn' | 'danger';
  }[] = [
    { id: 'overview' as TTab, label: 'Vault Overview', icon: <LayoutDashboard size={18} /> },
    {
      id: 'library' as TTab,
      label: 'Library & Explorer',
      icon: <Library size={18} />,
      badge: summary?.totalEntries || undefined,
      badgeVariant: 'info'
    },
    {
      id: 'scanner' as TTab,
      label: 'Scanner & Hashing',
      icon: <ScanSearch size={18} />,
      badge: summary?.unverifiedEntries || undefined,
      badgeVariant: 'warn'
    },
    {
      id: 'organizer' as TTab,
      label: 'Fix & Organize',
      icon: <FolderSync size={18} />,
      badge: summary?.pendingConflicts || undefined,
      badgeVariant: 'warn'
    },
    {
      id: 'dats' as TTab,
      label: 'DATs & Platforms',
      icon: <Database size={18} />
    },
    {
      id: 'quarantine' as TTab,
      label: 'Quarantine & Audit',
      icon: <ShieldAlert size={18} />,
      badge: summary?.quarantinedFiles || undefined,
      badgeVariant: 'danger'
    },
    {
      id: 'tools' as TTab,
      label: 'Patch & Conversions',
      icon: <Wrench size={18} />
    },
    {
      id: 'settings' as TTab,
      label: 'Settings & Vaults',
      icon: <Settings size={18} />
    }
  ];

  return (
    <aside className={`sl-sidebar ${collapsed ? 'collapsed' : ''}`} data-testid="sidebar-nav-rail">
      <div className="sl-brand">
        <div className="sl-brand-logo">
          <Disc3 size={22} className={summary?.job?.isRunning ? 'sl-spin-slow' : ''} />
        </div>
        {!collapsed && (
          <div className="sl-brand-text">
            <div className="sl-brand-title">{appTitle}</div>
            <div className="sl-brand-sub">{appSubtitle}</div>
          </div>
        )}
        <button
          type="button"
          className="sl-collapse-btn"
          onClick={onToggleCollapse}
          title={collapsed ? 'Expand Sidebar' : 'Collapse Sidebar'}
        >
          {collapsed ? <ChevronRight size={16} /> : <ChevronLeft size={16} />}
        </button>
      </div>

      <nav className="sl-nav-list">
        {navItems.map((item) => (
          <button
            key={item.id}
            type="button"
            data-testid={`nav-${item.id}`}
            className={`sl-nav-item ${activeTab === item.id ? 'active' : ''}`}
            onClick={() => onSelectTab(item.id)}
            title={collapsed ? item.label : undefined}
          >
            <span className="sl-nav-icon">{item.icon}</span>
            {!collapsed && <span className="sl-nav-label">{item.label}</span>}
            {!collapsed && item.badge !== undefined && item.badge > 0 && (
              <span className={`sl-nav-badge sl-badge-${item.badgeVariant || 'info'}`}>
                {item.badge.toLocaleString()}
              </span>
            )}
          </button>
        ))}
      </nav>

      {!collapsed && (
        <div className="sl-sidebar-footer">
          <div className="sl-footer-pill">
            <span className="sl-dot-online" />
            <span>v{summary?.version || '1.0.0-rc.20-hf.1'}</span>
          </div>
          <div className="sl-footer-sub">No-Intro • Redump • TOSEC</div>
        </div>
      )}
    </aside>
  );
};
