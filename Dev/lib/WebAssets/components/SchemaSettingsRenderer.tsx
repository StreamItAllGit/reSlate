// @module: Foundation.UI.Web.SchemaSettingsRenderer
// @purpose: Standard 09 §4.6 declarative settings form renderer with 2-line tooltips, dirty indicator, and atomic save support.
// @depends-on: react, lucide-react, ./StyledTooltip
// @exports: SchemaSettingsRenderer, SettingFieldType, SettingFieldDescriptor, SchemaSettingsRendererProps

import React from 'react';
import { Save } from 'lucide-react';
import { StyledTooltip } from './StyledTooltip';

export type SettingFieldType = 'boolean' | 'number' | 'string' | 'select' | 'path';

export interface SettingFieldDescriptor {
  key: string;
  label: string;
  type: SettingFieldType;
  tooltipTitle: string;
  tooltipBody: string;
  hotkey?: string;
  min?: number;
  max?: number;
  placeholder?: string;
  options?: { value: string; label: string }[];
  pickFile?: boolean;
}

export interface SchemaSettingsRendererProps {
  title: string;
  subtitle?: string;
  fields: SettingFieldDescriptor[];
  values: Record<string, unknown>;
  onChange: (key: string, value: unknown) => void;
  onSave?: () => void;
  isDirty?: boolean;
  saving?: boolean;
  nativePickerAvailable?: boolean;
  renderPathField?: (
    field: SettingFieldDescriptor,
    value: string,
    onChange: (nextPath: string) => void,
    nativePickerAvailable: boolean,
  ) => React.ReactNode;
}

export const SchemaSettingsRenderer: React.FC<SchemaSettingsRendererProps> = ({
  title,
  subtitle,
  fields,
  values,
  onChange,
  onSave,
  isDirty = false,
  saving = false,
  nativePickerAvailable = false,
  renderPathField,
}) => {
  return (
    <section className="sl-card">
      <div className="sl-card-header">
        <div>
          <h3 className="sl-card-title">
            {title}
            {isDirty ? <span className="sl-badge sl-badge-warn">UNSAVED CHANGES</span> : null}
          </h3>
          {subtitle ? (
            <div style={{ fontSize: '0.8rem', color: 'var(--sl-text-secondary)' }}>{subtitle}</div>
          ) : null}
        </div>
        {onSave ? (
          <StyledTooltip
            title="Save Settings"
            hotkey="Ctrl+S"
            body="Persists modified settings atomically and rotates a rollback snapshot."
          >
            <button
              type="button"
              className="sl-btn sl-btn-sm sl-btn-primary"
              disabled={saving}
              onClick={onSave}
            >
              <Save size={14} /> {saving ? 'Saving...' : 'Save Settings'}
            </button>
          </StyledTooltip>
        ) : null}
      </div>

      <div
        style={{
          display: 'grid',
          gridTemplateColumns: 'repeat(auto-fit, minmax(260px, 1fr))',
          gap: '0.75rem',
        }}
      >
        {fields.map((field) => {
          const rawVal = values[field.key];
          if (field.type === 'boolean') {
            return (
              <StyledTooltip
                key={field.key}
                title={field.tooltipTitle}
                body={field.tooltipBody}
                hotkey={field.hotkey}
              >
                <label className="sl-row" style={{ cursor: 'pointer', fontSize: '0.84rem' }}>
                  <input
                    type="checkbox"
                    checked={Boolean(rawVal)}
                    onChange={(e) => onChange(field.key, e.target.checked)}
                  />
                  <span>{field.label}</span>
                </label>
              </StyledTooltip>
            );
          }

          if (field.type === 'path' && renderPathField) {
            return (
              <div key={field.key} style={{ gridColumn: '1 / -1' }}>
                {renderPathField(
                  field,
                  String(rawVal ?? ''),
                  (v) => onChange(field.key, v),
                  nativePickerAvailable,
                )}
              </div>
            );
          }

          return (
            <div key={field.key} className="sl-field">
              <StyledTooltip
                title={field.tooltipTitle}
                body={field.tooltipBody}
                hotkey={field.hotkey}
              >
                <span className="sl-field-label">{field.label}</span>
              </StyledTooltip>
              {field.type === 'select' ? (
                <select
                  className="sl-select sl-btn-sm"
                  value={String(rawVal ?? '')}
                  onChange={(e) => onChange(field.key, e.target.value)}
                >
                  {(field.options ?? []).map((opt) => (
                    <option key={opt.value} value={opt.value}>
                      {opt.label}
                    </option>
                  ))}
                </select>
              ) : (
                <input
                  type={field.type === 'number' ? 'number' : 'text'}
                  className="sl-input sl-btn-sm"
                  min={field.min}
                  max={field.max}
                  placeholder={field.placeholder}
                  value={String(rawVal ?? '')}
                  onChange={(e) =>
                    onChange(
                      field.key,
                      field.type === 'number' ? Number(e.target.value) : e.target.value,
                    )
                  }
                />
              )}
            </div>
          );
        })}
      </div>
    </section>
  );
};
