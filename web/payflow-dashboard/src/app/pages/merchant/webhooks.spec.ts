import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { axeViolations, settle } from '../../../testing/axe';
import { WebhookEndpoint } from '../../core/models';
import { WebhooksPage } from './webhooks';

const ENDPOINT: WebhookEndpoint = {
  id: 'we_1',
  url: 'https://hooks.example.test/payflow',
  description: 'Orders service',
  eventTypes: ['*'],
  isEnabled: true,
  secretHint: 'a1b2',
  previousSecretExpiresAt: null,
  createdAt: '2026-11-18T09:00:00Z',
  updatedAt: '2026-11-18T09:00:00Z',
};

describe('WebhooksPage', () => {
  let http: HttpTestingController;

  async function render() {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(WebhooksPage);
    fixture.componentRef.setInput('merchantId', 'mer_1');
    fixture.detectChanges();
    await settle(fixture);
    flushLists([ENDPOINT]);
    await settle(fixture);
    return { fixture, el: fixture.nativeElement as HTMLElement };
  }

  function flushLists(endpoints: WebhookEndpoint[]) {
    http.expectOne('/api/merchants/mer_1/webhook-endpoints').flush(endpoints);
    http.expectOne((r) => r.url === '/api/merchants/mer_1/webhook-deliveries').flush({ items: [], page: 1, pageSize: 25, total: 0 });
  }

  function submitEndpoint(el: HTMLElement, url: string) {
    const input = el.querySelector('input[name="url"]') as HTMLInputElement;
    input.value = url;
    input.dispatchEvent(new Event('input'));
    (el.querySelector('form[aria-label="Add a webhook endpoint"]') as HTMLFormElement).dispatchEvent(new Event('submit'));
  }

  it('creates an endpoint for all events and shows its signing secret once', async () => {
    const { fixture, el } = await render();
    submitEndpoint(el, '  https://orders.example.test/hooks ');
    await settle(fixture);

    const create = http.expectOne((r) => r.method === 'POST' && r.url === '/api/merchants/mer_1/webhook-endpoints');
    expect(create.request.body).toEqual({ url: 'https://orders.example.test/hooks', description: null, eventTypes: ['*'] });
    create.flush({ endpoint: { ...ENDPOINT, id: 'we_2', url: 'https://orders.example.test/hooks' }, secret: 'whsec_shownonce', notice: '' });
    await settle(fixture);
    flushLists([ENDPOINT]);
    await settle(fixture);
    expect(el.querySelector('.pf-secret code')?.textContent).toBe('whsec_shownonce');
  });

  it('subscribes to chosen events only', async () => {
    const { fixture, el } = await render();
    (el.querySelector('fieldset input[type="checkbox"]') as HTMLInputElement).click(); // untick "all events"
    await settle(fixture);
    const refund = Array.from(el.querySelectorAll('label.pf-check')).find((l) => l.textContent?.includes('refund.succeeded'))!.querySelector('input') as HTMLInputElement;
    refund.click();
    await settle(fixture);
    submitEndpoint(el, 'https://orders.example.test/hooks');
    await settle(fixture);
    expect(http.expectOne((r) => r.method === 'POST' && r.url.endsWith('/webhook-endpoints')).request.body.eventTypes).toEqual(['refund.succeeded']);
  });

  it('rejects something that is not a URL locally, and leaves the allowed-URL policy to the API', async () => {
    const { fixture, el } = await render();
    submitEndpoint(el, 'orders.example.test/hooks');
    await settle(fixture);
    http.expectNone((r) => r.method === 'POST');
    expect(el.textContent).toContain('Enter a full URL, starting with https://.');

    submitEndpoint(el, 'https://10.0.0.5/hooks');
    await settle(fixture);
    http.expectOne((r) => r.method === 'POST').flush({ code: 'webhook_url_not_allowed', detail: 'Not allowed.' }, { status: 400, statusText: 'Bad Request' });
    await settle(fixture);
    expect(el.textContent).toContain('it must be https to a public host');
  });

  it('disables an endpoint with a PATCH', async () => {
    const { fixture, el } = await render();
    (Array.from(el.querySelectorAll('button')).find((b) => b.textContent?.trim() === 'Disable') as HTMLButtonElement).click();
    await settle(fixture);
    const patch = http.expectOne('/api/merchants/mer_1/webhook-endpoints/we_1');
    expect(patch.request.method).toBe('PATCH');
    expect(patch.request.body).toEqual({ isEnabled: false });
  });

  it('has no axe accessibility violations', async () => {
    const { el } = await render();
    expect(await axeViolations(el)).toEqual([]);
  });
});
