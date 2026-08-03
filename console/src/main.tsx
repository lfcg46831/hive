import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { ConsoleApp } from './app/ConsoleApp.js';
import './app/styles.css';
import { ConsoleConfigError, readInjectedConsoleConfig } from './config.js';

const container = document.getElementById('root');
if (container === null) {
  throw new Error('The console host page is missing its #root element.');
}

const root = createRoot(container);

try {
  root.render(
    <StrictMode>
      <ConsoleApp config={readInjectedConsoleConfig()} />
    </StrictMode>,
  );
} catch (cause) {
  // A misconfigured console must say so rather than render an empty
  // organization that reads like an organization with nothing in it.
  const message =
    cause instanceof ConsoleConfigError ? cause.message : 'The console failed to start.';
  root.render(
    <main className="console">
      <p className="console__status console__status--error" role="alert">
        {message}
      </p>
    </main>,
  );
}
