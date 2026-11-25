import { Link, useLocation } from 'react-router-dom';
import type { ReactNode } from 'react';
import { useAuth } from '../auth/AuthContext';
import { fa } from '../i18n/fa';
import { ChatIcon, FolderIcon, LogOutIcon } from './Icons';

export function Layout({ children }: { children: ReactNode }) {
  const { user, logout } = useAuth();
  const location = useLocation();
  const isAdmin = user?.roles.includes('Admin') ?? false;

  return (
    <div className="app-shell">
      <a className="skip-link" href="#main-content">
        {fa.nav.skipToContent}
      </a>

      <header className="app-header">
        <div className="app-header-inner">
          <div className="brand-group">
            <Link to="/collections" className="brand-link">
              <svg width="22" height="22" viewBox="0 0 24 24" fill="none" aria-hidden="true">
                <rect x="4" y="3" width="14" height="18" rx="2" stroke="currentColor" strokeWidth="2" />
                <path d="M8 8h6M8 12h6M8 16h4" stroke="currentColor" strokeWidth="2" strokeLinecap="round" />
              </svg>
              <span>{fa.appName}</span>
            </Link>

            <nav aria-label={fa.appName} className="nav-links">
              <Link
                to="/collections"
                className="nav-link"
                aria-current={location.pathname.startsWith('/collections') ? 'page' : undefined}
              >
                <FolderIcon />
                <span>{fa.nav.collections}</span>
              </Link>
              <Link
                to="/chat"
                className="nav-link"
                aria-current={location.pathname.startsWith('/chat') ? 'page' : undefined}
              >
                <ChatIcon />
                <span>{fa.nav.chat}</span>
              </Link>
            </nav>
          </div>

          {user && (
            <div className="header-user">
              <span className="sr-only">{fa.nav.signedInAs}</span>
              <bdi className="user-email" title={user.email}>
                {user.email}
              </bdi>
              {isAdmin && <span className="badge badge-role">{fa.nav.adminBadge}</span>}
              <button type="button" className="btn btn-secondary btn-sm" onClick={logout}>
                <LogOutIcon />
                <span>{fa.nav.logout}</span>
              </button>
            </div>
          )}
        </div>
      </header>

      <main id="main-content" className="app-main" tabIndex={-1}>
        {children}
      </main>
    </div>
  );
}
