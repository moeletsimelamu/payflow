import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { DomSanitizer, SafeUrl } from '@angular/platform-browser';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Api, ApiError } from '../core/api';
import { Auth } from '../core/auth';
import { MfaEnrollment } from '../core/models';
import { safeReturnUrl } from './login';

/** Groups a Base32 key in fours so it can be read and typed without mistakes. */
export function groupKey(secret: string): string {
  return secret.replace(/(.{4})(?=.)/g, '$1 ');
}

/**
 * Sets up an authenticator app: during sign-in for a platform admin (who gets no session without
 * one), or later from the Security page. Shows the setup key, checks the first code, then shows the
 * recovery codes once. None of these values is stored in the browser or put in a URL.
 */
@Component({
  selector: 'pf-mfa-setup',
  imports: [ReactiveFormsModule, MatFormFieldModule, MatInputModule, MatButtonModule, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="pf-login" id="main">
      <section class="pf-card pf-login-card" aria-labelledby="mfa-title">
        <h1 id="mfa-title">Set up two-step verification</h1>
        @switch (state()) {
          @case ('loading') {
            <p role="status">Preparing your setup key…</p>
          }
          @case ('expired') {
            <p class="pf-error" role="alert">Your sign-in has expired. Sign in again to set up your authenticator.</p>
            <p><a routerLink="/login">Sign in</a></p>
          }
          @case ('enabled') {
            <p class="pf-notice" role="status">An authenticator is already set up for your account.</p>
            <p><a routerLink="/account/security">Go to Security</a></p>
          }
          @case ('failed') {
            <p class="pf-error" role="alert">{{ error() }}</p>
          }
          @case ('scan') {
            @if (enrollment(); as e) {
              <p>Platform admins, and anyone who turns this on, sign in with a password and a code from an authenticator app (Google Authenticator, Microsoft Authenticator, 1Password and others).</p>
              <ol class="pf-steps">
                <li>Open your authenticator app and add an account.</li>
                <li>
                  Choose to enter a setup key, and enter:
                  <code class="pf-setup-key" aria-label="Setup key">{{ groupedKey() }}</code>
                  <span class="pf-muted">(time-based, 6 digits, every 30 seconds). On this device you can also <a [href]="link()">open it in your authenticator app</a>.</span>
                </li>
                <li>Enter the 6-digit code the app shows.</li>
              </ol>
              <form [formGroup]="form" (ngSubmit)="confirm()" novalidate>
                <mat-form-field appearance="outline">
                  <mat-label>Authentication code</mat-label>
                  <input matInput formControlName="code" inputmode="numeric" autocomplete="one-time-code" maxlength="7" required />
                </mat-form-field>
                @if (error(); as message) {
                  <p class="pf-error" role="alert">{{ message }}</p>
                }
                <button mat-flat-button type="submit" [disabled]="busy()">{{ busy() ? 'Checking…' : 'Turn on' }}</button>
              </form>
            }
          }
          @case ('codes') {
            <section class="pf-secret" role="region" aria-label="Recovery codes">
              <h2>Your recovery codes</h2>
              <p><strong>Store these now.</strong> {{ notice() }} Use one if you lose your phone.</p>
              <ul class="pf-recovery-codes">
                @for (code of recoveryCodes(); track code) {
                  <li><code>{{ code }}</code></li>
                }
              </ul>
              <button mat-stroked-button type="button" (click)="copy()">{{ copied() ? 'Copied' : 'Copy all' }}</button>
              @if (copyFailed()) {
                <p class="pf-error" role="alert">Copying is not available here; select the codes and copy them manually.</p>
              }
              <p><button mat-flat-button type="button" (click)="done()">I have stored them</button></p>
            </section>
          }
        }
      </section>
    </main>
  `,
  styles: `
    .pf-steps li { margin: 8px 0; }
    .pf-setup-key {
      display: block;
      margin: 6px 0;
      padding: 6px 8px;
      font-size: 1.05rem;
      letter-spacing: 0.04em;
      background: #fff;
      border: 1px solid #c4c7cf;
      border-radius: 4px;
      word-break: break-all;
      user-select: all;
    }
    .pf-secret { border: 2px solid #8a6d00; background: #fffbea; border-radius: 8px; padding: 12px 16px; }
    .pf-secret h2 { margin: 0 0 4px; font-size: 1.05rem; }
    .pf-recovery-codes { columns: 2; padding-left: 1.2rem; }
    .pf-recovery-codes code { user-select: all; }
  `,
})
export class MfaSetupPage implements OnInit {
  private readonly api = inject(Api);
  private readonly auth = inject(Auth);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly sanitizer = inject(DomSanitizer);

  protected readonly form = inject(FormBuilder).nonNullable.group({ code: [''] });
  protected readonly state = signal<'loading' | 'scan' | 'codes' | 'expired' | 'enabled' | 'failed'>('loading');
  protected readonly enrollment = signal<MfaEnrollment | null>(null);
  protected readonly recoveryCodes = signal<string[]>([]);
  protected readonly notice = signal('');
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly copied = signal(false);
  protected readonly copyFailed = signal(false);
  protected readonly groupedKey = computed(() => groupKey(this.enrollment()?.secret ?? ''));

  /** The otpauth:// link, trusted only after checking it is exactly that kind of link. */
  protected readonly link = computed<SafeUrl | null>(() => {
    const uri = this.enrollment()?.provisioningUri ?? '';
    return /^otpauth:\/\/totp\/[^\s"'<>]+$/.test(uri) ? this.sanitizer.bypassSecurityTrustUrl(uri) : null;
  });

  async ngOnInit(): Promise<void> {
    try {
      // The page may be opened in a fresh tab or after a reload, before any XSRF token exists.
      await this.api.antiforgery();
      this.enrollment.set(await this.api.startMfaEnrollment());
      this.state.set('scan');
    } catch (e) {
      const error = ApiError.from(e);
      if (error.code === 'mfa_already_enabled') {
        this.state.set('enabled');
      } else if (error.status === 401) {
        this.state.set('expired');
      } else {
        this.error.set(error.message);
        this.state.set('failed');
      }
    }
  }

  protected async confirm(): Promise<void> {
    const code = this.form.getRawValue().code.replace(/\s/g, '');
    if (!/^\d{6}$/.test(code)) {
      this.error.set('Enter the 6-digit code from your authenticator app.');
      return;
    }

    this.busy.set(true);
    this.error.set(null);
    try {
      const result = await this.auth.confirmMfaEnrollment(code);
      this.recoveryCodes.set(result.recoveryCodes);
      this.notice.set(result.notice);
      this.enrollment.set(null); // the setup key is not kept once the authenticator is on
      this.state.set('codes');
    } catch (e) {
      const error = ApiError.from(e);
      if (error.code === 'mfa_sign_in_expired') {
        this.state.set('expired');
      } else {
        this.error.set(
          error.status === 401
            ? 'That code did not match. Check the time on your phone and enter the newest code. After 5 failed attempts the account locks for 15 minutes.'
            : error.message,
        );
      }
      this.form.reset();
    } finally {
      this.busy.set(false);
    }
  }

  protected async copy(): Promise<void> {
    try {
      await navigator.clipboard.writeText(this.recoveryCodes().join('\n'));
      this.copied.set(true);
      this.copyFailed.set(false);
    } catch {
      this.copyFailed.set(true);
    }
  }

  protected async done(): Promise<void> {
    this.recoveryCodes.set([]);
    const returnUrl = safeReturnUrl(this.route.snapshot.queryParamMap.get('returnUrl'));
    await this.router.navigateByUrl(returnUrl ?? this.auth.homeUrl());
  }
}
