import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { axeViolations, settle } from '../../../testing/axe';
import { Page, PaymentRow } from '../../core/models';
import { PaymentsPage } from './payments';

const MERCHANT = 'mer_01testmerchant';

function row(overrides: Partial<PaymentRow> = {}): PaymentRow {
  return {
    id: 'pay_01a',
    merchantId: MERCHANT,
    merchantReference: 'order-1',
    amount: '1250.5',
    currency: 'ZAR',
    status: 'Succeeded',
    fee: '38.26',
    failureCode: null,
    createdAt: '2026-11-18T09:12:09Z',
    ...overrides,
  };
}

describe('PaymentsPage', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])] });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  async function render() {
    const fixture = TestBed.createComponent(PaymentsPage);
    fixture.componentRef.setInput('merchantId', MERCHANT);
    fixture.detectChanges();
    await settle(fixture);
    return fixture;
  }

  function respond(body: Page<PaymentRow> | null, status = 200) {
    const req = http.expectOne((r) => r.url === `/api/merchants/${MERCHANT}/payments`);
    if (status === 200) {
      req.flush(body);
    } else {
      req.flush({ title: 'Server error', detail: 'The database is unavailable.', code: 'internal_error', correlationId: 'corr-1' }, { status, statusText: 'Error' });
    }
    return req;
  }

  it('shows a loading state, then the transactions with formatted money', async () => {
    const fixture = await render();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[role="status"]')?.textContent).toContain('Loading transactions');

    const req = respond({ items: [row(), row({ id: 'pay_01b', status: 'Declined', amount: '10', fee: null, failureCode: 'insufficient_funds' })], page: 1, pageSize: 25, total: 2 });
    expect(req.request.params.get('page')).toBe('1');
    expect(req.request.params.get('pageSize')).toBe('25');
    expect(req.request.params.has('status')).toBe(false); // empty filters are not sent
    await settle(fixture);

    const rows = el.querySelectorAll('tbody tr');
    expect(rows.length).toBe(2);
    expect(rows[0].textContent).toContain('ZAR 1,250.50');
    expect(rows[0].textContent).toContain('ZAR 38.26');
    expect(rows[0].textContent).toContain('2026-11-18 09:12:09 UTC');
    expect(rows[1].textContent).toContain('Declined');
    expect(rows[1].textContent).toContain('insufficient_funds');
    expect(el.textContent).toContain('2 transactions');
  });

  it('shows an empty state when nothing matches', async () => {
    const fixture = await render();
    respond({ items: [], page: 1, pageSize: 25, total: 0 });
    await settle(fixture);

    const el = fixture.nativeElement as HTMLElement;
    expect(el.textContent).toContain('No transactions match these filters.');
    expect(el.querySelector('table')).toBeNull();
  });

  it('shows the error with a retry that reloads', async () => {
    const fixture = await render();
    respond(null, 500);
    await settle(fixture);

    const el = fixture.nativeElement as HTMLElement;
    const alert = el.querySelector('[role="alert"]')!;
    expect(alert.textContent).toContain('The database is unavailable.');
    expect(alert.textContent).toContain('corr-1');

    (alert.querySelector('button') as HTMLButtonElement).click();
    await settle(fixture);
    respond({ items: [row()], page: 1, pageSize: 25, total: 1 });
    await settle(fixture);
    expect(el.querySelectorAll('tbody tr').length).toBe(1);
  });

  it('sends filters, with the "to" day made inclusive in UTC', async () => {
    const fixture = await render();
    respond({ items: [], page: 1, pageSize: 25, total: 0 });
    await settle(fixture);

    const page = fixture.componentInstance as unknown as { status: string; reference: string; from: string; to: string; apply(): void };
    page.status = 'Declined';
    page.reference = '  order-7 ';
    page.from = '2026-11-01';
    page.to = '2026-11-18';
    page.apply();
    await settle(fixture);

    const req = respond({ items: [], page: 1, pageSize: 25, total: 0 });
    expect(req.request.params.get('status')).toBe('Declined');
    expect(req.request.params.get('reference')).toBe('order-7');
    expect(req.request.params.get('from')).toBe('2026-11-01T00:00:00Z');
    expect(req.request.params.get('to')).toBe('2026-11-19T00:00:00.000Z');
  });

  it('has no axe accessibility violations with data', async () => {
    const fixture = await render();
    respond({ items: [row()], page: 1, pageSize: 25, total: 1 });
    await settle(fixture);
    expect(await axeViolations(fixture.nativeElement)).toEqual([]);
  });
});
