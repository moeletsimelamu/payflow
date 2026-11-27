import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { axeViolations, settle } from '../../testing/axe';
import { AcceptInvitationPage } from './accept-invitation';

describe('AcceptInvitationPage', () => {
  let http: HttpTestingController;

  async function render(fragment: string | null) {
    history.replaceState(null, '', fragment ? `/accept-invitation#${fragment}` : '/accept-invitation');
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])] });
    http = TestBed.inject(HttpTestingController);
    const replace = vi.spyOn(history, 'replaceState');
    const fixture = TestBed.createComponent(AcceptInvitationPage);
    fixture.detectChanges();
    await settle(fixture);
    return { fixture, replace, el: fixture.nativeElement as HTMLElement };
  }

  function type(el: HTMLElement, name: string, value: string) {
    const input = el.querySelector(`input[formcontrolname="${name}"]`) as HTMLInputElement;
    input.value = value;
    input.dispatchEvent(new Event('input'));
    input.dispatchEvent(new Event('blur'));
  }

  it('takes email and token from the link fragment, then clears them from the address bar', async () => {
    const { el, replace } = await render('email=new%40merchant.test&token=abc123');
    expect((el.querySelector('input[formcontrolname="email"]') as HTMLInputElement).value).toBe('new@merchant.test');
    expect((el.querySelector('input[formcontrolname="token"]') as HTMLInputElement).value).toBe('abc123');
    expect(replace).toHaveBeenCalled();
  });

  it('keeps "+", "/" and "=" in the token exactly (regression: decoded fragments turned "+" into a space)', async () => {
    const token = 'CfDJ8+7tH/WM7==';
    // Both forms the address bar can hold: as generated (%2B), and after the router rewrote it (+).
    for (const fragment of [`email=new%40merchant.test&token=${encodeURIComponent(token)}`, `email=new@merchant.test&token=${token}`]) {
      TestBed.resetTestingModule();
      const { el } = await render(fragment);
      expect((el.querySelector('input[formcontrolname="token"]') as HTMLInputElement).value).toBe(token);
      expect((el.querySelector('input[formcontrolname="email"]') as HTMLInputElement).value).toBe('new@merchant.test');
    }
  });

  it('sets the password and goes to sign-in', async () => {
    const { fixture, el } = await render('email=new%40merchant.test&token=abc123');
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    type(el, 'password', 'a-long-passphrase');
    type(el, 'confirm', 'a-long-passphrase');
    (el.querySelector('form') as HTMLFormElement).dispatchEvent(new Event('submit'));
    await settle(fixture);

    http.expectOne('/api/auth/antiforgery').flush(null);
    await settle(fixture);
    const accept = http.expectOne('/api/auth/accept-invitation');
    expect(accept.request.body).toEqual({ email: 'new@merchant.test', token: 'abc123', password: 'a-long-passphrase' });
    accept.flush(null, { status: 204, statusText: 'No Content' });
    await settle(fixture);
    expect(navigate).toHaveBeenCalledWith(['/login'], { queryParams: { accepted: 1 } });
  });

  it('enforces 12 characters and matching passwords before calling the API', async () => {
    const { fixture, el } = await render('email=a%40b.test&token=t');
    type(el, 'password', 'short');
    type(el, 'confirm', 'different');
    (el.querySelector('form') as HTMLFormElement).dispatchEvent(new Event('submit'));
    await settle(fixture);
    http.expectNone('/api/auth/antiforgery');
    expect(el.textContent).toContain('Use at least 12 characters.');
    expect(el.textContent).toContain('The passwords do not match.');
  });

  it('explains an invalid or used invitation', async () => {
    const { fixture, el } = await render('email=a%40b.test&token=used');
    type(el, 'password', 'a-long-passphrase');
    type(el, 'confirm', 'a-long-passphrase');
    (el.querySelector('form') as HTMLFormElement).dispatchEvent(new Event('submit'));
    await settle(fixture);
    http.expectOne('/api/auth/antiforgery').flush(null);
    await settle(fixture);
    http.expectOne('/api/auth/accept-invitation').flush({ code: 'invalid_invitation' }, { status: 400, statusText: 'Bad Request' });
    await settle(fixture);
    expect(el.querySelector('[role="alert"]')?.textContent).toContain('invalid, already used or expired');
  });

  it('shows why the server refused a breached password', async () => {
    const { fixture, el } = await render('email=a%40b.test&token=t');
    type(el, 'password', 'Qwerty123456');
    type(el, 'confirm', 'Qwerty123456');
    (el.querySelector('form') as HTMLFormElement).dispatchEvent(new Event('submit'));
    await settle(fixture);
    http.expectOne('/api/auth/antiforgery').flush(null);
    await settle(fixture);
    http.expectOne('/api/auth/accept-invitation').flush(
      { code: 'validation_failed', errors: { password: ['This password appears in lists of breached passwords. Choose a different one.'] } },
      { status: 400, statusText: 'Bad Request' },
    );
    await settle(fixture);
    expect(el.querySelector('[role="alert"]')?.textContent).toContain('appears in lists of breached passwords');
  });

  it('has no axe accessibility violations', async () => {
    const { el } = await render(null);
    expect(await axeViolations(el)).toEqual([]);
  });
});
