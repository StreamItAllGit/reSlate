// @module: ModalDialog
// @purpose: Accessible native HTML <dialog> modal wrapper and ConfirmActionModal with top-layer backdrop, Escape dismissal, and light-dismiss support.
// @depends-on: react, lucide-react
// @exports: ModalDialog, ModalDialogProps, ConfirmActionModal, ConfirmActionModalProps

import React, { useEffect, useId, useRef } from 'react';

export interface ModalDialogProps {
  open: boolean;
  title: string;
  wide?: boolean;
  onClose: () => void;
  children: React.ReactNode;
}

export interface ConfirmActionModalProps {
  open: boolean;
  title: string;
  message: string;
  confirmLabel?: string;
  cancelLabel?: string;
  onConfirm: () => void;
  onCancel: () => void;
}

export const ConfirmActionModal: React.FC<ConfirmActionModalProps> = ({
  open,
  title,
  message,
  confirmLabel = 'Confirm Stop',
  cancelLabel = 'Keep Running',
  onConfirm,
  onCancel,
}) => {
  const dialogRef = useRef<HTMLDialogElement | null>(null);
  const titleId = useId();
  const descId = useId();

  useEffect(() => {
    const dialog = dialogRef.current;
    if (!dialog) return;
    if (open && !dialog.open) {
      dialog.showModal();
    } else if (!open && dialog.open) {
      dialog.close();
    }
  }, [open]);

  return (
    <dialog
      ref={dialogRef}
      className="sl-dialog sl-login-gate"
      aria-labelledby={titleId}
      aria-describedby={descId}
      onClose={onCancel}
      {...({ closedby: 'any' } as Record<string, string>)}
    >
      <div className="sl-dialog-inner">
        <div className="sl-card-header">
          <h2 id={titleId} className="sl-card-title">
            {title}
          </h2>
        </div>
        <p id={descId} style={{ margin: 0, color: 'var(--sl-text-secondary)', fontSize: '0.88rem', lineHeight: 1.5 }}>
          {message}
        </p>
        <div className="sl-row" style={{ justifyContent: 'flex-end', marginTop: '0.35rem' }}>
          <button
            type="button"
            className="sl-btn sl-btn-sm"
            onClick={() => dialogRef.current?.close()}
          >
            {cancelLabel}
          </button>
          <button
            type="button"
            className="sl-btn sl-btn-danger sl-btn-sm"
            onClick={() => {
              onConfirm();
              dialogRef.current?.close();
            }}
          >
            {confirmLabel}
          </button>
        </div>
      </div>
    </dialog>
  );
};

export const ModalDialog: React.FC<ModalDialogProps> = ({
  open,
  title,
  wide = false,
  onClose,
  children,
}) => {
  const dialogRef = useRef<HTMLDialogElement | null>(null);
  const titleId = useId();

  useEffect(() => {
    const dialog = dialogRef.current;
    if (!dialog) return;

    if (open && !dialog.open) {
      dialog.showModal();
    } else if (!open && dialog.open) {
      dialog.close();
    }
  }, [open]);

  useEffect(() => {
    const dialog = dialogRef.current;
    if (!dialog) return;
    if ('closedBy' in HTMLDialogElement.prototype) return;

    const handleClick = (event: MouseEvent) => {
      if (event.target === dialog) {
        const rect = dialog.getBoundingClientRect();
        const clickedInDialog =
          event.clientX >= rect.left &&
          event.clientX <= rect.right &&
          event.clientY >= rect.top &&
          event.clientY <= rect.bottom;
        if (!clickedInDialog) {
          dialog.close();
        }
      }
    };

    dialog.addEventListener('click', handleClick);
    return () => dialog.removeEventListener('click', handleClick);
  }, []);

  return (
    <dialog
      ref={dialogRef}
      className={`sl-dialog${wide ? ' sl-dialog-wide' : ''}`}
      aria-labelledby={titleId}
      onClose={onClose}
      {...({ closedby: 'any' } as Record<string, string>)}
    >
      <div className="sl-dialog-inner">
        <div className="sl-card-header">
          <h2 id={titleId} className="sl-card-title">
            {title}
          </h2>
          <button
            type="button"
            className="sl-btn sl-btn-sm"
            onClick={() => dialogRef.current?.close()}
            aria-label="Close dialog"
          >
            Close Modal (Esc)
          </button>
        </div>
        {open ? children : null}
      </div>
    </dialog>
  );
};
