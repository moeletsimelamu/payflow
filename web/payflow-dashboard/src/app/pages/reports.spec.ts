import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { axeViolations, settle } from '../../testing/axe';
import { Report } from '../core/models';
import { Chart } from '../shared/chart';
import { ReportsPage, outcomeChart, volumeChart } from './reports';

function report(overrides: Partial<Report> = {}): Report {
  return {
    merchantId: 'mer_1',
    currency: 'ZAR',
    from: '2026-11-17',
    to: '2026-11-18',
    generatedAt: '2026-11-18T10:00:00Z',
    volume: {
      payments: 7,
      succeeded: 4,
      succeededAmount: '411.99',
      days: [
        { date: '2026-11-17', payments: 0, succeeded: 0, succeededAmount: '0.00' },
        { date: '2026-11-18', payments: 7, succeeded: 4, succeededAmount: '411.99' },
      ],
    },
    outcomes: { succeeded: 3, reversed: 1, declined: 2, failed: 1, expired: 0, inFlight: 0, successRate: 0.5714, declineRate: 0.2857, failureRate: 0.1429, topFailureCodes: [{ code: 'insufficient_funds', count: 2 }] },
    refunds: { count: 1, amount: '40.00', failed: 0, refundRate: 0.0971 },
    settlements: { batches: 1, pending: 0, paid: 1, failed: 0, paidOut: '313.26', awaitingPayout: '0.00', carriedForward: '0.00' },
    reconciliation: null,
    performance: { processedPayments: 6, processingP50Ms: 12, processingP95Ms: 40, webhookAttempts: 0, webhookSuccessRate: 0, webhookP50Ms: null, webhookP95Ms: null },
    ...overrides,
  };
}

describe('ReportsPage', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    // jsdom has no canvas: replace the ECharts canvas with its accessible label; the table toggle stays real.
    TestBed.overrideComponent(Chart, {
      set: {
        imports: [],
        template: `<figure class="pf-chart"><figcaption><span>{{ title() }}</span><button type="button" (click)="asTable.set(!asTable())">toggle</button></figcaption>
          @if (asTable()) { <ng-content /> } @else { <div role="img" [attr.aria-label]="summary()"></div> }</figure>`,
      },
    });
    http = TestBed.inject(HttpTestingController);
  });

  async function render(merchantId?: string) {
    const fixture = TestBed.createComponent(ReportsPage);
    if (merchantId) {
      fixture.componentRef.setInput('merchantId', merchantId);
    }
    fixture.detectChanges();
    await settle(fixture);
    return fixture;
  }

  it("loads a merchant's own report and shows formatted figures", async () => {
    const fixture = await render('mer_1');
    const req = http.expectOne((r) => r.url === '/api/merchants/mer_1/reports');
    expect(req.request.params.get('currency')).toBe('ZAR');
    req.flush(report());
    await settle(fixture);

    const el = fixture.nativeElement as HTMLElement;
    expect(el.textContent).toContain('ZAR 411.99');
    expect(el.textContent).toContain('57.14%');
    expect(el.textContent).toContain('9.71% of value');
    expect(el.textContent).toContain('insufficient_funds');
    expect(el.textContent).toContain('12 ms / 40 ms');
    expect(el.querySelector('[role="img"]')?.getAttribute('aria-label')).toContain('7 payments from 2026-11-17 to 2026-11-18, ZAR 411.99 succeeded.');
    expect(el.textContent).not.toContain('Reconciliation backlog'); // platform-only section
  });

  it('uses the platform endpoint without a merchant and shows the reconciliation backlog', async () => {
    const fixture = await render();
    http.expectOne((r) => r.url === '/api/reports').flush(
      report({ merchantId: null, reconciliation: { unresolved: 3, byOutcome: [{ outcome: 'AmountMismatch', open: 3 }], byAge: [{ label: 'under 1 day', open: 3 }] } }),
    );
    await settle(fixture);
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('3 unresolved exceptions');
  });

  it('can show every chart as a table', async () => {
    const fixture = await render('mer_1');
    http.expectOne(() => true).flush(report());
    await settle(fixture);

    const el = fixture.nativeElement as HTMLElement;
    (el.querySelector('figure button') as HTMLButtonElement).click();
    await settle(fixture);
    expect(el.querySelector('figure table')?.textContent).toContain('2026-11-18');
  });

  it('has no axe accessibility violations', async () => {
    const fixture = await render('mer_1');
    http.expectOne(() => true).flush(report());
    await settle(fixture);
    expect(await axeViolations(fixture.nativeElement)).toEqual([]);
  });
});

describe('chart options', () => {
  it('plot one bar per day and leave out empty outcome slices', () => {
    const options = volumeChart(report()) as { xAxis: { data: string[] } };
    expect(options.xAxis.data).toEqual(['11-17', '11-18']);
    const pie = outcomeChart(report()) as { series: { data: { name: string }[] }[] };
    expect(pie.series[0].data.map((d) => d.name)).toEqual(['Succeeded', 'Reversed', 'Declined', 'Failed']);
  });
});
