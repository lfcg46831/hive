import { HiveApiError } from '../../api/index.js';

/**
 * Why an action was refused, in the API's own words.
 *
 * Governance rejections are reported verbatim rather than reworded: the console
 * does not hold the rules of §4.4, so paraphrasing them would be an invention.
 * The structured codes are shown because they are what an operator can act on.
 */
export function InboxActionError({ error }: { readonly error: Error }) {
  if (!(error instanceof HiveApiError)) {
    return (
      <p className="inbox-error" role="alert">
        The action could not be sent. {error.message}
      </p>
    );
  }

  const rejections = error.emissionErrors;

  return (
    <div className="inbox-error" role="alert">
      <p className="inbox-error__title">{error.problem?.title ?? error.message}</p>
      {error.problem?.detail === undefined || error.problem.detail === null ? null : (
        <p className="inbox-error__detail">{error.problem.detail}</p>
      )}
      {rejections.length === 0 ? null : (
        <ul className="inbox-error__list">
          {rejections.map((rejection) => (
            <li key={`${rejection.code}:${rejection.path}`}>
              <code>{rejection.code}</code> at <code>{rejection.path}</code> — {rejection.reason}
            </li>
          ))}
        </ul>
      )}
      {error.isReadModelUnavailable ? (
        <p className="inbox-error__detail">
          Nothing was emitted. The request can be retried once the service is available again.
        </p>
      ) : null}
    </div>
  );
}
