import { ChangeDetectionStrategy, Component, OnInit, inject, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Api } from '../../core/api';
import { isEmptyPage, loadable } from '../../core/loadable';
import { MoneyPipe, RatePipe, UtcPipe } from '../../shared/pipes';
import { StateView } from '../../shared/state';
import { StatusBadge } from '../../shared/status-badge';

/** The merchant's landing page: profile, last 30 days at a glance, recent payments and settlements. */
@Component({
  selector: 'pf-merchant-overview',
  imports: [RouterLink, StateView, StatusBadge, MoneyPipe, RatePipe, UtcPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <pf-state [of]="merchant" loadingText="Loading merchant…">
      @if (merchant.value(); as m) {
        <header class="pf-page-header">
          <h1>{{ m.tradingName }}</h1>
          <p>
            <pf-status [value]="m.status" />
            <span class="pf-muted">{{ m.legalName }} · {{ m.countryCode }} · {{ m.contactEmail }}</span>
          </p>
          @if (m.statusReason) {
            <p class="pf-notice">{{ m.statusReason }}</p>
          }
        </header>
      }
    </pf-state>

    <section aria-labelledby="last30">
      <h2 id="last30">Last 30 days ({{ currency }})</h2>
      <pf-state [of]="report" loadingText="Calculating…">
        @if (report.value(); as r) {
          <div class="pf-kpis">
            <div class="pf-kpi"><span class="pf-kpi-label">Payments</span><span class="pf-kpi-value">{{ r.volume.payments }}</span></div>
            <div class="pf-kpi"><span class="pf-kpi-label">Succeeded value</span><span class="pf-kpi-value">{{ r.volume.succeededAmount | money: r.currency }}</span></div>
            <div class="pf-kpi"><span class="pf-kpi-label">Success rate</span><span class="pf-kpi-value">{{ r.outcomes.successRate | rate }}</span></div>
            <div class="pf-kpi"><span class="pf-kpi-label">Refunded</span><span class="pf-kpi-value">{{ r.refunds.amount | money: r.currency }}</span></div>
            <div class="pf-kpi"><span class="pf-kpi-label">Awaiting payout</span><span class="pf-kpi-value">{{ r.settlements.awaitingPayout | money: r.currency }}</span></div>
          </div>
          <p><a [routerLink]="['/merchants', merchantId(), 'reports']">Full reports</a></p>
        }
      </pf-state>
    </section>

    <div class="pf-grid-2">
      <section class="pf-card" aria-labelledby="recent">
        <h2 id="recent">Recent payments</h2>
        <pf-state [of]="recent" emptyText="No payments yet. Payments made with this merchant's API keys appear here.">
          @if (recent.value(); as page) {
            <table class="pf-table">
              <caption class="pf-visually-hidden">The 5 most recent payments</caption>
              <thead><tr><th scope="col">Payment</th><th scope="col">Status</th><th scope="col" class="pf-num">Amount</th></tr></thead>
              <tbody>
                @for (p of page.items; track p.id) {
                  <tr>
                    <td><a [routerLink]="['/merchants', merchantId(), 'payments', p.id]">{{ p.merchantReference }}</a><br /><span class="pf-muted">{{ p.createdAt | utc }}</span></td>
                    <td><pf-status [value]="p.status" /></td>
                    <td class="pf-num">{{ p.amount | money: p.currency }}</td>
                  </tr>
                }
              </tbody>
            </table>
            <p><a [routerLink]="['/merchants', merchantId(), 'payments']">All transactions</a></p>
          }
        </pf-state>
      </section>

      <section class="pf-card" aria-labelledby="payouts">
        <h2 id="payouts">Recent settlements</h2>
        <pf-state [of]="settlements" emptyText="No settlement batches yet. Settlement runs daily for the previous day's transactions.">
          @if (settlements.value(); as page) {
            <table class="pf-table">
              <caption class="pf-visually-hidden">The most recent settlement batches</caption>
              <thead><tr><th scope="col">Batch</th><th scope="col">Status</th><th scope="col" class="pf-num">Payout</th></tr></thead>
              <tbody>
                @for (b of page.items.slice(0, 5); track b.id) {
                  <tr>
                    <td><a [routerLink]="['/merchants', merchantId(), 'settlements', b.id]">{{ b.createdAt | utc: false }}</a></td>
                    <td><pf-status [value]="b.status" /></td>
                    <td class="pf-num">{{ b.payoutAmount | money: b.currency }}</td>
                  </tr>
                }
              </tbody>
            </table>
          }
        </pf-state>
      </section>
    </div>
  `,
})
export class MerchantOverviewPage implements OnInit {
  private readonly api = inject(Api);
  readonly merchantId = input.required<string>();
  protected readonly currency = 'ZAR';

  protected readonly merchant = loadable(() => this.api.merchant(this.merchantId()));
  protected readonly report = loadable(() => this.api.report({ merchantId: this.merchantId(), asPlatform: false }, { currency: this.currency }));
  protected readonly recent = loadable(() => this.api.payments(this.merchantId(), { page: 1, pageSize: 5 }), isEmptyPage);
  protected readonly settlements = loadable(() => this.api.merchantSettlements(this.merchantId(), { page: 1 }), isEmptyPage);

  ngOnInit(): void {
    for (const l of [this.merchant, this.report, this.recent, this.settlements]) {
      void l.load();
    }
  }
}
