import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { axeViolations, settle } from '../../testing/axe';
import { LoginPage } from './login';

describe('LoginPage', () => {
  let http: HttpTestingController;

  async function render(query = '') {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])] });
    http = TestBed.inject(HttpTestingController);
    if (query) {
      await TestBed.inject(Router).navigateByUrl(`/${query}`);
    }
    const fixture = TestBed.createComponent(LoginPage);
    fixture.detectChanges();
    await settle(fixture);
    return fixture;
  }

  async function fill(fixture: Awaited<ReturnType<typeof render>>, email: string, password: string) {
    const el = fixture.nativeElement as HTMLElement;
    for (const [name, value] of [['email', email], ['password', password]] as const) {
      const input = el.querySelector(`input[formcontrolname="${name}"]`) as HTMLInputElement;
      input.value = value;
      input.dispatchEvent(new Event('input'));
    }
    (el.querySelector('form') as HTMLFormElement).dispatchEvent(new Event('submit'));
    await settle(fixture);
  }

  it('fetches the XSRF token, then signs in and goes to the home page', async () => {
    const fixture = await render();
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
    await fill(fixture, 'admin@payflow.test', 'secret-passphrase');

    http.expectOne('/api/auth/antiforgery').flush(null);
    await settle(fixture);
    const login = http.expectOne('/api/auth/login');
    expect(login.request.body).toEqual({ email: 'admin@payflow.test', password: 'secret-passphrase' });
    login.flush({ id: 'usr_1', email: 'admin@payflow.test', displayName: 'Admin', isPlatformAdmin: true, memberships: [] });
    await settle(fixture);

    expect(navigate).toHaveBeenCalledWith('/admin/merchants');
  });

  it('shows one message for every credential failure, never which part was wrong', async () => {
    const fixture = await render();
    await fill(fixture, 'someone@example.test', 'wrong');
    http.expectOne('/api/auth/antiforgery').flush(null);
    await settle(fixture);
    http.expectOne('/api/auth/login').flush({ code: 'invalid_credentials' }, { status: 401, statusText: 'Unauthorized' });
    await settle(fixture);

    const alert = (fixture.nativeElement as HTMLElement).querySelector('[role="alert"]');
    expect(alert?.textContent).toContain('The email or password is incorrect, or the account is locked');
  });

  it('does not submit an invalid form', async () => {
    const fixture = await render();
    await fill(fixture, 'not-an-email', '');
    http.expectNone('/api/auth/antiforgery');
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Enter a valid email address.');
  });

  it('has no axe accessibility violations', async () => {
    const fixture = await render();
    expect(await axeViolations(fixture.nativeElement)).toEqual([]);
  });
});
