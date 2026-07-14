import {
  PublicClientApplication,
  InteractionRequiredAuthError,
  type AccountInfo,
} from "@azure/msal-browser";

// ─── Local (dev) token storage ────────────────────────────────────────────────

const TOKEN_KEY = "migration_platform_token";
const EXPIRY_KEY = "migration_platform_token_expiry";

export function getToken(): string | null {
  if (typeof window === "undefined") return null;
  return localStorage.getItem(TOKEN_KEY);
}

export function setToken(token: string, expiresAt: string): void {
  localStorage.setItem(TOKEN_KEY, token);
  localStorage.setItem(EXPIRY_KEY, expiresAt);
}

export function clearToken(): void {
  localStorage.removeItem(TOKEN_KEY);
  localStorage.removeItem(EXPIRY_KEY);
}

/** Synchronous check for the LOCAL (dev) token only. Prefer ensureAuthenticated(). */
export function isAuthenticated(): boolean {
  if (typeof window === "undefined") return false;
  const token = localStorage.getItem(TOKEN_KEY);
  if (!token) return false;
  const expiry = localStorage.getItem(EXPIRY_KEY);
  if (!expiry) return false;
  return new Date(expiry) > new Date();
}

// ─── Auth mode configuration (served by the API) ─────────────────────────────

export type AuthMode = "entraId" | "local" | "none";

export interface AuthConfig {
  mode: AuthMode;
  tenantId: string | null;
  clientId: string | null;
  apiScope: string | null;
}

const BASE_URL = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5000/api";

let configPromise: Promise<AuthConfig> | null = null;

/**
 * Fetch (once) the API's authentication configuration. Falls back to local
 * dev-token mode when the endpoint is unreachable or predates the contract,
 * so an older backend keeps working unchanged.
 */
export function getAuthConfig(): Promise<AuthConfig> {
  configPromise ??= (async (): Promise<AuthConfig> => {
    try {
      const res = await fetch(`${BASE_URL}/auth/config`);
      if (!res.ok) throw new Error(`auth/config ${res.status}`);
      const cfg = (await res.json()) as AuthConfig;
      if (cfg.mode === "entraId" && (!cfg.tenantId || !cfg.clientId)) {
        // Misconfigured server — treat as unconfigured rather than crashing MSAL.
        return { mode: "none", tenantId: null, clientId: null, apiScope: null };
      }
      return cfg;
    } catch {
      return { mode: "local", tenantId: null, clientId: null, apiScope: null };
    }
  })();
  return configPromise;
}

// ─── MSAL (Entra ID mode) ────────────────────────────────────────────────────

let msalPromise: Promise<PublicClientApplication> | null = null;

async function getMsal(cfg: AuthConfig): Promise<PublicClientApplication> {
  msalPromise ??= (async () => {
    const app = new PublicClientApplication({
      auth: {
        clientId: cfg.clientId!,
        authority: `https://login.microsoftonline.com/${cfg.tenantId}`,
        redirectUri: window.location.origin,
        postLogoutRedirectUri: `${window.location.origin}/login`,
      },
      cache: {
        // localStorage so the session survives tab reloads, matching the
        // behavior of the dev-token flow.
        cacheLocation: "localStorage",
      },
    });
    await app.initialize();
    return app;
  })();
  return msalPromise;
}

function scopesFor(cfg: AuthConfig): string[] {
  // The API scope authorizes calls to the backend; fall back to the client-id
  // default scope if the operator didn't configure an explicit one.
  return cfg.apiScope ? [cfg.apiScope] : [`api://${cfg.clientId}/.default`];
}

function activeAccount(app: PublicClientApplication): AccountInfo | null {
  return app.getActiveAccount() ?? app.getAllAccounts()[0] ?? null;
}

/** Interactive Microsoft sign-in (Entra ID mode only). Throws on failure/cancel. */
export async function msalLogin(): Promise<void> {
  const cfg = await getAuthConfig();
  if (cfg.mode !== "entraId") throw new Error("Entra ID sign-in is not enabled.");
  const app = await getMsal(cfg);
  const result = await app.loginPopup({ scopes: scopesFor(cfg), prompt: "select_account" });
  app.setActiveAccount(result.account);
}

/**
 * Access token for API calls in whichever mode is active. Entra ID: silent
 * acquisition with popup fallback (MSAL handles renewal); local: the stored
 * dev token. Null when unauthenticated.
 */
export async function getAccessToken(): Promise<string | null> {
  if (typeof window === "undefined") return null;
  const cfg = await getAuthConfig();

  if (cfg.mode !== "entraId") return getToken();

  const app = await getMsal(cfg);
  const account = activeAccount(app);
  if (!account) return null;

  const request = { scopes: scopesFor(cfg), account };
  try {
    const result = await app.acquireTokenSilent(request);
    return result.accessToken;
  } catch (err) {
    if (err instanceof InteractionRequiredAuthError) {
      try {
        const result = await app.acquireTokenPopup(request);
        app.setActiveAccount(result.account);
        return result.accessToken;
      } catch {
        return null;
      }
    }
    return null;
  }
}

/** Mode-aware authentication check (async because Entra mode needs MSAL init). */
export async function ensureAuthenticated(): Promise<boolean> {
  if (typeof window === "undefined") return false;
  const cfg = await getAuthConfig();
  switch (cfg.mode) {
    case "local":
      return isAuthenticated();
    case "entraId": {
      const app = await getMsal(cfg);
      return activeAccount(app) !== null;
    }
    default:
      return false;
  }
}

/** Display identity of the signed-in user, or null when unauthenticated. */
export async function getDisplayUser(): Promise<string | null> {
  const cfg = await getAuthConfig();
  if (cfg.mode === "entraId") {
    const app = await getMsal(cfg);
    const account = activeAccount(app);
    return account?.username ?? null;
  }
  return isAuthenticated() ? "admin (dev)" : null;
}

/** Sign out of the active mode and land on /login. */
export async function signOut(): Promise<void> {
  const cfg = await getAuthConfig();
  if (cfg.mode === "entraId") {
    const app = await getMsal(cfg);
    const account = activeAccount(app);
    try {
      await app.logoutPopup({ account: account ?? undefined });
    } catch {
      // Popup blocked/closed — fall back to clearing the local MSAL cache so
      // the app-side session ends even if the Microsoft session persists.
      await app.clearCache();
    }
  }
  clearToken();
  window.location.href = "/login";
}

/**
 * Invoked by the API layer on a 401: drop whatever session state the active
 * mode holds and return to /login (which re-triggers sign-in).
 */
export async function handleUnauthorized(): Promise<void> {
  const cfg = await getAuthConfig();
  if (cfg.mode === "entraId") {
    try {
      const app = await getMsal(cfg);
      await app.clearCache();
    } catch {
      // MSAL not initializable — nothing to clear.
    }
  }
  clearToken();
  window.location.href = "/login";
}
