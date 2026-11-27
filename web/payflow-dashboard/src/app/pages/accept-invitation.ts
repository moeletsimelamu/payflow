import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { AbstractControl, FormBuilder, ReactiveFormsModule, ValidationErrors, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { Router, RouterLink } from '@angular/router';
import { Api, ApiError } from '../core/api';
import { parseInvitationFragment } from '../core/invitation-link';

export const MIN_PASSWORD_LENGTH = 12;

function matching(group: AbstractControl): ValidationErrors | null {
  return group.get('password')?.value === group.get('confirm')?.value ? null : { mismatch: true };
}

/** First sign-in for an invited user: set a password with the one-time token. */
@Component({
  selector: 'pf-accept-invitation',
  imports: [ReactiveFormsModule, RouterLink, MatFormFieldModule, MatInputModule, MatButtonModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="pf-login" id="main">
      <section class="pf-card pf-login-card" aria-labelledby="accept-title">
        <h1 id="accept-title">Set up your PayFlow account</h1>
        <p class="pf-muted">Choose a password of at least {{ minLength }} characters that is not a common password and does not contain your name, your email name or "PayFlow". A long passphrase works well. The invitation can be used once and expires after 3 days.</p>
        <form [formGroup]="form" (ngSubmit)="submit()" novalidate>
          <mat-form-field appearance="outline">
            <mat-label>Email</mat-label>
            <input matInput type="email" formControlName="email" autocomplete="username" required />
            @if (form.controls.email.invalid) {
              <mat-error>Enter the email address the invitation was sent to.</mat-error>
            }
          </mat-form-field>
          <mat-form-field appearance="outline">
            <mat-label>Invitation token</mat-label>
            <input matInput formControlName="token" autocomplete="one-time-code" required />
            @if (form.controls.token.invalid) {
              <mat-error>Paste the token from your invitation.</mat-error>
            }
          </mat-form-field>
          <mat-form-field appearance="outline">
            <mat-label>New password</mat-label>
            <input matInput type="password" formControlName="password" autocomplete="new-password" required />
            @if (form.controls.password.invalid) {
              <mat-error>Use at least {{ minLength }} characters.</mat-error>
            }
          </mat-form-field>
          <mat-form-field appearance="outline">
            <mat-label>Repeat the password</mat-label>
            <input matInput type="password" formControlName="confirm" autocomplete="new-password" required />
          </mat-form-field>
          @if (form.hasError('mismatch') && form.controls.confirm.touched) {
            <p class="pf-error" role="alert">The passwords do not match.</p>
          }
          @if (error(); as message) {
            <p class="pf-error" role="alert">{{ message }}</p>
          }
          <button mat-flat-button type="submit" [disabled]="busy()">{{ busy() ? 'Saving…' : 'Set password' }}</button>
        </form>
        <p><a routerLink="/login">Back to sign in</a></p>
      </section>
    </main>
  `,
})
export class AcceptInvitationPage {
  private readonly api = inject(Api);
  private readonly router = inject(Router);
  /** Email and token from the link's fragment (see invitation-link.ts for why not URLSearchParams). */
  private readonly link = parseInvitationFragment(location.hash);
  protected readonly minLength = MIN_PASSWORD_LENGTH;

  protected readonly form = inject(FormBuilder).nonNullable.group(
    {
      email: [this.link.email ?? '', [Validators.required, Validators.email]],
      token: [this.link.token ?? '', Validators.required],
      password: ['', [Validators.required, Validators.minLength(MIN_PASSWORD_LENGTH)]],
      confirm: ['', Validators.required],
    },
    { validators: matching },
  );
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  constructor() {
    // Take the token out of the address bar and browser history once it is in the form.
    if (this.link.token) {
      history.replaceState(history.state, '', location.pathname);
    }
  }

  protected async submit(): Promise<void> {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    try {
      const { email, token, password } = this.form.getRawValue();
      await this.api.antiforgery();
      await this.api.acceptInvitation(email.trim(), token.trim(), password);
      await this.router.navigate(['/login'], { queryParams: { accepted: 1 } });
    } catch (e) {
      const error = ApiError.from(e);
      this.error.set(
        error.status === 400 && error.problem.errors
          ? Object.values(error.problem.errors).flat().join(' ')
          : error.status === 400 || error.status === 401
            ? 'The invitation is invalid, already used or expired. Ask your merchant admin for a new one.'
            : error.message,
      );
    } finally {
      this.busy.set(false);
    }
  }
}
