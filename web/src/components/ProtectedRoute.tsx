import { Navigate, useLocation } from 'react-router-dom';
import type { ReactNode } from 'react';
import { useAuth } from '../auth/AuthContext';
import { fa } from '../i18n/fa';

export function ProtectedRoute({ children }: { children: ReactNode }) {
  const { isAuthenticated, isInitializing, wasExplicitLogout } = useAuth();
  const location = useLocation();

  if (isInitializing) {
    return (
      <div className="app-main" role="status" aria-live="polite">
        <p>{fa.common.loading}</p>
      </div>
    );
  }

  if (!isAuthenticated) {
    return (
      <Navigate
        to="/login"
        replace
        state={wasExplicitLogout ? undefined : { from: location }}
      />
    );
  }

  return <>{children}</>;
}
