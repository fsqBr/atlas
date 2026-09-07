// OIDC sign-in for the SPA (authorization code + PKCE via oidc-client-ts). Disabled unless the API says so.
import { User, UserManager, WebStorageStateStore } from "oidc-client-ts";

export interface AuthConfig {
  enabled: boolean;
  authority: string | null;
  clientId: string | null;
  scopes: string | null;
}

export interface AuthUser {
  name: string;
  email: string | null;
}

const CALLBACK_PATH = "/auth/callback";
const RETURN_KEY = "atlas.auth.returnTo";

let manager: UserManager | null = null;
let current: User | null = null;
let enabled = false;

export function isAuthEnabled(): boolean {
  return enabled;
}

export function getAccessToken(): string | null {
  return current && !current.expired ? current.access_token : null;
}

export function currentUser(): AuthUser | null {
  if (!current) return null;
  const p = current.profile;
  return { name: (p.name as string) ?? (p.preferred_username as string) ?? (p.email as string) ?? "user", email: (p.email as string) ?? null };
}

export async function signIn(): Promise<void> {
  if (!manager) return;
  try {
    sessionStorage.setItem(RETURN_KEY, window.location.pathname + window.location.search);
  } catch {
    /* storage unavailable */
  }
  await manager.signinRedirect();
}

export async function signOut(): Promise<void> {
  if (!manager) return;
  current = null;
  await manager.signoutRedirect();
}

/**
 * Loads the auth config, completes a callback if this is the redirect landing, and
 * returns whether the app may render. When sign-in is required the browser is
 * redirected and `false` is returned so nothing renders in between.
 */
export async function initAuth(): Promise<boolean> {
  let config: AuthConfig = { enabled: false, authority: null, clientId: null, scopes: null };
  try {
    const response = await fetch("/api/auth/config", { headers: { Accept: "application/json" } });
    if (response.ok) config = (await response.json()) as AuthConfig;
  } catch {
    /* API unreachable: render and let pages show the error */
  }

  enabled = config.enabled && !!config.authority && !!config.clientId;
  if (!enabled) return true;

  manager = new UserManager({
    authority: config.authority!,
    client_id: config.clientId!,
    redirect_uri: window.location.origin + CALLBACK_PATH,
    post_logout_redirect_uri: window.location.origin,
    response_type: "code",
    scope: config.scopes ?? "openid profile email",
    userStore: new WebStorageStateStore({ store: window.sessionStorage }),
    automaticSilentRenew: true,
  });
  manager.events.addUserLoaded((user) => {
    current = user;
  });
  manager.events.addUserUnloaded(() => {
    current = null;
  });
  manager.events.addAccessTokenExpired(() => {
    void signIn();
  });

  if (window.location.pathname === CALLBACK_PATH) {
    try {
      current = await manager.signinRedirectCallback();
    } catch (err) {
      console.error("OIDC callback failed", err);
    }
    let returnTo = "/";
    try {
      returnTo = sessionStorage.getItem(RETURN_KEY) || "/";
      sessionStorage.removeItem(RETURN_KEY);
    } catch {
      /* ignore */
    }
    window.history.replaceState({}, "", returnTo);
  } else {
    current = await manager.getUser();
  }

  if (!current || current.expired) {
    await signIn();
    return false;
  }

  return true;
}
