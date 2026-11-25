import { Injectable, signal } from '@angular/core';
import type { InteractionRequiredAuthError, PublicClientApplication } from '@azure/msal-browser';

interface AuthConfig {
  clientId: string;
  authority: string;
  apiScope: string;
  useStreaming?: boolean;
}

@Injectable({ providedIn: 'root' })
export class AuthService {
  readonly signedIn = signal(false);
  readonly ready = signal(false);
  readonly error = signal('');
  readonly useStreaming = signal(true);
  private client?: PublicClientApplication;
  private interactionError?: typeof InteractionRequiredAuthError;
  private scope = '';
  private sessionGeneration = 0;

  async initialize(): Promise<void> {
    try {
      const response = await fetch('/auth-config.json', { cache: 'no-store' });
      if (!response.ok) throw new Error('Authentication configuration could not be loaded.');
      const config: unknown = await response.json();
      if (!isAuthConfig(config)) throw new Error('Sign-in is not configured. Contact the administrator.');
      this.scope = config.apiScope;
      this.useStreaming.set(config.useStreaming ?? true);
      const msal = await import('@azure/msal-browser');
      this.interactionError = msal.InteractionRequiredAuthError;
      this.client = new msal.PublicClientApplication({
        auth: {
          clientId: config.clientId,
          authority: config.authority,
          redirectUri: `${window.location.origin}/auth-callback.html`,
        },
        cache: { cacheLocation: 'sessionStorage' },
      });
      await this.client.initialize();
      const accounts = this.client.getAllAccounts();
      if (accounts.length === 1) {
        this.client.setActiveAccount(accounts[0]);
        this.signedIn.set(true);
      }
      this.ready.set(true);
    } catch (error) {
      console.error('Sign-in initialization failed.', error);
      this.error.set(error instanceof Error ? error.message : 'Sign-in could not be initialized.');
    }
  }

  async signIn(): Promise<void> {
    if (!this.client) throw new Error('Sign-in is not configured.');
    this.sessionGeneration++;
    const result = await this.client.loginPopup({ scopes: [this.scope] });
    this.client.setActiveAccount(result.account);
    this.error.set('');
    this.signedIn.set(true);
  }

  async getToken(): Promise<string> {
    const account = this.client?.getActiveAccount();
    if (!this.client || !account) throw new Error('Please sign in to use chat.');
    const generation = this.sessionGeneration;
    try {
      return (await this.client.acquireTokenSilent({ account, scopes: [this.scope] })).accessToken;
    } catch (error) {
      if (this.interactionError && error instanceof this.interactionError) {
        if (generation === this.sessionGeneration) {
          this.error.set('Your session needs a new sign-in.');
          this.signedIn.set(false);
        }
        throw new Error('Your session needs a new sign-in.');
      }
      throw error;
    }
  }

  async signOut(): Promise<void> {
    if (!this.client) throw new Error('Sign-in is not configured.');
    await this.client.logoutPopup({
      account: this.client.getActiveAccount(),
      postLogoutRedirectUri: `${window.location.origin}/auth-callback.html`,
    });
    this.sessionGeneration++;
    this.client.setActiveAccount(null);
    this.signedIn.set(false);
  }
}

function isAuthConfig(value: unknown): value is AuthConfig {
  if (typeof value !== 'object' || value === null ||
      !('clientId' in value) || typeof value.clientId !== 'string' ||
      !('authority' in value) || typeof value.authority !== 'string' ||
      !('apiScope' in value) || typeof value.apiScope !== 'string') return false;
  if ('useStreaming' in value && typeof value.useStreaming !== 'boolean') return false;
  const guid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
  if (!guid.test(value.clientId) ||
      !/^api:\/\/[0-9a-f-]{36}\/access_as_user$/i.test(value.apiScope)) return false;
  try {
    const authority = new URL(value.authority);
    return authority.protocol === 'https:' && authority.hostname.endsWith('.ciamlogin.com') &&
      guid.test(authority.pathname.replace(/^\/|\/$/g, '')) &&
      !authority.username && !authority.password && !authority.search && !authority.hash;
  } catch {
    return false;
  }
}
