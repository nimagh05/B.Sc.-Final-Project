export interface StoredSession {
  accessToken: string;
  expiresAtUtc: string;
  roles: string[];
}

const SESSION_STORAGE_KEY = 'docsearch_auth_session';

let memorySession: StoredSession | null = null;
let expiredSessionDetected = false;

function isExpired(expiresAtUtc: string): boolean {
  const expiresMs = Date.parse(expiresAtUtc);
  if (Number.isNaN(expiresMs)) {
    return false;
  }
  return expiresMs <= Date.now();
}

function clearStorageOnly(): void {
  memorySession = null;
  try {
    if (typeof window !== 'undefined' && window.sessionStorage) {
      window.sessionStorage.removeItem(SESSION_STORAGE_KEY);
    }
  } catch {
    // Ignore storage errors
  }
}

function readFromSessionStorage(): StoredSession | null {
  try {
    if (typeof window === 'undefined' || !window.sessionStorage) {
      return null;
    }
    const raw = window.sessionStorage.getItem(SESSION_STORAGE_KEY);
    if (!raw) {
      return null;
    }
    const parsed = JSON.parse(raw) as Partial<StoredSession>;
    if (
      typeof parsed.accessToken === 'string' &&
      parsed.accessToken.length > 0 &&
      typeof parsed.expiresAtUtc === 'string'
    ) {
      if (isExpired(parsed.expiresAtUtc)) {
        expiredSessionDetected = true;
        window.sessionStorage.removeItem(SESSION_STORAGE_KEY);
        return null;
      }
      return {
        accessToken: parsed.accessToken,
        expiresAtUtc: parsed.expiresAtUtc,
        roles: Array.isArray(parsed.roles) ? parsed.roles.filter((r): r is string => typeof r === 'string') : [],
      };
    }
  } catch {
    // Ignore unavailable or corrupted sessionStorage
  }
  return null;
}

export const tokenStorage = {
  getSession(): StoredSession | null {
    if (memorySession) {
      if (isExpired(memorySession.expiresAtUtc)) {
        expiredSessionDetected = true;
        clearStorageOnly();
        return null;
      }
      return memorySession;
    }

    const fromStorage = readFromSessionStorage();
    if (fromStorage) {
      memorySession = fromStorage;
    }
    return fromStorage;
  },

  getAccessToken(): string | null {
    return this.getSession()?.accessToken ?? null;
  },

  consumeExpiredNotice(): boolean {
    const wasExpired = expiredSessionDetected;
    expiredSessionDetected = false;
    return wasExpired;
  },

  setSession(session: StoredSession): void {
    expiredSessionDetected = false;
    memorySession = session;
    try {
      if (typeof window !== 'undefined' && window.sessionStorage) {
        window.sessionStorage.setItem(SESSION_STORAGE_KEY, JSON.stringify(session));
      }
    } catch {
      // Memory token still works if sessionStorage is restricted
    }
  },

  clear(): void {
    expiredSessionDetected = false;
    clearStorageOnly();
  },
};
