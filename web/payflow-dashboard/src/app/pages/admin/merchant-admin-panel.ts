import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { Api, ApiError } from '../../core/api';
import { Merchant } from '../../core/models';
import { validateAmount } from '../../core/money';
import { confirmAction } from '../../shared/confirm-dialog';
import { MoneyPipe } from '../../shared/pipes';

type Target = 'Active' | 'Suspended' | 'Closed';

/** Which status changes the UI offers from each status (the API's whitelist decides). */
export function statusActions(status: Merchant['status']): Target[] {
  switch (status) {
    case 'PendingActivation':
      return ['Active', 'Closed'];
    case 'Active':
      return ['Suspended', 'Closed'];
    case 'Suspended':
      return ['Active', 'Closed'];
    default:
      return [];
  }
}

const LABEL: Record<Target, string> = { Active: 'Activate', Suspended: 'Suspend', Closed: 'Close' };

/** Platform-admin controls on a merchant's overview (MER-2, MER-4): status and currency limits. */
@Component({
  selector: 'pf-merchant-admin',
  imports: [FormsModule, MatButtonModule, MatFormFieldModule, MatInputModule, MoneyPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="pf-card" aria-labelledby="manage">
      <h2 id="manage">Manage merchant (platform admin)</h2>
      @if (actions().length) {
        <div class="pf-filters">
          <mat-form-field appearance="outline" class="pf-full">
            <mat-label>Reason (required to suspend or close)</mat-label>
            <input matInput name="reason" maxlength="500" [ngModel]="reason()" (ngModelChange)="reason.set($event)" />
          </mat-form-field>
          @for (a of actions(); track a) {
            <button mat-stroked-button type="button" (click)="changeStatus(a)" [disabled]="busy()" [class.pf-danger]="a === 'Closed'">{{ label[a] }}</button>
          }
        </div>
      } @else {
        <p class="pf-muted">This merchant is closed; its status can no longer change.</p>
      }

      <h3>Currency limits</h3>
      <table class="pf-table">
        <caption class="pf-visually-hidden">Enabled currencies and limits</caption>
        <thead><tr><th scope="col">Currency</th><th scope="col">Max payment</th><th scope="col">Operator refund limit</th><th scope="col"><span class="pf-visually-hidden">Save</span></th></tr></thead>
        <tbody>
          @for (c of merchant().currencies; track c.currency) {
            <tr>
              <td>{{ c.currency }}</td>
              <td>
                <label class="pf-visually-hidden" [for]="'max-' + c.currency">Max payment in {{ c.currency }}</label>
                <input class="pf-inline-input" [id]="'max-' + c.currency" inputmode="decimal" [value]="c.maxPaymentAmount" #max />
                <span class="pf-muted">now {{ c.maxPaymentAmount | money: c.currency }}</span>
              </td>
              <td>
                <label class="pf-visually-hidden" [for]="'limit-' + c.currency">Operator refund limit in {{ c.currency }}</label>
                <input class="pf-inline-input" [id]="'limit-' + c.currency" inputmode="decimal" [value]="c.operatorRefundLimit ?? ''" #limit placeholder="none" />
              </td>
              <td><button mat-button type="button" (click)="saveLimits(c.currency, max.value, limit.value)" [disabled]="busy()">Save {{ c.currency }}</button></td>
            </tr>
          }
        </tbody>
      </table>
      @if (message(); as m) {
        <p [class]="failed() ? 'pf-error' : 'pf-notice'" role="status">{{ m }}</p>
      }
    </section>
  `,
})
export class MerchantAdminPanel {
  private readonly api = inject(Api);
  private readonly dialog = inject(MatDialog);
  readonly merchant = input.required<Merchant>();
  readonly changed = output<Merchant>();

  protected readonly label = LABEL;
  protected readonly actions = computed(() => statusActions(this.merchant().status));
  protected readonly reason = signal('');
  protected readonly busy = signal(false);
  protected readonly message = signal<string | null>(null);
  protected readonly failed = signal(false);

  protected async changeStatus(target: Target): Promise<void> {
    const reason = this.reason().trim();
    if (target !== 'Active' && !reason) {
      this.fail('Give a reason to suspend or close a merchant; it is recorded in the audit log.');
      return;
    }
    if (target === 'Closed') {
      const ok = await confirmAction(this.dialog, {
        title: `Close ${this.merchant().tradingName}?`,
        message: 'Closing is permanent: every API key stops working immediately and the merchant cannot be reopened.',
        confirmLabel: 'Close merchant',
        destructive: true,
      });
      if (!ok) {
        return;
      }
    }
    await this.run(async () => {
      const updated = await this.api.changeMerchantStatus(this.merchant().id, target, reason || null);
      this.reason.set('');
      this.succeed(`Status is now ${updated.status}.`, updated);
    });
  }

  protected async saveLimits(currency: string, max: string, limit: string): Promise<void> {
    if (validateAmount(max, currency) !== null || (limit.trim() !== '' && validateAmount(limit, currency) !== null)) {
      this.fail(`Enter positive ${currency} amounts with the right number of decimals.`);
      return;
    }
    await this.run(async () => {
      const updated = await this.api.setCurrencyLimits(this.merchant().id, currency, { maxPaymentAmount: max.trim(), operatorRefundLimit: limit.trim() || null });
      this.succeed(`${currency} limits saved.`, updated);
    });
  }

  private async run(work: () => Promise<void>): Promise<void> {
    this.busy.set(true);
    this.message.set(null);
    try {
      await work();
    } catch (e) {
      this.fail(ApiError.from(e).message);
    } finally {
      this.busy.set(false);
    }
  }

  private succeed(text: string, merchant: Merchant): void {
    this.failed.set(false);
    this.message.set(text);
    this.changed.emit(merchant);
  }

  private fail(text: string): void {
    this.failed.set(true);
    this.message.set(text);
  }
}
