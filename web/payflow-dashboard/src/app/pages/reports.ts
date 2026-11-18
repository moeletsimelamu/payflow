import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import type { EChartsCoreOption } from 'echarts/core';
import { Api } from '../core/api';
import { loadable } from '../core/loadable';
import { Report } from '../core/models';
import { SUPPORTED_CURRENCIES, formatMoney } from '../core/money';
import { Chart } from '../shared/chart';
import { MoneyPipe, RatePipe, UtcPipe, WordsPipe } from '../shared/pipes';
import { StateView } from '../shared/state';

/** Volume chart options. Amounts stay strings until ECharts needs a plotting value. */
export function volumeChart(report: Report): EChartsCoreOption {
  return {
    grid: { left: 8, right: 8, top: 56, bottom: 8, containLabel: true },
    tooltip: { trigger: 'axis' },
    legend: { top: 0, data: ['Payments', 'Succeeded value'] },
    xAxis: { type: 'category', data: report.volume.days.map((d) => d.date.slice(5)) },
    yAxis: [
      { type: 'value', name: 'Payments', minInterval: 1 },
      { type: 'value', name: report.currency },
    ],
    series: [
      { name: 'Payments', type: 'bar', data: report.volume.days.map((d) => d.payments) },
      // Plotting only: chart geometry needs numbers; every displayed figure uses formatMoney.
      { name: 'Succeeded value', type: 'line', yAxisIndex: 1, data: report.volume.days.map((d) => Number(d.succeededAmount)) },
    ],
  };
}

export function outcomeChart(report: Report): EChartsCoreOption {
  const o = report.outcomes;
  return {
    tooltip: { trigger: 'item' },
    legend: { bottom: 0 },
    series: [
      {
        type: 'pie',
        radius: ['40%', '70%'],
        top: 0,
        bottom: 32,
        // The legend and tooltip name each slice; inline labels get truncated in narrow cards.
        label: { show: false },
        data: [
          { name: 'Succeeded', value: o.succeeded },
          { name: 'Reversed', value: o.reversed },
          { name: 'Declined', value: o.declined },
          { name: 'Failed', value: o.failed },
          { name: 'Expired', value: o.expired },
        ].filter((d) => d.value > 0),
      },
    ],
  };
}

