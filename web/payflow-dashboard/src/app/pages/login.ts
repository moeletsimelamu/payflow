import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { ActivatedRoute, Router } from '@angular/router';
import { ApiError } from '../core/api';
import { Auth } from '../core/auth';

/** Only same-app paths: never follow a returnUrl to another site. */
export function safeReturnUrl(returnUrl: string | null): string | null {
  return returnUrl && returnUrl.startsWith('/') && !returnUrl.startsWith('//') && !returnUrl.startsWith('/login') && !returnUrl.startsWith('/mfa-setup')
    ? returnUrl
    : null;
}

@Component({
  selector: 'pf-login',
  imports: [ReactiveFormsModule, MatFormFieldModule, MatInputModule, MatButtonModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="pf-login" id="main">
      <section class="pf-card pf-login-card" aria-labelledby="login-title">
        @if (step() === 'password') {
          <h1 id="login-title">Sign in to PayFlow</h1>
          <p class="pf-muted">A simulated payments platform. No real money or card data is ever used.</p>
          @if (accepted) {
            <p class="pf-notice" role="status">Your password is set. Sign in to continue.</p>
          }
          @if (expired) {
            <p class="pf-notice" role="status">Your session ended. Sign in again to continue.</p>
          }
          <form [formGroup]="form" (ngSubmit)="submit()" novalidate>
            <mat-form-field appearance="outline">
              <mat-label>Email</mat-label>
              <input matInput type="email" formControlName="email" autocomplete="username" required />
              @if (form.controls.email.hasError('required')) {
                <mat-error>Enter your email address.</mat-error>
              } @else if (form.controls.email.hasError('email')) {
                <mat-error>Enter a valid email address.</mat-error>
              }
            </mat-form-field>
            <mat-form-field appearance="outline">
              <mat-label>Password</mat-label>
              <input matInput type="password" formControlName="password" autocomplete="current-password" required />
              @if (form.controls.password.hasError('required')) {
                <mat-error>Enter your password.</mat-error>
              }
            </mat-form-field>
            @if (error(); as message) {
              <p class="pf-error" role="alert">{{ message }}</p>
            }
            <button mat-flat-button type="submit" [disabled]="busy()">{{ busy() ? 'Signing in…' : 'Sign in' }}</button>
          </form>
        } @else {
          <h1 id="login-title">Two-step verification</h1>
          @if (useRecovery()) {
            <p class="pf-muted">Enter one of the recovery codes you stored when you set up your authenticator. Each code works once.</p>
          } @else {
            <p class="pf-muted">Enter the 6-digit code from your authenticator app.</p>
          }
          <form [formGroup]="second" (ngSubmit)="verify()" novalidate>
            @if (useRecovery()) {
              <mat-form-field appearance="outline">
                <mat-label>Recovery code</mat-label>
                <input matInput formControlName="recoveryCode" autocomplete="off" spellcheck="false" required />
              </mat-form-field>
            } @else {
              <mat-form-field appearance="outline">
                <mat-label>Authentication code</mat-label>
                <input matInput formControlName="code" inputmode="numeric" autocomplete="one-time-code" maxlength="7" required />
              </mat-form-field>
            }
            @if (error(); as message) {
              <p class="pf-error" role="alert">{{ message }}</p>
            }
            <button mat-flat-button type="submit" [disabled]="busy()">{{ busy() ? 'Verifying…' : 'Verify' }}</button>
            <button mat-button type="button" (click)="toggleRecovery()">
              {{ useRecovery() ? 'Use the authenticator app instead' : 'Use a recovery code instead' }}
            </button>
          </form>
        }
      </section>
    </main>
  `,
})
export class LoginPage {
  private readonly auth = inject(Auth);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly fb = inject(FormBuilder).nonNullable;

  protected readonly form = this.fb.group({
    email: ['', [Validators.required, Validators.email]],
    password: ['', Validators.required],
  });
  protected readonly second = this.fb.group({ code: [''], recoveryCode: [''] });
  protected readonly step = signal<'password' | 'code'>('password');
  protected readonly useRecovery = signal(false);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly expired = this.route.snapshot.queryParamMap.has('expired');
  protected readonly accepted = this.route.snapshot.queryParamMap.has('accepted');

  protected async submit(): Promise<void> {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    this.busy.set(true);
    this.error.set(null);
    try {
      const { email, password } = this.form.getRawValue();
      const result = await this.auth.login(email, password);
      if ('next' in result) {
        if (result.next === 'mfa_enrollment') {
          // Platform admins must set up an authenticator before they get a session.
          const returnUrl = safeReturnUrl(this.route.snapshot.queryParamMap.get('returnUrl'));
          await this.router.navigate(['/mfa-setup'], { queryParams: returnUrl ? { returnUrl } : {} });
        } else {
          this.form.reset();
          this.step.set('code');
        }
        return;
      }
      await this.goHome();
    } catch (e) {
      const error = ApiError.from(e);
      this.error.set(
        error.status === 401
          ? 'The email or password is incorrect, or the account is locked. After 5 failed attempts, wait 15 minutes.'
          : error.status === 429
            ? 'Too many sign-in attempts. Wait a minute and try again.'
            : error.message,
      );
    } finally {
      this.busy.set(false);
    }
  }

  protected async verify(): Promise<void> {
    const { code, recoveryCode } = this.second.getRawValue();
    const answer = this.useRecovery() ? recoveryCode.trim() : code.replace(/\s/g, '');
    if (!answer) {
      this.error.set(this.useRecovery() ? 'Enter a recovery code.' : 'Enter the 6-digit code.');
      return;
    }

    this.busy.set(true);
    this.error.set(null);
    try {
      await this.auth.verifyMfa(this.useRecovery() ? { recoveryCode: answer } : { code: answer });
      await this.goHome();
    } catch (e) {
      const error = ApiError.from(e);
      if (error.code === 'mfa_sign_in_expired') {
        this.step.set('password');
        this.error.set('The sign-in took too long. Enter your password again.');
      } else {
        this.error.set(
          error.status === 401
            ? 'That code is incorrect or was already used. After 5 failed attempts the account locks for 15 minutes.'
            : error.status === 429
              ? 'Too many attempts. Wait a minute and try again.'
              : error.message,
        );
      }
      this.second.reset();
    } finally {
      this.busy.set(false);
    }
  }

  protected toggleRecovery(): void {
    this.useRecovery.set(!this.useRecovery());
    this.error.set(null);
    this.second.reset();
  }

  private async goHome(): Promise<void> {
    const target = safeReturnUrl(this.route.snapshot.queryParamMap.get('returnUrl')) ?? this.auth.homeUrl();
    await this.router.navigateByUrl(target);
  }
}
