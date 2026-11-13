// @module: StyledTooltip
// @purpose: Standard 09 Â§5.6 2-line styled tooltip with bold Title Case header, optional hotkey chip, Sentence-case body, and 250ms hover delay.
// @depends-on: react, lucide-react
// @exports: StyledTooltip, StyledTooltipProps

import React, { useCallback, useEffect, useId, useRef, useState } from 'react';

export interface StyledTooltipProps {
  title: string;
  body?: string;
  hotkey?: string;
  delayMs?: number;
  children: React.ReactNode;
}

export const StyledTooltip: React.FC<StyledTooltipProps> = ({
  title,
  body,
  hotkey,
  delayMs = 250,
  children,
}) => {
  const tooltipId = useId();
  const triggerRef = useRef<HTMLSpanElement | null>(null);
  const cardRef = useRef<HTMLDivElement | null>(null);
  const timerRef = useRef<number | null>(null);

  const [visible, setVisible] = useState(false);
  const [coords, setCoords] = useState<{ top: number; left: number }>({ top: -9999, left: -9999 });

  const clearTimer = () => {
    if (timerRef.current !== null) {
      window.clearTimeout(timerRef.current);
      timerRef.current = null;
    }
  };

  const updateCoords = useCallback(() => {
    const el = triggerRef.current;
    if (!el) return;
    const rect = el.getBoundingClientRect();
    const width = cardRef.current?.offsetWidth || 260;
    const height = cardRef.current?.offsetHeight || 52;
    const margin = 8;

    let left = rect.left + rect.width / 2 - width / 2;
    if (left < margin) left = margin;
    if (left + width > window.innerWidth - margin) {
      left = Math.max(margin, window.innerWidth - width - margin);
    }

    let top = rect.bottom + 8;
    if (top + height > window.innerHeight - margin && rect.top - height - 8 >= margin) {
      top = rect.top - height - 8;
    }

    setCoords({ top: Math.round(top), left: Math.round(left) });
  }, []);

  const handleShow = () => {
    clearTimer();
    timerRef.current = window.setTimeout(() => {
      setVisible(true);
    }, delayMs);
  };

  const handleHide = useCallback(() => {
    clearTimer();
    setVisible(false);
  }, []);

  useEffect(() => {
    if (!visible) return;
    updateCoords();
    const raf = window.requestAnimationFrame(updateCoords);
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Escape') handleHide();
    };
    window.addEventListener('keydown', onKeyDown, true);
    window.addEventListener('scroll', handleHide, true);
    window.addEventListener('resize', handleHide);
    return () => {
      window.cancelAnimationFrame(raf);
      window.removeEventListener('keydown', onKeyDown, true);
      window.removeEventListener('scroll', handleHide, true);
      window.removeEventListener('resize', handleHide);
    };
  }, [visible, updateCoords, handleHide]);

  useEffect(() => () => clearTimer(), []);

  return (
    <span
      ref={triggerRef}
      className="sl-tooltip-trigger"
      aria-describedby={visible ? tooltipId : undefined}
      onMouseEnter={handleShow}
      onMouseLeave={handleHide}
      onFocus={handleShow}
      onBlur={handleHide}
      onClick={handleHide}
    >
      {children}
      {visible ? (
        <div
          ref={cardRef}
          id={tooltipId}
          role="tooltip"
          className="sl-tooltip-card"
          style={{ top: `${coords.top}px`, left: `${coords.left}px` }}
        >
          <div className="sl-tooltip-line1">
            <span>{title}</span>
            {hotkey ? <span className="sl-tooltip-hotkey">[{hotkey}]</span> : null}
          </div>
          {body ? <div className="sl-tooltip-line2">{body}</div> : null}
        </div>
      ) : null}
    </span>
  );
};
