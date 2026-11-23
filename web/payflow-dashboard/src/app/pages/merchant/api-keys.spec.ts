import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { of } from 'rxjs';
import { axeViolations, settle } from '../../../testing/axe';
import { ApiKey } from '../../core/models';
import { ApiKeysPage } from './api-keys';

const KEY: ApiKey = { id: 'key_1', name: 'Checkout', prefix: 'pf_test_sk_ab12', state: 'Active', createdAt: '2026-11-18T09:00:00Z', createdBy: 'usr_1', lastUsedAt: null, expiresAt: null, revokedAt: null };

describe('ApiKeysPage', () => {
  let http: HttpTestingController;
  let confirmAnswer: boolean;

  async function render() {
    confirmAnswer = true;
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), { provide: MatDialog, useValue: { open: () => ({ afterClosed: () => of(confirmAnswer) }) } }],
    });
    http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(ApiKeysPage);
    fixture.componentRef.setInput('merchantId', 'mer_1');
    fixture.detectChanges();
    await settle(fixture);
    http.expectOne('/api/merchants/mer_1/api-keys').flush([KEY]);
    await settle(fixture);
    return fixture;
  }

  function button(el: HTMLElement, text: string): HTMLButtonElement {
    return Array.from(el.querySelectorAll('button')).find((b) => b.textContent?.trim().startsWith(text)) as HTMLButtonElement;
  }

  it('shows a new secret once and forgets it when dismissed', async () => {
    const fixture = await render();
    const el = fixture.nativeElement as HTMLElement;
    const name = el.querySelector('input[name="name"]') as HTMLInputElement;
    name.value = 'Checkout server';
    name.dispatchEvent(new Event('input'));
    (el.querySelector('form') as HTMLFormElement).dispatchEvent(new Event('submit'));
    await settle(fixture);

    const create = http.expectOne((r) => r.method === 'POST' && r.url === '/api/merchants/mer_1/api-keys');
    expect(create.request.body).toEqual({ name: 'Checkout server' });
    create.flush({ key: { ...KEY, id: 'key_2', name: 'Checkout server' }, secret: 'pf_test_sk_secretvalue', notice: 'Shown once.' });
    await settle(fixture);
    http.expectOne('/api/merchants/mer_1/api-keys').flush([KEY]);
    await settle(fixture);

    expect(el.querySelector('.pf-secret code')?.textContent).toBe('pf_test_sk_secretvalue');
    button(el, 'I have stored it').click();
    await settle(fixture);
    expect(el.textContent).not.toContain('pf_test_sk_secretvalue');
  });

  it('revokes only after confirmation', async () => {
    const fixture = await render();
    const el = fixture.nativeElement as HTMLElement;

    confirmAnswer = false;
    button(el, 'Revoke').click();
    await settle(fixture);
    http.expectNone((r) => r.url.endsWith('/revoke'));

    confirmAnswer = true;
    button(el, 'Revoke').click();
    await settle(fixture);
    http.expectOne('/api/merchants/mer_1/api-keys/key_1/revoke').flush({ ...KEY, state: 'Revoked' });
    await settle(fixture);
    http.expectOne('/api/merchants/mer_1/api-keys').flush([{ ...KEY, state: 'Revoked' }]);
    await settle(fixture);
    expect(el.textContent).toContain('revoked');
  });

  it('rotates with the chosen grace period and shows the new secret', async () => {
    const fixture = await render();
    const el = fixture.nativeElement as HTMLElement;
    const grace = el.querySelector('#grace-key_1') as HTMLInputElement;
    grace.value = '6';
    button(el, 'Rotate').click();
    await settle(fixture);

    const rotate = http.expectOne('/api/merchants/mer_1/api-keys/key_1/rotate');
    expect(rotate.request.body).toEqual({ gracePeriodHours: 6 });
    rotate.flush({ key: { ...KEY, id: 'key_3' }, secret: 'pf_test_sk_rotated', notice: '' });
    await settle(fixture);
    http.expectOne('/api/merchants/mer_1/api-keys').flush([KEY]);
    await settle(fixture);
    expect(el.textContent).toContain('the old one keeps working for 6 h');
    expect(el.querySelector('.pf-secret code')?.textContent).toBe('pf_test_sk_rotated');
  });

  it('has no axe accessibility violations', async () => {
    const fixture = await render();
    expect(await axeViolations(fixture.nativeElement)).toEqual([]);
  });
});
