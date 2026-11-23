import { ChangeDetectionStrategy, Component, OnInit, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { Api, ApiError } from '../../core/api';
import { loadable } from '../../core/loadable';
import { ApiKey } from '../../core/models';
import { confirmAction } from '../../shared/confirm-dialog';
import { UtcPipe } from '../../shared/pipes';
import { SecretPanel } from '../../shared/secret-panel';
import { StateView } from '../../shared/state';
import { StatusBadge } from '../../shared/status-badge';

/** API keys (MER-5..7): created and rotated with the secret shown once; revocation is immediate. */
@Component({
  selector: 'pf-api-keys',
  imports: [FormsModule, MatButtonModule, MatFormFieldModule, MatInputModule, StateView, StatusBadge, SecretPanel, UtcPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="pf-page-header">
      <h1>API keys</h1>
      <p class="pf-muted">Your servers use these to call the merchant API (<code>/v1</code>). Keys start with <code>pf_test_sk_</code>: PayFlow only ever runs in simulation.</p>
    </header>

    @if (secret(); as s) {
      <pf-secret [title]="'Secret for ' + s.name" [value]="s.value" notice="PayFlow stores only a hash of it; it cannot be shown again." (dismissed)="secret.set(null)" />
    }

    <section class="pf-card" aria-labelledby="new-key">
      <h2 id="new-key">Create a key</h2>
      <form class="pf-filters" (ngSubmit)="create()" aria-label="Create an API key">
        <mat-form-field appearance="outline">
          <mat-label>Name</mat-label>
          <input matInput name="name" maxlength="100" [ngModel]="name()" (ngModelChange)="name.set($event)" placeholder="e.g. Checkout server" required />
        </mat-form-field>
        <button mat-flat-button type="submit" [disabled]="busy()">Create key</button>
      </form>
      @if (message(); as m) {
        <p [class]="failed() ? 'pf-error' : 'pf-notice'" role="status">{{ m }}</p>
      }
    </section>

    <section class="pf-card" aria-labelledby="keys">
      <h2 id="keys">Keys</h2>
      <pf-state [of]="keys" emptyText="No API keys yet.">
        <div class="pf-table-wrap">
          <table class="pf-table">
            <caption class="pf-visually-hidden">API keys</caption>
            <thead><tr><th scope="col">Name</th><th scope="col">Prefix</th><th scope="col">State</th><th scope="col">Created</th><th scope="col">Last used</th><th scope="col">Expires</th><th scope="col">Actions</th></tr></thead>
            <tbody>
              @for (k of keys.value(); track k.id) {
                <tr>
                  <td>{{ k.name }}</td>
                  <td><code>{{ k.prefix }}…</code></td>
                  <td><pf-status [value]="k.state" /></td>
                  <td class="pf-when">{{ k.createdAt | utc: false }}</td>
                  <td class="pf-when">{{ k.lastUsedAt | utc: false }}</td>
                  <td class="pf-when">{{ k.expiresAt | utc: false }}</td>
                  <td>
                    @if (k.state === 'Active') {
                      <label class="pf-visually-hidden" [for]="'grace-' + k.id">Grace period for {{ k.name }} in hours</label>
                      <input class="pf-inline-input pf-tiny" type="number" min="0" max="168" [id]="'grace-' + k.id" value="24" #grace />
                      <span class="pf-muted">h</span>
                      <button mat-button type="button" (click)="rotate(k, +grace.value)" [disabled]="busy()">Rotate</button>
                    }
                    @if (k.state === 'Active' || k.state === 'Expiring') {
                      <button mat-button type="button" class="pf-danger-text" (click)="revoke(k)" [disabled]="busy()">Revoke</button>
                    }
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      </pf-state>
    </section>
  `,
})
export class ApiKeysPage implements OnInit {
  private readonly api = inject(Api);
  private readonly dialog = inject(MatDialog);
  readonly merchantId = input.required<string>();

  protected readonly keys = loadable(() => this.api.apiKeys(this.merchantId()), (list) => list.length === 0);
  protected readonly name = signal('');
  protected readonly secret = signal<{ name: string; value: string } | null>(null);
  protected readonly busy = signal(false);
  protected readonly message = signal<string | null>(null);
  protected readonly failed = signal(false);

  ngOnInit(): void {
    void this.keys.load();
  }

  protected async create(): Promise<void> {
    const name = this.name().trim();
    if (!name) {
      this.report('Give the key a name, such as the server that will use it.', true);
      return;
    }
    await this.run(async () => {
      const issued = await this.api.createApiKey(this.merchantId(), name);
      this.secret.set({ name: issued.key.name, value: issued.secret });
      this.name.set('');
      this.report(`Key ${issued.key.prefix}… created.`, false);
    });
  }

  protected async rotate(key: ApiKey, graceHours: number): Promise<void> {
    if (!Number.isInteger(graceHours) || graceHours < 0 || graceHours > 168) {
      this.report('The grace period is 0 to 168 hours.', true);
      return;
    }
    await this.run(async () => {
      const issued = await this.api.rotateApiKey(this.merchantId(), key.id, graceHours);
      this.secret.set({ name: issued.key.name, value: issued.secret });
      this.report(graceHours ? `New key issued; the old one keeps working for ${graceHours} h.` : 'New key issued; the old one stopped working.', false);
    });
  }

  protected async revoke(key: ApiKey): Promise<void> {
    const ok = await confirmAction(this.dialog, {
      title: `Revoke "${key.name}"?`,
      message: `Requests signed with ${key.prefix}… will be refused immediately. This cannot be undone.`,
      confirmLabel: 'Revoke key',
      destructive: true,
    });
    if (ok) {
      await this.run(async () => {
        await this.api.revokeApiKey(this.merchantId(), key.id);
        this.report(`Key ${key.prefix}… revoked.`, false);
      });
    }
  }

  private async run(work: () => Promise<void>): Promise<void> {
    this.busy.set(true);
    try {
      await work();
      void this.keys.load();
    } catch (e) {
      this.report(ApiError.from(e).message, true);
    } finally {
      this.busy.set(false);
    }
  }

  private report(text: string, failed: boolean): void {
    this.failed.set(failed);
    this.message.set(text);
  }
}
