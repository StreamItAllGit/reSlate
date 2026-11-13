// @module: Foundation.UI.Web.UniversalDataTable
// @purpose: Standard 09 §4.6 declarative sortable data table with tabular-nums, bulk selection, and CSV/JSON export.
// @depends-on: react, lucide-react
// @exports: UniversalDataTable, UniversalColumnDef, UniversalDataTableProps

import React, { useMemo, useState } from 'react';
import { ArrowDown, ArrowUp, ArrowUpDown, Download } from 'lucide-react';

export interface UniversalColumnDef<T> {
  key: string;
  header: string;
  sortable?: boolean;
  sortValue?: (row: T) => string | number;
  render: (row: T, index: number) => React.ReactNode;
  isRowHeader?: boolean;
}

export interface UniversalDataTableProps<T extends object> {
  ariaLabel: string;
  rows: T[];
  rowKey: (row: T, index: number) => string;
  columns: UniversalColumnDef<T>[];
  maxHeight?: string;
  exportBaseFilename?: string;
  emptyMessage?: string;
  selectable?: boolean;
  selectedKeys?: Set<string>;
  onToggleSelectKey?: (key: string) => void;
  onToggleSelectAll?: (keys: string[]) => void;
}

function downloadExportFile<T extends object>(rows: T[], baseFilename: string, format: 'csv' | 'json'): void {
  if (!rows || rows.length === 0) return;
  let content = '';
  let mime = 'application/json;charset=utf-8';
  if (format === 'json') {
    content = JSON.stringify(rows, null, 2);
  } else {
    mime = 'text/csv;charset=utf-8';
    const keys = Object.keys(rows[0] as Record<string, unknown>);
    const headerLine = keys.join(',');
    const bodyLines = rows.map((r) =>
      keys
        .map((k) => {
          const v = (r as Record<string, unknown>)[k];
          const str = v === null || v === undefined ? '' : typeof v === 'object' ? JSON.stringify(v) : String(v);
          return `"${str.replace(/"/g, '""')}"`;
        })
        .join(','),
    );
    content = [headerLine, ...bodyLines].join('\r\n');
  }
  const blob = new Blob([content], { type: mime });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = `${baseFilename}.${format}`;
  document.body.appendChild(a);
  a.click();
  document.body.removeChild(a);
  URL.revokeObjectURL(url);
}

export function UniversalDataTable<T extends object>({
  ariaLabel,
  rows,
  rowKey,
  columns,
  maxHeight,
  exportBaseFilename,
  emptyMessage = 'No matching records found.',
  selectable = false,
  selectedKeys,
  onToggleSelectKey,
  onToggleSelectAll,
}: UniversalDataTableProps<T>) {
  const [sortKey, setSortKey] = useState<string | null>(null);
  const [sortAsc, setSortAsc] = useState(true);

  const sortedRows = useMemo(() => {
    if (!sortKey) return rows;
    const col = columns.find((c) => c.key === sortKey);
    if (!col) return rows;

    return [...rows].sort((a, b) => {
      const va = col.sortValue
        ? col.sortValue(a)
        : String((a as Record<string, unknown>)[sortKey] ?? '');
      const vb = col.sortValue
        ? col.sortValue(b)
        : String((b as Record<string, unknown>)[sortKey] ?? '');
      if (typeof va === 'number' && typeof vb === 'number') {
        return sortAsc ? va - vb : vb - va;
      }
      return sortAsc
        ? String(va).localeCompare(String(vb), undefined, { numeric: true })
        : String(vb).localeCompare(String(va), undefined, { numeric: true });
    });
  }, [rows, columns, sortKey, sortAsc]);

  const handleSort = (key: string) => {
    if (sortKey === key) {
      setSortAsc((prev) => !prev);
    } else {
      setSortKey(key);
      setSortAsc(true);
    }
  };

  const colVars: Record<string, string> = {};
  const offset = selectable ? 1 : 0;
  columns.forEach((c, idx) => {
    const colIdx = idx + 1 + offset;
    if (colIdx >= 2 && colIdx <= 8) {
      colVars[`--col-${colIdx}`] = JSON.stringify(c.header);
    }
  });

  const allRowKeys = useMemo(
    () => sortedRows.map((r, i) => rowKey(r, i)),
    [sortedRows, rowKey],
  );
  const allSelected =
    selectable &&
    allRowKeys.length > 0 &&
    allRowKeys.every((k) => selectedKeys?.has(k));

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: '0.45rem' }}>
      {exportBaseFilename && rows.length > 0 ? (
        <div className="sl-row" style={{ justifyContent: 'flex-end' }}>
          <button
            type="button"
            className="sl-btn sl-btn-sm"
            onClick={() => downloadExportFile(rows, exportBaseFilename, 'csv')}
          >
            <Download size={13} /> Export CSV
          </button>
          <button
            type="button"
            className="sl-btn sl-btn-sm"
            onClick={() => downloadExportFile(rows, exportBaseFilename, 'json')}
          >
            <Download size={13} /> Export JSON
          </button>
        </div>
      ) : null}

      <div
        tabIndex={0}
        role="region"
        aria-label={ariaLabel}
        className="sl-table-wrapper"
        style={maxHeight ? { maxBlockSize: maxHeight } : undefined}
      >
        <table className="sl-table" style={colVars as React.CSSProperties}>
          <thead>
            <tr>
              {selectable ? (
                <th scope="col" style={{ width: 34 }}>
                  <input
                    type="checkbox"
                    checked={Boolean(allSelected)}
                    onChange={() => onToggleSelectAll?.(allRowKeys)}
                    aria-label="Select all rows"
                  />
                </th>
              ) : null}
              {columns.map((col) => (
                <th key={col.key} scope="col">
                  {col.sortable ? (
                    <button
                      type="button"
                      className="sl-table-sort-btn"
                      onClick={() => handleSort(col.key)}
                    >
                      <span>{col.header}</span>
                      {sortKey === col.key ? (
                        sortAsc ? (
                          <ArrowUp size={12} />
                        ) : (
                          <ArrowDown size={12} />
                        )
                      ) : (
                        <ArrowUpDown size={12} style={{ opacity: 0.45 }} />
                      )}
                    </button>
                  ) : (
                    col.header
                  )}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {sortedRows.length === 0 ? (
              <tr>
                <td
                  colSpan={columns.length + (selectable ? 1 : 0)}
                  style={{ color: 'var(--sl-text-secondary)', padding: '0.85rem' }}
                >
                  {emptyMessage}
                </td>
              </tr>
            ) : (
              sortedRows.map((row: T, idx: number) => {
                const k = rowKey(row, idx);
                return (
                  <tr key={k}>
                    {selectable ? (
                      <td>
                        <input
                          type="checkbox"
                          checked={Boolean(selectedKeys?.has(k))}
                          onChange={() => onToggleSelectKey?.(k)}
                          aria-label={`Select row ${idx + 1}`}
                        />
                      </td>
                    ) : null}
                    {columns.map((col, cIdx) =>
                      col.isRowHeader || (!selectable && cIdx === 0) ? (
                        <th key={col.key} scope="row">
                          {col.render(row, idx)}
                        </th>
                      ) : (
                        <td key={col.key}>{col.render(row, idx)}</td>
                      ),
                    )}
                  </tr>
                );
              })
            )}
          </tbody>
        </table>
      </div>
    </div>
  );
}
