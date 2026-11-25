import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { axeViolations, settle } from '../../testing/axe';
import { MfaStatus } from '../core/models';
import { AccountSecurityPage } from './account-security';

describe('AccountSecurityPage', () => {
  let http: HttpTestingController;

  async function render(status: MfaStatus) {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])] });
    http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(AccountSecurityPage);
    fixture.detectChanges();
    await settle(fixture);
    http.expectOne('/api/auth/mfa').flush(status);
    await settle(fixture);
    return fixture;
  }

  function button(fixture: Awaited<ReturnType<typeof render>>, label: string): HTMLButtonElement | undefined {
    return Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('button')).find((b) => b.textContent?.includes(label));
  }

  async function enter(fixture: Awaited<ReturnType<typeof render>>, code: string) {
    const input = (fixture.nativeElement as HTMLElement).querySelector('input[formcontrolname="code"]') as HTMLInputElement;
    input.value = code;
    input.dispatchEvent(new Event('input'));
    await settle(fixture);
  }

  it('offers set-up when MFA is off', async () => {
    const fixture = await render({ enabled: false, required: false, recoveryCodesRemaining: 0 });
    const el = fixture.nativeElement as HTMLElement;
    expect(el.textContent).toContain('Off.');
    expect(el.querySelector('a[href^="/mfa-setup"]')).not.toBeNull();
  });

  it('replaces the recovery codes after a current code and shows the new ones', async () => {
    const fixture = await render({ enabled: true, required: false, recoveryCodesRemaining: 3 });
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('3 recovery codes left');
    await enter(fixture, '654321');
    button(fixture, 'New recovery codes')!.click();
    await settle(fixture);

    const request = http.expectOne('/api/auth/mfa/recovery-codes');
    expect(request.request.body).toEqual({ code: '654321' });
    request.flush({ recoveryCodes: ['aaaaa-bbbbb-ccccc-ddddd', 'eeeee-fffff-ggggg-hhhhh'], notice: 'Each works once.' });
    await settle(fixture);
    http.expectOne('/api/auth/mfa').flush({ enabled: true, required: false, recoveryCodesRemaining: 10 });
    await settle(fixture);

    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelectorAll('.pf-recovery-codes li')).toHaveLength(2);
    expect(el.textContent).toContain('10 recovery codes left');
  });

  it('lets a merchant user turn MFA off, but not a platform admin', async () => {
    const merchantUser = await render({ enabled: true, required: false, recoveryCodesRemaining: 10 });
    await enter(merchantUser, '111111');
    button(merchantUser, 'Turn off')!.click();
    await settle(merchantUser);
    expect(http.expectOne('/api/auth/mfa/disable').request.body).toEqual({ code: '111111' });

    TestBed.resetTestingModule();
    const admin = await render({ enabled: true, required: true, recoveryCodesRemaining: 10 });
    expect(button(admin, 'Turn off')).toBeUndefined();
    expect((admin.nativeElement as HTMLElement).textContent).toContain('Platform admins must keep two-step verification on.');
  });

  it('never sends an incomplete code', async () => {
    const fixture = await render({ enabled: true, required: false, recoveryCodesRemaining: 10 });
    await enter(fixture, '12');
    button(fixture, 'New recovery codes')!.click();
    await settle(fixture);
    http.expectNone('/api/auth/mfa/recovery-codes');
    expect((fixture.nativeElement as HTMLElement).querySelector('[role="alert"]')?.textContent).toContain('6-digit code');
  });

  it('has no axe accessibility violations', async () => {
    const fixture = await render({ enabled: true, required: true, recoveryCodesRemaining: 10 });
    expect(await axeViolations(fixture.nativeElement)).toEqual([]);
  });
});
