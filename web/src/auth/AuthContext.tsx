import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { setUnauthorizedHandler } from '../api/client';
import { authApi } from '../api/endpoints';
import type { CurrentUserResponse, RegisterRequest } from '../api/types';
import { tokenStorage } from './tokenStorage';

interface AuthContextValue {
  user: CurrentUserResponse | null;
  isAuthenticated: boolean;
  isInitializing: boolean;
  sessionExpiredNotice: boolean;
  clearSessionNotice: () => void;
  login: (email: string, password: string) => Promise<void>;
  register: (payload: RegisterRequest) => Promise<void>;
  logout: () => void;
}

const AuthContext = createContext<AuthContextValue | null>(null);

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<CurrentUserResponse | null>(null);
  const [isInitializing, setIsInitializing] = useState<boolean>(() => tokenStorage.getAccessToken() !== null);
  const [sessionExpiredNotice, setSessionExpiredNotice] = useState(false);

  const handleUnauthorized = useCallback(() => {
    tokenStorage.clear();
    setUser(null);
    setSessionExpiredNotice(true);
  }, []);

  useEffect(() => {
    setUnauthorizedHandler(handleUnauthorized);
    return () => setUnauthorizedHandler(null);
  }, [handleUnauthorized]);

  useEffect(() => {
    const existingToken = tokenStorage.getAccessToken();
    if (!existingToken) {
      setIsInitializing(false);
      return;
    }

    const controller = new AbortController();
    let active = true;

    authApi
      .me(controller.signal)
      .then((me) => {
        if (active) {
          setUser(me);
        }
      })
      .catch(() => {
        if (active) {
          tokenStorage.clear();
          setUser(null);
        }
      })
      .finally(() => {
        if (active) {
          setIsInitializing(false);
        }
      });

    return () => {
      active = false;
      controller.abort();
    };
  }, []);

  const login = useCallback(async (email: string, password: string) => {
    const response = await authApi.login({ email, password });
    tokenStorage.setSession({
      accessToken: response.accessToken,
      expiresAtUtc: response.expiresAtUtc,
      roles: response.roles,
    });
    setSessionExpiredNotice(false);

    try {
      const me = await authApi.me();
      setUser(me);
    } catch {
      setUser({
        id: '',
        email,
        roles: response.roles,
      });
    }
  }, []);

  const register = useCallback(async (payload: RegisterRequest) => {
    await authApi.register(payload);
  }, []);

  const logout = useCallback(() => {
    tokenStorage.clear();
    setUser(null);
    setSessionExpiredNotice(false);
  }, []);

  const clearSessionNotice = useCallback(() => {
    setSessionExpiredNotice(false);
  }, []);

  const value = useMemo<AuthContextValue>(
    () => ({
      user,
      isAuthenticated: user !== null,
      isInitializing,
      sessionExpiredNotice,
      clearSessionNotice,
      login,
      register,
      logout,
    }),
    [user, isInitializing, sessionExpiredNotice, clearSessionNotice, login, register, logout],
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthContextValue {
  const ctx = useContext(AuthContext);
  if (!ctx) {
    throw new Error('useAuth must be used inside AuthProvider');
  }
  return ctx;
}
