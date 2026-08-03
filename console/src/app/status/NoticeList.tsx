import type { ConsoleNotice } from './consoleStatus.js';

export interface NoticeListProps {
  readonly notices: readonly ConsoleNotice[];
  onRetry(): void;
}

/**
 * Degraded-mode banners: reconnection, polling fallback, a registry version
 * being replaced, data that may have gone out of date and a failed update
 * attempt. They sit above the organogram rather than replacing it, because the
 * last known snapshot is still the most useful thing on screen — as long as the
 * reader is told what it is.
 */
export function NoticeList({ notices, onRetry }: NoticeListProps) {
  if (notices.length === 0) {
    return null;
  }

  return (
    <ul className="notices" aria-label="Console status notices">
      {notices.map((notice) => (
        <li
          key={notice.id}
          className={`notice notice--${notice.severity}`}
          data-notice={notice.id}
          role={notice.severity === 'warning' ? 'alert' : 'status'}
        >
          <span className="notice__message">{notice.message}</span>
          {notice.retryable ? (
            <button className="notice__action" type="button" onClick={onRetry}>
              Refresh now
            </button>
          ) : null}
        </li>
      ))}
    </ul>
  );
}
