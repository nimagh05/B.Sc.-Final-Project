import { useEffect, useId, useState, type FormEvent } from 'react';
import { useLocation, useNavigate } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';
import { ErrorAlert } from '../components/ErrorAlert';
import { fa } from '../i18n/fa';

type AuthMode = 'login' | 'register';

function meetsPasswordPolicy(password: string): boolean {
  return (
    password.length >= 10 &&
    /[A-Z]/.test(password) &&
    /[a-z]/.test(password) &&
    /[0-9]/.test(password)
  );
}

export function LoginPage() {
  const { isAuthenticated, isInitializing, sessionExpiredNotice, clearSessionNotice, login, register } =
    useAuth();
  const navigate = useNavigate();
  const location = useLocation();

  const [mode, setMode] = useState<AuthMode>('login');
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [displayName, setDisplayName] = useState('');
  const [fieldErrors, setFieldErrors] = useState<{ email?: string; password?: string }>({});
  const [apiError, setApiError] = useState<unknown>(null);
  const [successMessage, setSuccessMessage] = useState<string | null>(null);
  const [isSubmitting, setIsSubmitting] = useState(false);

  const emailId = useId();
  const passwordId = useId();
  const displayNameId = useId();

  const state = location.state as { from?: { pathname?: string; search?: string } } | null;
  const targetPath = state?.from?.pathname
    ? `${state.from.pathname}${state.from.search ?? ''}`
    : '/collections';

  useEffect(() => {
    if (!isInitializing && isAuthenticated) {
      navigate(targetPath, { replace: true });
    }
  }, [isInitializing, isAuthenticated, navigate, targetPath]);

  const switchMode = (nextMode: AuthMode) => {
    setMode(nextMode);
    setFieldErrors({});
    setApiError(null);
    setSuccessMessage(null);
  };

  const handleSubmit = async (event: FormEvent) => {
    event.preventDefault();
    setApiError(null);
    setSuccessMessage(null);
    clearSessionNotice();

    const trimmedEmail = email.trim();
    const errors: { email?: string; password?: string } = {};

    if (!trimmedEmail || !trimmedEmail.includes('@')) {
      errors.email = fa.auth.emailRequired;
    }

    if (!password) {
      errors.password = fa.auth.passwordRequired;
    } else if (mode === 'register' && !meetsPasswordPolicy(password)) {
      errors.password = fa.auth.passwordInvalidPolicy;
    }

    if (errors.email || errors.password) {
      setFieldErrors(errors);
      return;
    }

    setFieldErrors({});
    setIsSubmitting(true);

    try {
      if (mode === 'login') {
        await login(trimmedEmail, password);
        navigate(targetPath, { replace: true });
      } else {
        await register({
          email: trimmedEmail,
          password,
          displayName: displayName.trim() || null,
        });
        setPassword('');
        setMode('login');
        setSuccessMessage(fa.auth.registerSuccess);
      }
    } catch (err) {
      setApiError(err);
    } finally {
      setIsSubmitting(false);
    }
  };

  return (
    <div className="auth-wrapper">
      <main className="card auth-card">
        <h1 className="page-title">{fa.appName}</h1>
        <p className="page-subtitle">{fa.auth.subtitle}</p>

        <div className="auth-tabs" role="tablist" aria-label={fa.auth.loginTitle}>
          <button
            type="button"
            role="tab"
            className="auth-tab"
            aria-selected={mode === 'login'}
            onClick={() => switchMode('login')}
          >
            {fa.auth.loginTab}
          </button>
          <button
            type="button"
            role="tab"
            className="auth-tab"
            aria-selected={mode === 'register'}
            onClick={() => switchMode('register')}
          >
            {fa.auth.registerTab}
          </button>
        </div>

        <div className="form-stack">
          {sessionExpiredNotice && (
            <div className="alert alert-warning" role="alert">
              <p className="alert-detail">{fa.auth.sessionExpired}</p>
            </div>
          )}

          {successMessage && (
            <div className="alert alert-success" role="status">
              <p className="alert-detail">{successMessage}</p>
            </div>
          )}

          {apiError !== null && <ErrorAlert error={apiError} />}

          <form className="form-stack" onSubmit={handleSubmit} noValidate>
            <div className="field">
              <label className="field-label" htmlFor={emailId}>
                {fa.auth.emailLabel}
              </label>
              <input
                id={emailId}
                type="email"
                dir="ltr"
                autoComplete="email"
                className="input"
                placeholder={fa.auth.emailPlaceholder}
                value={email}
                onChange={(e) => setEmail(e.target.value)}
                aria-invalid={Boolean(fieldErrors.email)}
              />
              {fieldErrors.email && (
                <span className="field-error" role="alert">
                  {fieldErrors.email}
                </span>
              )}
            </div>

            {mode === 'register' && (
              <div className="field">
                <label className="field-label" htmlFor={displayNameId}>
                  {fa.auth.displayNameLabel}
                </label>
                <input
                  id={displayNameId}
                  type="text"
                  autoComplete="name"
                  className="input"
                  placeholder={fa.auth.displayNamePlaceholder}
                  value={displayName}
                  onChange={(e) => setDisplayName(e.target.value)}
                />
              </div>
            )}

            <div className="field">
              <label className="field-label" htmlFor={passwordId}>
                {fa.auth.passwordLabel}
              </label>
              <input
                id={passwordId}
                type="password"
                dir="ltr"
                autoComplete={mode === 'login' ? 'current-password' : 'new-password'}
                className="input"
                placeholder={fa.auth.passwordPlaceholder}
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                aria-invalid={Boolean(fieldErrors.password)}
              />
              {mode === 'register' && (
                <span className="field-hint">{fa.auth.passwordPolicyHint}</span>
              )}
              {fieldErrors.password && (
                <span className="field-error" role="alert">
                  {fieldErrors.password}
                </span>
              )}
            </div>

            <button type="submit" className="btn btn-primary" disabled={isSubmitting}>
              {isSubmitting
                ? mode === 'login'
                  ? fa.auth.loggingIn
                  : fa.auth.registering
                : mode === 'login'
                  ? fa.auth.loginButton
                  : fa.auth.registerButton}
            </button>
          </form>
        </div>
      </main>
    </div>
  );
}
