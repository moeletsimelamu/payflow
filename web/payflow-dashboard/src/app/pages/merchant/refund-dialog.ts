import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { Api, ApiError } from '../../core/api';
import { Refund } from '../../core/models';
import { AmountProblem, formatMoney, minorUnits, validateAmount } from '../../core/money';

export interface RefundDialogData {
  merchantId: string;
  paymentId: string;
  currency: string;
  refundable: string;
  operatorLimit: string | null;
}

const MESSAGES: Record<AmountProblem, (d: RefundDialogData) => string> = {
  required: () => 'Enter the amount to refund.',
  format: () => 'Use digits and an optional decimal point, for example 40.00.',
  precision: (d) => `${d.currency} amounts have at most ${minorUnits(d.currency)} decimal places.`,
  positive: () => 'The amount must be more than zero.',
  too_large: (d) => `At most ${formatMoney(d.refundable, d.currency)} can still be refunded.`,
};

/**
 * Refund a payment (REF-1..4). One idempotency key is created when the dialog opens and reused for
 * every submit, so a double click or a retry after a network error cannot refund twice.
 */
@Component({
  selector: 'pf-refund-dialog',
  imports: [FormsModule, MatDialogModule, MatFormFieldModule, MatInputModule, MatButtonModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>Refund payment</h2>
    <form (ngSubmit)="submit()" novalidate>
      <mat-dialog-content>
        <p>Up to <strong>{{ refundableText }}</strong> can be refunded. Refunds are simulated; the fee is not returned.</p>
        @if (data.operatorLimit) {
          <p class="pf-muted">Operators may refund up to {{ limitText }} per refund.</p>
        }
        <mat-form-field appearance="outline" class="pf-full">
          <mat-label>Amount ({{ data.currency }})</mat-label>
          <input matInput name="amount" inputmode="decimal" autocomplete="off" [ngModel]="amount()" (ngModelChange)="amount.set($event); touched.set(true)" required aria-describedby="refund-amount-hint" />
          <mat-hint id="refund-amount-hint">For example {{ example }}</mat-hint>
        </mat-form-field>
        @if (touched() && problem()) {
          <p class="pf-error" role="alert">{{ problem() }}</p>
        }
        <mat-form-field appearance="outline" class="pf-full">
          <mat-label>Reason (optional)</mat-label>
          <input matInput name="reason" maxlength="500" [ngModel]="reason()" (ngModelChange)="reason.set($event)" />
        </mat-form-field>
        @if (serverError(); as message) {
          <p class="pf-error" role="alert">{{ message }}</p>
        }
      </mat-dialog-content>
      <mat-dialog-actions align="end">
        <button mat-button type="button" mat-dialog-close [disabled]="busy()">Cancel</button>
        <button mat-flat-button type="submit" [disabled]="busy()">{{ busy() ? 'Refunding…' : 'Refund' }}</button>
      </mat-dialog-actions>
    </form>
  `,
})
export class RefundDialog {
  protected readonly data = inject<RefundDialogData>(MAT_DIALOG_DATA);
  private readonly api = inject(Api);
  private readonly ref = inject<MatDialogRef<RefundDialog, Refund>>(MatDialogRef);

  /** Exposed for tests: the key every submit of this dialog uses. */
  readonly idempotencyKey = crypto.randomUUID();

  protected readonly amount = signal(this.data.refundable);
  protected readonly reason = signal('');
  protected readonly touched = signal(false);
  protected readonly busy = signal(false);
  protected readonly serverError = signal<string | null>(null);

  protected readonly refundableText = formatMoney(this.data.refundable, this.data.currency);
  protected readonly limitText = formatMoney(this.data.operatorLimit, this.data.currency);
  protected readonly example = minorUnits(this.data.currency) === 0 ? '500' : `40.${'0'.repeat(minorUnits(this.data.currency))}`;

  protected readonly problem = computed(() => {
    const p = validateAmount(this.amount(), this.data.currency, this.data.refundable);
    return p ? MESSAGES[p](this.data) : null;
  });

  async submit(): Promise<void> {
    this.touched.set(true);
    if (this.problem() || this.busy()) {
      return;
    }

    this.busy.set(true);
    this.serverError.set(null);
    try {
      const refund = await this.api.refund(
        this.data.merchantId,
        this.data.paymentId,
        { amount: this.amount().trim(), reason: this.reason().trim() || null },
        this.idempotencyKey,
      );
      this.ref.close(refund);
    } catch (e) {
      const error = ApiError.from(e);
      this.serverError.set(
        error.code === 'refund_exceeds_refundable_balance'
          ?`That is more than can still be refunded (${this.refundableText}). Someone may have refunded part of it already; reload the payment.`
          : error.code === 'operator_refund_limit_exceeded'
            ? `Operators may refund at most ${this.limitText} at a time. Ask a merchant admin to refund more.`
            : error.message,
      );
    } finally {
      this.busy.set(false);
    }
  }
}