@Component({
  selector: 'pf-reports',
  imports: [FormsModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatButtonModule, StateView, Chart, MoneyPipe, RatePipe, UtcPipe, WordsPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="pf-page-header">
      <h1>{{ merchantId() ? 'Reports' : 'Platform reports' }}</h1>
      <p class="pf-muted">Figures are per currency and refreshed at most once a minute.</p>
    </header>

    <form class="pf-filters" (ngSubmit)="report.load()" aria-label="Report period">
      <mat-form-field appearance="outline">
        <mat-label>Currency</mat-label>
        <mat-select name="currency" [(ngModel)]="currency">
          @for (c of currencies; track c) {
            <mat-option [value]="c">{{ c }}</mat-option>
          }
        </mat-select>
      </mat-form-field>
      <mat-form-field appearance="outline">
        <mat-label>From</mat-label>
        <input matInput type="date" name="from" [(ngModel)]="from" />
      </mat-form-field>
      <mat-form-field appearance="outline">
        <mat-label>To</mat-label>
        <input matInput type="date" name="to" [(ngModel)]="to" />
      </mat-form-field>
      <button mat-stroked-button type="submit">Update</button>
    </form>

    <pf-state [of]="report" loadingText="Calculating report…">
      @if (report.value(); as r) {
        <p class="pf-muted">{{ r.from }} to {{ r.to }} · generated {{ r.generatedAt | utc }}</p>

        <section class="pf-kpis" aria-label="Key figures">
          <div class="pf-kpi"><span class="pf-kpi-label">Payments</span><span class="pf-kpi-value">{{ r.volume.payments }}</span></div>
          <div class="pf-kpi"><span class="pf-kpi-label">Succeeded value</span><span class="pf-kpi-value">{{ r.volume.succeededAmount | money: r.currency }}</span></div>
          <div class="pf-kpi"><span class="pf-kpi-label">Success rate</span><span class="pf-kpi-value">{{ r.outcomes.successRate | rate }}</span></div>
          <div class="pf-kpi"><span class="pf-kpi-label">Refunded</span><span class="pf-kpi-value">{{ r.refunds.amount | money: r.currency }}</span><span class="pf-muted">{{ r.refunds.refundRate | rate }} of value</span></div>
          <div class="pf-kpi"><span class="pf-kpi-label">Paid out</span><span class="pf-kpi-value">{{ r.settlements.paidOut | money: r.currency }}</span><span class="pf-muted">{{ r.settlements.awaitingPayout | money: r.currency }} awaiting</span></div>
        </section>

        <div class="pf-grid-2">
          <pf-chart title="Daily volume (RPT-1)" [summary]="volumeSummary()" [options]="volumeOptions()">
            <table class="pf-table">
              <caption class="pf-visually-hidden">Payments and succeeded value per day</caption>
              <thead><tr><th scope="col">Day</th><th scope="col" class="pf-num">Payments</th><th scope="col" class="pf-num">Succeeded</th><th scope="col" class="pf-num">Succeeded value</th></tr></thead>
              <tbody>
                @for (d of r.volume.days; track d.date) {
                  <tr><td>{{ d.date }}</td><td class="pf-num">{{ d.payments }}</td><td class="pf-num">{{ d.succeeded }}</td><td class="pf-num">{{ d.succeededAmount | money: r.currency }}</td></tr>
                }
              </tbody>
            </table>
          </pf-chart>

          <pf-chart title="Outcomes (RPT-2)" [summary]="outcomeSummary()" [options]="outcomeOptions()">
            <table class="pf-table">
              <caption class="pf-visually-hidden">Payment outcomes</caption>
              <tbody>
                <tr><th scope="row">Succeeded</th><td class="pf-num">{{ r.outcomes.succeeded }}</td></tr>
                <tr><th scope="row">Reversed</th><td class="pf-num">{{ r.outcomes.reversed }}</td></tr>
                <tr><th scope="row">Declined</th><td class="pf-num">{{ r.outcomes.declined }}</td></tr>
                <tr><th scope="row">Failed</th><td class="pf-num">{{ r.outcomes.failed }}</td></tr>
                <tr><th scope="row">Expired</th><td class="pf-num">{{ r.outcomes.expired }}</td></tr>
                <tr><th scope="row">Still in flight</th><td class="pf-num">{{ r.outcomes.inFlight }}</td></tr>
              </tbody>
            </table>
          </pf-chart>
        </div>

        <div class="pf-grid-2">
          <section class="pf-card" aria-labelledby="rates">
            <h2 id="rates">Rates and failure codes</h2>
            <dl class="pf-dl">
              <dt>Success</dt><dd>{{ r.outcomes.successRate | rate }}</dd>
              <dt>Decline</dt><dd>{{ r.outcomes.declineRate | rate }}</dd>
              <dt>Failure (incl. expired)</dt><dd>{{ r.outcomes.failureRate | rate }}</dd>
            </dl>
            @if (r.outcomes.topFailureCodes.length) {
              <table class="pf-table">
                <caption>Top failure codes</caption>
                <thead><tr><th scope="col">Code</th><th scope="col" class="pf-num">Payments</th></tr></thead>
                <tbody>
                  @for (c of r.outcomes.topFailureCodes; track c.code) {
                    <tr><td><code>{{ c.code }}</code></td><td class="pf-num">{{ c.count }}</td></tr>
                  }
                </tbody>
              </table>
            } @else {
              <p class="pf-muted">No failed or declined payments in this period.</p>
            }
          </section>

          <section class="pf-card" aria-labelledby="perf">
            <h2 id="perf">Processing and webhooks (RPT-6)</h2>
            <dl class="pf-dl">
              <dt>Payments processed synchronously</dt><dd>{{ r.performance.processedPayments }}</dd>
              <dt>Processing p50 / p95</dt><dd>{{ ms(r.performance.processingP50Ms) }} / {{ ms(r.performance.processingP95Ms) }}</dd>
              <dt>Webhook attempts</dt><dd>{{ r.performance.webhookAttempts }}</dd>
              <dt>Webhook success rate</dt><dd>{{ r.performance.webhookAttempts ? (r.performance.webhookSuccessRate | rate) : '—' }}</dd>
              <dt>Webhook latency p50 / p95</dt><dd>{{ ms(r.performance.webhookP50Ms) }} / {{ ms(r.performance.webhookP95Ms) }}</dd>
            </dl>
          </section>
        </div>

        <div class="pf-grid-2">
          <section class="pf-card" aria-labelledby="settle">
            <h2 id="settle">Settlement (RPT-4) and refunds (RPT-3)</h2>
            <dl class="pf-dl">
              <dt>Batches created</dt><dd>{{ r.settlements.batches }} ({{ r.settlements.paid }} paid, {{ r.settlements.pending }} pending, {{ r.settlements.failed }} failed)</dd>
              <dt>Paid out</dt><dd>{{ r.settlements.paidOut | money: r.currency }}</dd>
              <dt>Awaiting payout</dt><dd>{{ r.settlements.awaitingPayout | money: r.currency }}</dd>
              <dt>Deficit carried forward</dt><dd>{{ r.settlements.carriedForward | money: r.currency }}</dd>
              <dt>Refunds succeeded</dt><dd>{{ r.refunds.count }} · {{ r.refunds.amount | money: r.currency }}</dd>
              <dt>Refunds failed</dt><dd>{{ r.refunds.failed }}</dd>
            </dl>
          </section>

          @if (r.reconciliation; as rec) {
            <section class="pf-card" aria-labelledby="rec">
              <h2 id="rec">Reconciliation backlog (RPT-5)</h2>
              <p>{{ rec.unresolved }} unresolved exception{{ rec.unresolved === 1 ? '' : 's' }}, all currencies.</p>
              <table class="pf-table">
                <caption>By type</caption>
                <tbody>
                  @for (o of rec.byOutcome; track o.outcome) {
                    <tr><th scope="row">{{ o.outcome | words }}</th><td class="pf-num">{{ o.open }}</td></tr>
                  } @empty {
                    <tr><td>None open.</td></tr>
                  }
                </tbody>
              </table>
              <table class="pf-table">
                <caption>By age</caption>
                <tbody>
                  @for (b of rec.byAge; track b.label) {
                    <tr><th scope="row">{{ b.label }}</th><td class="pf-num">{{ b.open }}</td></tr>
                  }
                </tbody>
              </table>
            </section>
          }
        </div>
      }
    </pf-state>
  `,
})
export class ReportsPage implements OnInit {
  private readonly api = inject(Api);
  /** Set from the route for a merchant's own reports; absent for the platform view. */
  readonly merchantId = input<string | undefined>();

  protected readonly currencies = SUPPORTED_CURRENCIES;
  protected currency = 'ZAR';
  protected from = isoDay(-29);
  protected to = isoDay(0);

  protected readonly report = loadable(() =>
    this.api.report(
      { merchantId: this.merchantId() ?? null, asPlatform: !this.merchantId() },
      { currency: this.currency, from: this.from, to: this.to },
    ),
  );

  protected readonly volumeOptions = computed(() => (this.report.value() ? volumeChart(this.report.value()!) : {}));
  protected readonly outcomeOptions = computed(() => (this.report.value() ? outcomeChart(this.report.value()!) : {}));
  protected readonly volumeSummary = computed(() => {
    const r = this.report.value();
    return r ? `${r.volume.payments} payments from ${r.from} to ${r.to}, ${formatMoney(r.volume.succeededAmount, r.currency)} succeeded.` : '';
  });
  protected readonly outcomeSummary = computed(() => {
    const o = this.report.value()?.outcomes;
    return o ? `${o.succeeded} succeeded, ${o.reversed} reversed, ${o.declined} declined, ${o.failed} failed, ${o.expired} expired.` : '';
  });

  ngOnInit(): void {
    void this.report.load();
  }

  protected ms(value: number | null): string {
    return value === null ? '—' : `${value} ms`;
  }
}

function isoDay(offset: number): string {
  const d = new Date();
  d.setUTCDate(d.getUTCDate() + offset);
  return d.toISOString().slice(0, 10);
}
