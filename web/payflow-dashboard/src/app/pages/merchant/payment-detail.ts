import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { RouterLink } from '@angular/router';
import { Api } from '../../core/api';
import { Auth } from '../../core/auth';
import { loadable } from '../../core/loadable';
import { toMinorUnits } from '../../core/money';
import { MoneyPipe, UtcPipe, WordsPipe } from '../../shared/pipes';
import { StateView } from '../../shared/state';
import { StatusBadge } from '../../shared/status-badge';
import { RefundDialog, RefundDialogData } from './refund-dialog';

/** One transaction (TXN-2..4): lifecycle timeline, ledger entries, refunds and reversal. */
@Component({
  selector: 'pf-payment-detail',
  imports: [RouterLink, MatButtonModule, StateView, StatusBadge, MoneyPipe, UtcPipe, WordsPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <p><a [routerLink]="['/merchants', merchantId(), 'payments']">← Transactions</a></p>
    <pf-state [of]="detail" loadingText="Loading transaction…" errorTitle="This transaction could not be loaded.">
      @if (detail.value(); as d) {
        <header class="pf-page-header">
          <h1>{{ d.payment.amount | money: d.payment.currency }} <pf-status [value]="d.payment.status" /></h1>
          <p class="pf-muted">{{ d.payment.merchantReference }} · <code>{{ d.payment.id }}</code> · simulated</p>
          @if (canRefund()) {
            <button mat-flat-button type="button" (click)="openRefund()">Refund</button>
          }
          @if (refundedNotice(); as notice) {
            <p class="pf-notice" role="status">{{ notice }}</p>
          }
        </header>

        <div class="pf-grid-2">
          <section class="pf-card" aria-labelledby="summary">
            <h2 id="summary">Summary</h2>
            <dl class="pf-dl">
              <dt>Amount</dt><dd>{{ d.payment.amount | money: d.payment.currency }}</dd>
              <dt>Fee</dt><dd>{{ d.payment.fee | money: d.payment.currency }} (rate {{ d.feeRate }}, plan <code>{{ d.feePlanId }}</code>)</dd>
              <dt>Net to merchant</dt><dd>{{ d.payment.net | money: d.payment.currency }}</dd>
              <dt>Refunded</dt><dd>{{ d.payment.refundedAmount | money: d.payment.currency }}</dd>
              <dt>Still refundable</dt><dd>{{ d.payment.refundableAmount | money: d.payment.currency }}</dd>
              <dt>Method</dt><dd><code>{{ d.payment.paymentMethod }}</code></dd>
              @if (d.payment.failureCode) {
                <dt>Failure</dt><dd><code>{{ d.payment.failureCode }}</code> — {{ d.payment.failureReason }}</dd>
              }
              <dt>Created</dt><dd>{{ d.payment.createdAt | utc }}</dd>
              <dt>Completed</dt><dd>{{ d.payment.completedAt | utc }}</dd>
              <dt>Processing time</dt><dd>{{ d.processingMilliseconds === null ? '—' : d.processingMilliseconds + ' ms' }}</dd>
              <dt>Idempotency key</dt><dd><code>{{ d.idempotencyKey }}</code></dd>
            </dl>
          </section>

          <section class="pf-card" aria-labelledby="timeline">
            <h2 id="timeline">Lifecycle</h2>
            <ol class="pf-timeline">
              @for (h of d.payment.statusHistory ?? []; track $index) {
                <li>
                  <span class="pf-timeline-when">{{ h.occurredAt | utc }}</span>
                  <span>
                    @if (h.from) { <pf-status [value]="h.from" /> → }
                    <pf-status [value]="h.to" />
                  </span>
                  <span class="pf-muted">{{ h.reason }} ({{ h.actorType | words }})</span>
                </li>
              }
            </ol>
          </section>
        </div>

        <section class="pf-card" aria-labelledby="ledger">
          <h2 id="ledger">Ledger entries</h2>
          @if (d.ledger.length) {
            <div class="pf-table-wrap">
              <table class="pf-table">
                <caption class="pf-visually-hidden">Double-entry postings for this payment</caption>
                <thead><tr><th scope="col">When</th><th scope="col">Entry</th><th scope="col">Account</th><th scope="col" class="pf-num">Debit</th><th scope="col" class="pf-num">Credit</th></tr></thead>
                <tbody>
                  @for (e of d.ledger; track $index) {
                    @for (p of e.postings; track $index; let first = $first) {
                      <tr>
                        <td>{{ first ? (e.occurredAt | utc) : '' }}</td>
                        <td>{{ first ? (e.entryType | words) : '' }}</td>
                        <td>{{ p.account }}</td>
                        <td class="pf-num">{{ p.direction === 'Debit' ? (p.amount | money: d.payment.currency) : '' }}</td>
                        <td class="pf-num">{{ p.direction === 'Credit' ? (p.amount | money: d.payment.currency) : '' }}</td>
                      </tr>
                    }
                  }
                </tbody>
              </table>
            </div>
          } @else {
            <p class="pf-muted">No ledger entries: nothing is posted until a payment succeeds.</p>
          }
        </section>
      }
    </pf-state>

    <section class="pf-card" aria-labelledby="refunds">
      <h2 id="refunds">Refunds and reversal</h2>
      <pf-state [of]="refunds" loadingText="Loading refunds…">
        @if (refunds.value(); as r) {
          @if (r.reversal; as rv) {
            <p>
              Reversed {{ rv.createdAt | utc }}: {{ rv.amount | money: rv.currency }} returned to the customer and the fee
              ({{ rv.feeReturned | money: rv.currency }}) returned to the merchant. {{ rv.reason }}
            </p>
          }
          @if (r.refunds.length) {
            <table class="pf-table">
              <caption class="pf-visually-hidden">Refunds of this payment</caption>
              <thead><tr><th scope="col">Requested</th><th scope="col">Status</th><th scope="col" class="pf-num">Amount</th><th scope="col">Reason</th></tr></thead>
              <tbody>
                @for (f of r.refunds; track f.id) {
                  <tr>
                    <td>{{ f.createdAt | utc }}<br /><code class="pf-muted">{{ f.id }}</code></td>
                    <td><pf-status [value]="f.status" />@if (f.failureReason) { <span class="pf-muted"> {{ f.failureReason }}</span> }</td>
                    <td class="pf-num">{{ f.amount | money: f.currency }}</td>
                    <td>{{ f.reason ?? '—' }}</td>
                  </tr>
                }
              </tbody>
            </table>
          } @else if (!r.reversal) {
            <p class="pf-muted">No refunds.</p>
          }
        }
      </pf-state>
    </section>
  `,
})
export class PaymentDetailPage implements OnInit {
  private readonly api = inject(Api);
  private readonly auth = inject(Auth);
  private readonly dialog = inject(MatDialog);
  readonly merchantId = input.required<string>();
  readonly paymentId = input.required<string>();

  protected readonly detail = loadable(() => this.api.payment(this.merchantId(), this.paymentId()));
  protected readonly refunds = loadable(() => this.api.refunds(this.merchantId(), this.paymentId()));
  private readonly merchant = loadable(() => this.api.merchant(this.merchantId()));
  protected readonly refundedNotice = signal<string | null>(null);

  protected readonly canRefund = computed(() => {
    const payment = this.detail.value()?.payment;
    if (!payment || !this.auth.canRefund(this.merchantId())) {
      return false;
    }
    const refundable = toMinorUnits(payment.refundableAmount, payment.currency) ?? 0n;
    return ['Succeeded', 'PartiallyRefunded'].includes(payment.status) && refundable > 0n;
  });

  ngOnInit(): void {
    void this.detail.load();
    void this.refunds.load();
    if (this.auth.roleFor(this.merchantId()) === 'MerchantOperator') {
      void this.merchant.load(); // for the operator's per-refund limit
    }
  }

  protected openRefund(): void {
    const payment = this.detail.value()!.payment;
    const isOperator = this.auth.roleFor(this.merchantId()) === 'MerchantOperator';
    const data: RefundDialogData = {
      merchantId: this.merchantId(),
      paymentId: payment.id,
      currency: payment.currency,
      refundable: payment.refundableAmount,
      operatorLimit: isOperator ? (this.merchant.value()?.currencies.find((c) => c.currency === payment.currency)?.operatorRefundLimit ?? null) : null,
    };
    this.dialog
      .open(RefundDialog, { data, width: '480px', autoFocus: 'first-tabbable', restoreFocus: true })
      .afterClosed()
      .subscribe((refund) => {
        if (refund) {
          this.refundedNotice.set(`Refund ${refund.id} created (${refund.status}). Pending refunds complete within a few seconds.`);
          void this.detail.load();
          void this.refunds.load();
        }
      });
  }
}
