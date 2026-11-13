// @module: ResponsiveTable
// @purpose: Accessible sticky-header data table wrapper with @container stacked card fallback on narrow screens.
// @depends-on: react, lucide-react
// @exports: ResponsiveTable, ResponsiveTableProps

import React from 'react';

export interface ResponsiveTableProps {
  ariaLabel: string;
  headers: string[];
  maxHeight?: string;
  children: React.ReactNode;
}

export const ResponsiveTable: React.FC<ResponsiveTableProps> = ({
  ariaLabel,
  headers,
  maxHeight,
  children,
}) => {
  const colVars: Record<string, string> = {};
  headers.forEach((h, idx) => {
    if (idx >= 1 && idx <= 7) {
      colVars[`--col-${idx + 1}`] = JSON.stringify(h);
    }
  });

  return (
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
            {headers.map((h, i) => (
              <th key={`${i}-${h}`} scope="col">
                {h}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>{children}</tbody>
      </table>
    </div>
  );
};
