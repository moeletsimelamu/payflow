import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { Router, RouterLink } from '@angular/router';
import { Api, ApiError } from '../core/api';
import { MfaStatus } from '../core/models';

/** Two-step verification for the signed-in user: status, new recovery codes, turning it off. */
@Component({
  selector: 'pf-account-security',
  imports: [ReactiveFormsModule, MatFormFieldModule, MatInputModule, MatButtonModule, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h1>Security</h1>
    <section class="pf-card" aria-labelledby="mfa-heading">
      <h2 id="mfa-heading">Two-step verification</h2>
      @if (status(); as s) {
        @if (s.enabled) {
          <p role="status">On. Signing in needs your password and a code from your authenticator app. {{ s.recoveryCodesRemaining }} recovery codes left.</p>
          @if (codes().length) {
            <section class="pf-secret" role="region" aria-label="New recovery codes">
              <p><strong>Store these now.</strong> {{ notice() }} Your previous codes no longer work.</p>
              <ul class="pf-recovery-codes">
                @for (code of codes(); track code) {
                  <li><code>{{ code }}</code></li>
                }
              </ul>
              <button mat-flat-button type="button" (click)="codes.set([])">I have stored them</button>
            </section>
          }
          <form [formGroup]="form" novalidate aria-label="Manage two-step verification">
            <p class="pf-muted">Enter a current code from your authenticator app to change these settings.</p>
            <mat-form-field appearance="outline">
              <mat-label>Authentication code</mat-label>
              <input matInput formControlName="code" inputmode="numeric" autocomplete="one-time-code" maxlength="7" />
            </mat-form-field>
            @if (error(); as message) {
              <p class="pf-error" role="alert">{{ message }}</p>
            }
            <div class="pf-actions">
              <button mat-stroked-button type="button" [disabled]="busy()" (click)="regenerate()">New recovery codes</button>
              @if (!s.required) {
                <button mat-stroked-button type="button" [disabled]="busy()" (click)="disable()">Turn off</button>
              }
            </div>
            @if (s.required) {
              <p class="pf-muted">Platform admins must keep two-step verification on.</p>
            }
          </form>
        } @else {
          <p role="status">Off. Signing in needs only your password.</p>
          <a mat-flat-button routerLink="/mfa-setup" [queryParams]="{ returnUrl: '/account/security' }">Set up an authenticator</a>
        }
      } @else if (error(); as message) {
        <p class="pf-error" role="alert">{{ message }}</p>
      } @else {
        <p role="status">Loading…</p>
      }
    </section>
  `,
  styles: `
    .pf-actions { display: flex; gap: 8px; flex-wrap: wrap; }
    .pf-secret { border: 2px solid #8a6d00; background: #fffbea; border-radius: 8px; padding: 12px 16px; margin: 12px 0; }
    .pf-recovery-codes { columns: 2; padding-left: 1.2rem; }
    .pf-recovery-codes code { user-select: all; }
  `,
})
export class AccountSecurityPage implements OnInit {
  private readonly api = inject(Api);
  private readonly router = inject(Router);

  protected readonly form = inject(FormBuilder).nonNullable.group({ code: [''] });
  protected readonly status = signal<MfaStatus | null>(null);
  protected readonly codes = signal<string[]>([]);
  protected readonly notice = signal('');
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  async ngOnInit(): Promise<void> {
    await this.load();
  }

  protected async regenerate(): Promise<void> {
    await this.withCode(async (code) => {
      const result = await this.api.regenerateRecoveryCodes(code);
      this.codes.set(result.recoveryCodes);
      this.notice.set(result.notice);
    });
  }

  protected async disable(): Promise<void> {
    await this.withCode(async (code) => {
      await this.api.disableMfa(code);
      this.codes.set([]);
    });
  }

  private async withCode(action: (code: string) => Promise<void>): Promise<void> {
    const code = this.form.getRawValue().code.replace(/\s/g, '');
    if (!/^\d{6}$/.test(code)) {
      this.error.set('Enter the 6-digit code from your authenticator app.');
      return;
    }

    this.busy.set(true);
    this.error.set(null);
    try {
      await action(code);
      await this.load();
    } catch (e) {
      const error = ApiError.from(e);
      this.error.set(
        error.code === 'mfa_code_invalid'
          ? 'That code is incorrect or was already used. Wait for the next code. After 5 failed attempts the account locks for 15 minutes.'
          : error.message,
      );
    } finally {
      this.form.reset();
      this.busy.set(false);
    }
  }

  private async load(): Promise<void> {
    try {
      this.status.set(await this.api.mfaStatus());
    } catch (e) {
      const error = ApiError.from(e);
      if (error.status === 401) {
        await this.router.navigate(['/login'], { queryParams: { expired: 1 } });
        return;
      }
      this.error.set(error.message);
    }
  }
}
