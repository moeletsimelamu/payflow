import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { axeViolations, settle } from '../../../testing/axe';
import { RegisterMerchantPage } from './register-merchant';
import { statusActions } from './merchant-admin-panel';

describe('RegisterMerchantPage', () => {
  let http: HttpTestingController;

  async function render() {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])] });
    http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(RegisterMerchantPage);
    fixture.detectChanges();
    http.expectOne('/api/fee-plans').flush([{ id: 'fp_1', name: 'Standard', version: 1, percentageRate: '0.029', terms: [], createdAt: '2026-11-01T00:00:00Z' }]);
    await settle(fixture);
    return fixture;
  }

  function set(el: HTMLElement, selector: string, value: string) {
    const input = el.querySelector(selector) as HTMLInputElement;
    input.value = value;
    input.dispatchEvent(new Event('input'));
  }

  async function fillAndSubmit(fixture: Awaited<ReturnType<typeof render>>, overrides: Record<string, string> = {}) {
    const el = fixture.nativeElement as HTMLElement;
    const values = { legalName: 'Karoo Coffee (Pty) Ltd', tradingName: 'Karoo Coffee', contactEmail: 'ops@karoo.test', adminEmail: 'owner@karoo.test', adminDisplayName: 'Thandi', ...overrides };
    for (const [name, value] of Object.entries(values)) {
      set(el, `input[formcontrolname="${name}"]`, value);
    }
    (el.querySelector('form') as HTMLFormElement).dispatchEvent(new Event('submit'));
    await settle(fixture);
  }

  it('posts the merchant with trimmed values and its currency limits', async () => {
    const fixture = await render();
    await fillAndSubmit(fixture, { tradingName: '  Karoo Coffee  ' });

    const req = http.expectOne('/api/merchants');
    expect(req.request.body).toEqual({
      legalName: 'Karoo Coffee (Pty) Ltd',
      tradingName: 'Karoo Coffee',
      contactEmail: 'ops@karoo.test',
      countryCode: 'ZA',
      feePlanId: null,
      currencies: [{ currency: 'ZAR', maxPaymentAmount: '50000.00', operatorRefundLimit: '1000.00' }],
      adminEmail: 'owner@karoo.test',
      adminDisplayName: 'Thandi',
    });
    req.flush({
      merchant: { id: 'mer_1', tradingName: 'Karoo Coffee', status: 'PendingActivation' },
      adminInvitation: { userId: 'usr_1', email: 'owner@karoo.test', role: 'MerchantAdmin', invitationToken: 'tok-123' },
    });
    await settle(fixture);

    const el = fixture.nativeElement as HTMLElement;
    expect(el.textContent).toContain('Karoo Coffee is registered');
    // The one-time token travels in the fragment, which browsers never send to a server.
    const link = el.querySelector('.pf-secret code')?.textContent ?? '';
    expect(link).toMatch(/\/accept-invitation#email=owner%40karoo\.test&token=tok-123$/);
    expect(link).not.toContain('?');

    (Array.from(el.querySelectorAll('button')).find((b) => b.textContent?.includes('I have stored it')) as HTMLButtonElement).click();
    await settle(fixture);
    expect(el.textContent).not.toContain('tok-123'); // dismissed means gone
  });

  it('refuses amounts that do not fit the currency, without calling the API', async () => {
    const fixture = await render();
    const el = fixture.nativeElement as HTMLElement;
    set(el, 'input[formcontrolname="maxPaymentAmount"]', '100.001');
    await fillAndSubmit(fixture);

    http.expectNone('/api/merchants');
    expect(el.textContent).toContain('Max payment must be a positive ZAR amount.');
    expect(el.textContent).toContain('Check the highlighted fields.');
  });

  it('shows the API validation errors field by field', async () => {
    const fixture = await render();
    await fillAndSubmit(fixture);
    http.expectOne('/api/merchants').flush(
      { code: 'validation_failed', detail: 'The request is invalid.', errors: { contactEmail: ['A merchant with this contact email exists.'] } },
      { status: 400, statusText: 'Bad Request' },
    );
    await settle(fixture);
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('contactEmail: A merchant with this contact email exists.');
  });

  it('has no axe accessibility violations', async () => {
    const fixture = await render();
    expect(await axeViolations(fixture.nativeElement)).toEqual([]);
  });
});

describe('statusActions', () => {
  it('offers only the transitions the API allows', () => {
    expect(statusActions('PendingActivation')).toEqual(['Active', 'Closed']);
    expect(statusActions('Active')).toEqual(['Suspended', 'Closed']);
    expect(statusActions('Suspended')).toEqual(['Active', 'Closed']);
    expect(statusActions('Closed')).toEqual([]);
  });
});
