import { BreakpointObserver } from '@angular/cdk/layout';
import { ChangeDetectionStrategy, Component, computed, effect, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatListModule } from '@angular/material/list';
import { MatSidenavModule } from '@angular/material/sidenav';
import { MatToolbarModule } from '@angular/material/toolbar';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { filter, map } from 'rxjs';
import { Api } from '../core/api';
import { Auth } from '../core/auth';

export interface NavItem {
  label: string;
  link: string;
  exact?: boolean;
}

/** Navigation for a user, given the merchant being viewed (if any). Exported for tests. */
export function navFor(
  user: { isPlatformAdmin: boolean; memberships: { merchantId: string; role: string }[] },
  merchantId: string | null,
): { platform: NavItem[]; merchant: NavItem[] } {
  const platform: NavItem[] = user.isPlatformAdmin
    ? [
        { label: 'Merchants', link: '/admin/merchants' },
        { label: 'Settlements', link: '/admin/settlements' },
        { label: 'Reconciliation', link: '/admin/reconciliation' },
        { label: 'Reports', link: '/admin/reports' },
      ]
    : [];

  if (!merchantId) {
    return { platform, merchant: [] };
  }

  const role = user.memberships.find((m) => m.merchantId === merchantId)?.role;
  const isAdmin = user.isPlatformAdmin || role === 'MerchantAdmin';
  const base = `/merchants/${merchantId}`;
  const merchant: NavItem[] = [
    { label: 'Overview', link: base, exact: true },
    { label: 'Transactions', link: `${base}/payments` },
    { label: 'Settlements', link: `${base}/settlements` },
    { label: 'Reports', link: `${base}/reports` },
  ];
  if (isAdmin) {
    merchant.push({ label: 'Webhooks', link: `${base}/webhooks` }); // endpoint configuration is MerchantAdmin-only
  }
  return { platform, merchant };
}

@Component({
  selector: 'pf-shell',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, MatToolbarModule, MatSidenavModule, MatListModule, MatButtonModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <a class="pf-skip-link" href="#main">Skip to content</a>
    <mat-toolbar class="pf-toolbar" role="banner">
      @if (narrow()) {
        <button mat-button type="button" (click)="drawerOpen.set(!drawerOpen())" [attr.aria-expanded]="drawerOpen()" aria-controls="pf-nav">
          Menu
        </button>
      }
      <a class="pf-brand" routerLink="/">PayFlow</a>
      <span class="pf-sim-flag" title="No real money moves. All payments are simulated.">Simulation</span>
      <span class="pf-spacer"></span>
      @if (auth.user(); as user) {
        <span class="pf-user">{{ user.displayName }}@if (user.isPlatformAdmin) { · Platform admin }</span>
        <button mat-button type="button" (click)="signOut()">Sign out</button>
      }
    </mat-toolbar>

    <mat-sidenav-container class="pf-container">
      <mat-sidenav id="pf-nav" [mode]="narrow() ? 'over' : 'side'" [opened]="!narrow() || drawerOpen()" (closedStart)="drawerOpen.set(false)">
        <nav aria-label="Main">
          @if (nav().platform.length) {
            <h2 class="pf-nav-heading">Platform</h2>
            <mat-nav-list>
              @for (item of nav().platform; track item.link) {
                <a mat-list-item [routerLink]="item.link" routerLinkActive="pf-active" ariaCurrentWhenActive="page" (click)="drawerOpen.set(false)">{{ item.label }}</a>
              }
            </mat-nav-list>
          }
          @if (merchantId(); as id) {
            <h2 class="pf-nav-heading">{{ merchantName() ?? 'Merchant' }}</h2>
            @if (memberships().length > 1) {
              <label class="pf-switch">
                <span>Switch merchant</span>
                <select [value]="id" (change)="switchMerchant($any($event.target).value)">
                  @for (m of memberships(); track m.merchantId) {
                    <option [value]="m.merchantId">{{ names()[m.merchantId] ?? m.merchantId }}</option>
                  }
                </select>
              </label>
            }
            <mat-nav-list>
              @for (item of nav().merchant; track item.link) {
                <a
                  mat-list-item
                  [routerLink]="item.link"
                  routerLinkActive="pf-active"
                  [routerLinkActiveOptions]="{ exact: item.exact ?? false }"
                  ariaCurrentWhenActive="page"
                  (click)="drawerOpen.set(false)"
                  >{{ item.label }}</a
                >
              }
            </mat-nav-list>
          }
        </nav>
      </mat-sidenav>
      <mat-sidenav-content>
        <main id="main" tabindex="-1" class="pf-main">
          <router-outlet />
        </main>
      </mat-sidenav-content>
    </mat-sidenav-container>
  `,
})
export class Shell {
  protected readonly auth = inject(Auth);
  private readonly api = inject(Api);
  private readonly router = inject(Router);

  protected readonly narrow = toSignal(inject(BreakpointObserver).observe('(max-width: 899px)').pipe(map((s) => s.matches)), { initialValue: false });
  protected readonly drawerOpen = signal(false);

  private readonly url = toSignal(
    this.router.events.pipe(
      filter((e): e is NavigationEnd => e instanceof NavigationEnd),
      map((e) => e.urlAfterRedirects),
    ),
    { initialValue: this.router.url },
  );

  protected readonly merchantId = computed(() => /^\/merchants\/([^/?#]+)/.exec(this.url())?.[1] ?? null);
  protected readonly memberships = computed(() => this.auth.user()?.memberships ?? []);
  protected readonly nav = computed(() => {
    const user = this.auth.user();
    return user ? navFor(user, this.merchantId()) : { platform: [], merchant: [] };
  });

  protected readonly names = signal<Record<string, string | undefined>>({});
  protected readonly merchantName = computed(() => {
    const id = this.merchantId();
    return id ? this.names()[id] : undefined;
  });

  constructor() {
    // Trading names for the heading and switcher, fetched once per merchant.
    effect(() => {
      const ids = new Set([...this.memberships().map((m) => m.merchantId), this.merchantId()].filter((x): x is string => !!x));
      for (const id of ids) {
        if (this.names()[id] === undefined) {
          this.names.update((n) => ({ ...n, [id]: id }));
          this.api
            .merchant(id)
            .then((m) => this.names.update((n) => ({ ...n, [id]: m.tradingName })))
            .catch(() => undefined);
        }
      }
    });
  }

  protected switchMerchant(id: string): void {
    void this.router.navigate(['/merchants', id]);
  }

  protected async signOut(): Promise<void> {
    await this.auth.logout();
    await this.router.navigate(['/login']);
  }
}
