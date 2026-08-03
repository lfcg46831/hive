import type { ConsoleFailure } from './consoleStatus.js';

/**
 * The three things the console can show instead of an organogram: it is still
 * loading one, it could not get one, or the organization it was asked about has
 * nothing in it. Each is stated in its own words, because the operator reaction
 * differs: wait, fix the deployment or credential, fix the registry.
 */

export function LoadingPanel() {
  return (
    <div className="panel" role="status" aria-live="polite" aria-busy="true">
      <p className="panel__title">Loading the organogram…</p>
      <p className="panel__detail">Fetching the current registry snapshot from the public API.</p>
      <ul className="skeleton" aria-hidden="true">
        <li className="skeleton__row" />
        <li className="skeleton__row" />
        <li className="skeleton__row" />
      </ul>
    </div>
  );
}

export interface FailurePanelProps {
  readonly failure: ConsoleFailure;
  onRetry(): void;
}

export function FailurePanel({ failure, onRetry }: FailurePanelProps) {
  return (
    <div className="panel panel--error" role="alert">
      <p className="panel__title">{failure.title}</p>
      <p className="panel__detail">{failure.detail}</p>
      {failure.retryable ? (
        // Retrying is a re-read, never a write: the console has no mutating
        // affordance anywhere, and this one only refetches the snapshot.
        <button className="panel__action" type="button" onClick={onRetry}>
          Try again
        </button>
      ) : null}
    </div>
  );
}

export interface EmptyPanelProps {
  readonly organizationId: string;
  readonly registryVersion: number | null;
}

export function EmptyPanel({ organizationId, registryVersion }: EmptyPanelProps) {
  return (
    <div className="panel" role="status">
      <p className="panel__title">This organization has no units or positions</p>
      <p className="panel__detail">
        The API answered for <code>{organizationId}</code>
        {registryVersion === null ? '' : ` at registry v${registryVersion}`}, and the snapshot is
        empty. Nothing is being hidden by a filter or by a failed request.
      </p>
    </div>
  );
}
