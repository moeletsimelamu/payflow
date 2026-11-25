import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { axeViolations, settle } from '../../testing/axe';
import { Auth } from '../core/auth';
import { MfaSetupPage, groupKey } from './mfa-setup';

const SECRET = 'JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP';
const URI = `otpauth://totp/PayFlow:admin%40payflow.test?secret=${SECRET}&issuer=PayFlow&algorithm=SHA1&digits=6&period=30`;
const CODES = Array.from({ length: 10 }, (_, i) => `aaaa${i}-bbbbb-ccccc-ddddd`);

describe('MfaSetupPage', () => {
  let http: HttpTestingController;

  async function render() {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])] });
    http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(MfaSetupPage);
    fixture.detectChanges();
    await settle(fixture);
    // A fresh tab has no XSRF token yet: the page fetches one before its first POST.
    http.expectOne('/api/auth/antiforgery').flush(null);
    await settle(fixture);
    return fixture;
  }

  async function showKey() {
    const fixture = await render();
    http.expectOne('/api/auth/mfa/enrollment').flush({ secret: SECRET, provisioningUri: URI });
    await settle(fixture);
    return fixture;
  }

  async function submit(fixture: Awaited<ReturnType<typeof render>>, code: string) {
    const el = fixture.nativeElement as HTMLElement;
    const input = el.querySelector('input[formcontrolname="code"]') as HTMLInputElement;
    input.value = code;
    input.dispatchEvent(new Event('input'));
    (el.querySelector('form') as HTMLFormElement).dispatchEvent(new Event('submit'));
    await settle(fixture);
  }

  it('shows the setup key in groups of four and a link the authenticator app can open', async () => {
    const fixture = await showKey();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[aria-label="Setup key"]')?.textContent).toBe('JBSW Y3DP EHPK 3PXP JBSW Y3DP EHPK 3PXP');
    expect(el.querySelector('a[href^="otpauth://totp/"]')).not.toBeNull();
  });

  it('turns MFA on with the first code, signs in and shows the recovery codes once', async () => {
    const fixture = await showKey();
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
    await submit(fixture, '12345'); // too short: never sent
    http.expectNone('/api/auth/mfa/enrollment/confirm');

    await submit(fixture, '123456');
    const confirm = http.expectOne('/api/auth/mfa/enrollment/confirm');
    expect(confirm.request.body).toEqual({ code: '123456' });
    confirm.flush({
      recoveryCodes: CODES,
      notice: 'Each works once.',
      user: { id: 'usr_1', email: 'admin@payflow.test', displayName: 'Admin', isPlatformAdmin: true, memberships: [], mfaEnabled: true },
    });
    await settle(fixture);

    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelectorAll('.pf-recovery-codes li')).toHaveLength(10);
    expect(el.querySelector('[aria-label="Setup key"]')).toBeNull(); // the key is gone once MFA is on
    expect(TestBed.inject(Auth).user()?.mfaEnabled).toBe(true);

    (Array.from(el.querySelectorAll('button')).find((b) => b.textContent?.includes('I have stored them')) as HTMLButtonElement).click();
    await settle(fixture);
    expect(navigate).toHaveBeenCalledWith('/admin/merchants');
    expect(el.querySelectorAll('.pf-recovery-codes li')).toHaveLength(0);
  });

  it('explains a code that did not match', async () => {
    const fixture = await showKey();
    await submit(fixture, '000000');
    http.expectOne('/api/auth/mfa/enrollment/confirm').flush({ code: 'mfa_code_invalid' }, { status: 401, statusText: 'Unauthorized' });
    await settle(fixture);
    expect((fixture.nativeElement as HTMLElement).querySelector('[role="alert"]')?.textContent).toContain('That code did not match');
  });

  it('asks to sign in again when the pending sign-in has expired', async () => {
    const fixture = await render();
    http.expectOne('/api/auth/mfa/enrollment').flush({ code: 'mfa_sign_in_expired' }, { status: 401, statusText: 'Unauthorized' });
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[role="alert"]')?.textContent).toContain('Your sign-in has expired');
    expect(el.querySelector('a[href="/login"]')).not.toBeNull();
  });

  it('has no axe accessibility violations', async () => {
    const fixture = await showKey();
    expect(await axeViolations(fixture.nativeElement)).toEqual([]);
  });
});

describe('groupKey', () => {
  it('groups in fours without a trailing space', () => {
    expect(groupKey('ABCDEFGHIJ')).toBe('ABCD EFGH IJ');
    expect(groupKey('ABCDEFGH')).toBe('ABCD EFGH');
  });
});
