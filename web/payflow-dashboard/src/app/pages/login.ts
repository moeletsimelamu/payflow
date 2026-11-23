import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { ActivatedRoute, Router } from '@angular/router';
import { ApiError } from '../core/api';
import { Auth } from '../core/auth';

@Component({
  selector: 'pf-login',
  imports: [ReactiveFormsModule, MatFormFieldModule, MatInputModule, MatButtonModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="pf-login" id="main">
      <section class="pf-card pf-login-card" aria-labelledby="login-title">
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
      </section>
    </main>
  `,
})
export class LoginPage {
  private readonly auth = inject(Auth);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  protected readonly form = inject(FormBuilder).nonNullable.group({
    email: ['', [Validators.required, Validators.email]],
    password: ['', Validators.required],
  });
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
      await this.auth.login(email, password);
      const returnUrl = this.route.snapshot.queryParamMap.get('returnUrl');
      // Only same-app paths: never follow a returnUrl to another site.
      const target = returnUrl && returnUrl.startsWith('/') && !returnUrl.startsWith('//') && !returnUrl.startsWith('/login') ? returnUrl : this.auth.homeUrl();
      await this.router.navigateByUrl(target);
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
}
