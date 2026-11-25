import { Injectable, computed, inject, signal } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { Api, ApiError } from './api';
import { CurrentUser, LoginStep, MemberRole, MfaRecoveryCodes } from './models';

/** The signed-in user, loaded once from /api/auth/me. The session itself is an HttpOnly cookie. */
@Injectable({ providedIn: 'root' })
export class Auth {
  private readonly api = inject(Api);
  private readonly current = signal<CurrentUser | null | undefined>(undefined);
  private pending: Promise<CurrentUser | null> | null = null;

  /** undefined until the first check completes; null when signed out. */
  readonly user = this.current.asReadonly();
  readonly isPlatformAdmin = computed(() => this.current()?.isPlatformAdmin ?? false);

  /** Resolves the session once; later calls reuse the answer until sign-in or sign-out. */
  ensureLoaded(): Promise<CurrentUser | null> {
    if (this.current() !== undefined) {
      return Promise.resolve(this.current() ?? null);
    }
    this.pending ??= this.api
      .me()
      .then((user) => {
        this.current.set(user);
        return user;
      })
      .catch((e: unknown) => {
        const error = ApiError.from(e);
        if (error.status !== 401) {
          this.pending = null;
          throw error;
        }
        this.current.set(null);
        return null;
      });
    return this.pending;
  }

  /** Signs in with a password; returns the user, or the second-factor step still needed. */
  async login(email: string, password: string): Promise<CurrentUser | LoginStep> {
    await this.api.antiforgery(); // the login POST must carry the XSRF token too
    const result = await this.api.login(email, password);
    if ('next' in result) {
      return result;
    }
    this.signedIn(result);
    return result;
  }

  /** Second step of sign-in: an authenticator code or a recovery code. */
  async verifyMfa(answer: { code: string } | { recoveryCode: string }): Promise<CurrentUser> {
    const user = await this.api.verifyMfa(answer);
    this.signedIn(user);
    return user;
  }

  /** Activates a new authenticator; the response carries the recovery codes (shown once) and the session. */
  async confirmMfaEnrollment(code: string): Promise<MfaRecoveryCodes> {
    const result = await this.api.confirmMfaEnrollment(code);
    if (result.user) {
      this.signedIn(result.user);
    }
    return result;
  }

  private signedIn(user: CurrentUser): void {
    this.current.set(user);
    this.pending = null;
  }

  async logout(): Promise<void> {
    try {
      await this.api.logout();
    } finally {
      this.current.set(null);
      this.pending = null;
    }
  }

  /** Clears local state after the API reports the session has gone (401). */
  sessionExpired(): void {
    this.current.set(null);
    this.pending = null;
  }

  roleFor(merchantId: string): MemberRole | null {
    return this.current()?.memberships.find((m) => m.merchantId === merchantId)?.role ?? null;
  }

  canViewMerchant(merchantId: string): boolean {
    return this.isPlatformAdmin() || this.roleFor(merchantId) !== null;
  }

  /** Refunds from the dashboard are made by the merchant's own users (the API refuses platform admins). */
  canRefund(merchantId: string): boolean {
    return this.roleFor(merchantId) !== null;
  }

  isMerchantAdmin(merchantId: string): boolean {
    return this.isPlatformAdmin() || this.roleFor(merchantId) === 'MerchantAdmin';
  }

  /** Where to land after sign-in. */
  homeUrl(): string {
    const user = this.current();
    if (!user) {
      return '/login';
    }
    if (user.isPlatformAdmin) {
      return '/admin/merchants';
    }
    const first = user.memberships[0];
    return first ? `/merchants/${first.merchantId}` : '/no-access';
  }
}

export const signedIn: CanActivateFn = async (_route, state) => {
  const auth = inject(Auth);
  const router = inject(Router);
  const user = await auth.ensureLoaded();
  return user ? true : router.createUrlTree(['/login'], { queryParams: { returnUrl: state.url } });
};

export const platformAdmin: CanActivateFn = async () => {
  const auth = inject(Auth);
  const router = inject(Router);
  const user = await auth.ensureLoaded();
  return user?.isPlatformAdmin ? true : router.createUrlTree(['/']);
};

/** Merchant configuration pages (keys, team, webhooks): MerchantAdmins of that merchant and platform admins. */
export const merchantAdmin: CanActivateFn = async (route) => {
  const auth = inject(Auth);
  const router = inject(Router);
  await auth.ensureLoaded();
  const merchantId = route.parent?.paramMap.get('merchantId') ?? route.paramMap.get('merchantId') ?? '';
  return auth.isMerchantAdmin(merchantId) ? true : router.createUrlTree(['/no-access']);
};

/** The UI check mirrors the API's: platform admins and members of the merchant in the route. */
export const merchantMember: CanActivateFn = async (route) => {
  const auth = inject(Auth);
  const router = inject(Router);
  await auth.ensureLoaded();
  const merchantId = route.paramMap.get('merchantId') ?? '';
  return auth.canViewMerchant(merchantId) ? true : router.createUrlTree(['/no-access']);
};
