import { ChangeDetectionStrategy, Component, OnInit, inject, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Api } from '../../core/api';
import { isEmptyPage, loadable } from '../../core/loadable';
import { MoneyPipe, UtcPipe } from '../../shared/pipes';
import { StateView } from '../../shared/state';
import { StatusBadge } from '../../shared/status-badge';

/** The merchant's settlement history (SET-5). */
@Component({
  selector: 'pf-merchant-settlements',
  imports: [RouterLink, StateView, StatusBadge, MoneyPipe, UtcPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="pf-page-header">
      <h1>Settlements</h1>
      <p class="pf-muted">Each batch pays out what PayFlow owes for transactions completed before its cut-off. Payouts are simulated.</p>
    </header>
    <pf-state [of]="list" loadingText="Loading settlements…" emptyText="No settlement batches yet.">
      @if (list.value(); as page) {
        <div class="pf-table-wrap">
          <table class="pf-table">
            <caption class="pf-visually-hidden">Settlement batches</caption>
            <thead>
              <tr>
                <th scope="col">Cut-off</th><th scope="col">Status</th><th scope="col" class="pf-num">Payments</th><th scope="col" class="pf-num">Refunds</th>
                <th scope="col" class="pf-num">Net</th><th scope="col" class="pf-num">Payout</th><th scope="col" class="pf-num">Carried forward</th>
              </tr>
            </thead>
            <tbody>
              @for (b of page.items; track b.id) {
                <tr>
                  <td><a [routerLink]="[b.id]">{{ b.cutoffAt | utc: false }}</a></td>
                  <td><pf-status [value]="b.status" /></td>
                  <td class="pf-num">{{ b.paymentCount }}</td>
                  <td class="pf-num">{{ b.refundCount }}</td>
                  <td class="pf-num">{{ b.netAmount | money: b.currency }}</td>
                  <td class="pf-num">{{ b.payoutAmount | money: b.currency }}</td>
                  <td class="pf-num">{{ b.carriedForwardOut | money: b.currency }}</td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      }
    </pf-state>
  `,
})
export class MerchantSettlementsPage implements OnInit {
  private readonly api = inject(Api);
  readonly merchantId = input.required<string>();
  protected readonly list = loadable(() => this.api.merchantSettlements(this.merchantId(), { page: 1 }), isEmptyPage);

  ngOnInit(): void {
    void this.list.load();
  }
}

/** One batch with its line items and the CSV export. */
@Component({
  selector: 'pf-settlement-detail',
  imports: [RouterLink, StateView, StatusBadge, MoneyPipe, UtcPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <p><a [routerLink]="['/merchants', merchantId(), 'settlements']">← Settlements</a></p>
    <pf-state [of]="batch" loadingText="Loading batch…">
      @if (batch.value(); as b) {
        <header class="pf-page-header">
          <h1>Payout {{ b.payoutAmount | money: b.currency }} <pf-status [value]="b.status" /></h1>
          <p class="pf-muted"><code>{{ b.id }}</code> · cut-off {{ b.cutoffAt | utc }} · simulated</p>
          <a class="pf-button-link" [href]="csvUrl()" download>Download line items (CSV)</a>
        </header>
        <section class="pf-card" aria-labelledby="calc">
          <h2 id="calc">How the payout was calculated</h2>
          <table class="pf-table pf-calc">
            <caption class="pf-visually-hidden">Net amount calculation</caption>
            <tbody>
              <tr><th scope="row">Gross payments ({{ b.paymentCount }})</th><td class="pf-num">{{ b.grossPayments | money: b.currency }}</td></tr>
              <tr><th scope="row">Fees</th><td class="pf-num">−{{ b.totalFees | money: b.currency }}</td></tr>
              <tr><th scope="row">Refunds ({{ b.refundCount }})</th><td class="pf-num">−{{ b.totalRefunds | money: b.currency }}</td></tr>
              <tr><th scope="row">Deficit carried in</th><td class="pf-num">{{ b.carriedForwardIn | money: b.currency }}</td></tr>
              <tr class="pf-total"><th scope="row">Net</th><td class="pf-num">{{ b.netAmount | money: b.currency }}</td></tr>
              <tr><th scope="row">Payout</th><td class="pf-num">{{ b.payoutAmount | money: b.currency }}</td></tr>
              <tr><th scope="row">Deficit carried forward</th><td class="pf-num">{{ b.carriedForwardOut | money: b.currency }}</td></tr>
            </tbody>
          </table>
          @if (b.paidAt) {
            <p>Paid {{ b.paidAt | utc }}, reference <code>{{ b.payoutReference }}</code>.</p>
          }
          @if (b.failureReason) {
            <p class="pf-error">Last payout attempt failed: {{ b.failureReason }}</p>
          }
        </section>
        <section class="pf-card" aria-labelledby="items">
          <h2 id="items">Line items</h2>
          <div class="pf-table-wrap">
            <table class="pf-table">
              <caption class="pf-visually-hidden">Payments and refunds in this batch</caption>
              <thead><tr><th scope="col">Completed</th><th scope="col">Type</th><th scope="col">Transaction</th><th scope="col" class="pf-num">Gross</th><th scope="col" class="pf-num">Fee</th><th scope="col" class="pf-num">Net</th></tr></thead>
              <tbody>
                @for (i of b.items ?? []; track i.sourceId) {
                  <tr>
                    <td>{{ i.completedAt | utc }}</td>
                    <td>{{ i.type }}</td>
                    <td><a [routerLink]="['/merchants', merchantId(), 'payments', i.paymentId]"><code>{{ i.sourceId }}</code></a></td>
                    <td class="pf-num">{{ i.gross | money: b.currency }}</td>
                    <td class="pf-num">{{ i.fee | money: b.currency }}</td>
                    <td class="pf-num">{{ i.net | money: b.currency }}</td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        </section>
      }
    </pf-state>
  `,
})
export class SettlementDetailPage implements OnInit {
  private readonly api = inject(Api);
  readonly merchantId = input.required<string>();
  readonly settlementId = input.required<string>();
  protected readonly batch = loadable(() => this.api.merchantSettlement(this.merchantId(), this.settlementId()));
  protected csvUrl(): string {
    return this.api.settlementCsvUrl(this.merchantId(), this.settlementId());
  }

  ngOnInit(): void {
    void this.batch.load();
  }
}
