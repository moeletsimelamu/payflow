import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { axeViolations, settle } from '../../../testing/axe';
import { RefundDialog, RefundDialogData } from './refund-dialog';

const DATA: RefundDialogData = { merchantId: 'mer_1', paymentId: 'pay_1', currency: 'ZAR', refundable: '60.00', operatorLimit: null };

describe('RefundDialog', () => {
  let http: HttpTestingController;
  let close: ReturnType<typeof vi.fn>;

  // ngModel inside a <form> connects to its input one tick after creation, so let it settle first.
  async function render(data: RefundDialogData = DATA) {
    close = vi.fn();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: MAT_DIALOG_DATA, useValue: data },
        { provide: MatDialogRef, useValue: { close } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(RefundDialog);
    fixture.detectChanges();
    await settle(fixture);
    return fixture;
  }

  async function type(fixture: Awaited<ReturnType<typeof render>>, value: string) {
    const input = (fixture.nativeElement as HTMLElement).querySelector('input[name="amount"]') as HTMLInputElement;
    input.value = value;
    input.dispatchEvent(new Event('input'));
    await settle(fixture);
  }

  it('starts at the full refundable amount and explains each invalid amount', async () => {
    const fixture = await render();
    const el = fixture.nativeElement as HTMLElement;
    expect((el.querySelector('input[name="amount"]') as HTMLInputElement).value).toBe('60.00');
    expect(el.textContent).toContain('ZAR 60.00');

    for (const [value, message] of [
      ['60.01', 'At most ZAR 60.00 can still be refunded.'],
      ['10.001', 'ZAR amounts have at most 2 decimal places.'],
      ['0', 'more than zero'],
      ['ten', 'Use digits'],
      ['', 'Enter the amount'],
    ]) {
      await type(fixture, value);
      expect(el.querySelector('[role="alert"]')?.textContent).toContain(message);
    }

    await fixture.componentInstance.submit();
    http.expectNone(() => true); // nothing is sent while the amount is invalid
  });

  it('uses one idempotency key for every attempt, so a retry cannot refund twice', async () => {
    const fixture = await render();
    await type(fixture, '40');

    void fixture.componentInstance.submit();
    const first = http.expectOne('/api/merchants/mer_1/payments/pay_1/refunds');
    expect(first.request.method).toBe('POST');
    expect(first.request.body).toEqual({ amount: '40', reason: null });
    const key = first.request.headers.get('Idempotency-Key');
    expect(key).toBe(fixture.componentInstance.idempotencyKey);
    first.error(new ProgressEvent('error'), { status: 0 }); // network failure: did it go through? unknown
    await settle(fixture);
    expect((fixture.nativeElement as HTMLElement).querySelector('[role="alert"]')?.textContent).toContain('could not be reached');
    expect(close).not.toHaveBeenCalled();

    void fixture.componentInstance.submit();
    const retry = http.expectOne('/api/merchants/mer_1/payments/pay_1/refunds');
    expect(retry.request.headers.get('Idempotency-Key')).toBe(key);
    retry.flush({ id: 're_1', status: 'Pending', amount: '40.00', currency: 'ZAR' });
    await settle(fixture);
    expect(close).toHaveBeenCalledWith(expect.objectContaining({ id: 're_1' }));
  });

  it('turns API refusals into plain explanations', async () => {
    const fixture = await render({ ...DATA, operatorLimit: '20.00' });
    const el = fixture.nativeElement as HTMLElement;
    expect(el.textContent).toContain('Operators may refund up to ZAR 20.00');

    void fixture.componentInstance.submit();
    http.expectOne(() => true).flush({ code: 'operator_refund_limit_exceeded', detail: 'Over limit.' }, { status: 422, statusText: 'Unprocessable' });
    await settle(fixture);
    expect(el.querySelector('[role="alert"]')?.textContent).toContain('Operators may refund at most ZAR 20.00 at a time');
  });

  it('formats a zero-decimal currency correctly', async () => {
    const fixture = await render({ ...DATA, currency: 'JPY', refundable: '5000' });
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('JPY 5,000');
  });

  it('has no axe accessibility violations', async () => {
    const fixture = await render();
    await settle(fixture);
    expect(await axeViolations(fixture.nativeElement)).toEqual([]);
  });
});
