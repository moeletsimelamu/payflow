import { navFor } from './shell';

const MERCHANT = 'mer_1';

describe('navFor', () => {
  it('gives platform admins the platform pages, plus merchant pages when viewing one', () => {
    const admin = { isPlatformAdmin: true, memberships: [] };
    expect(navFor(admin, null).platform.map((i) => i.label)).toEqual(['Merchants', 'Settlements', 'Reconciliation', 'Reports']);
    expect(navFor(admin, null).merchant).toEqual([]);
    expect(navFor(admin, MERCHANT).merchant.map((i) => i.label)).toEqual(['Overview', 'Transactions', 'Settlements', 'Reports', 'Webhooks']);
  });

  it('gives merchant admins webhooks but no platform pages', () => {
    const user = { isPlatformAdmin: false, memberships: [{ merchantId: MERCHANT, role: 'MerchantAdmin' }] };
    const nav = navFor(user, MERCHANT);
    expect(nav.platform).toEqual([]);
    expect(nav.merchant.map((i) => i.link)).toContain(`/merchants/${MERCHANT}/webhooks`);
  });

  it('hides webhook configuration from operators', () => {
    const user = { isPlatformAdmin: false, memberships: [{ merchantId: MERCHANT, role: 'MerchantOperator' }] };
    expect(navFor(user, MERCHANT).merchant.map((i) => i.label)).toEqual(['Overview', 'Transactions', 'Settlements', 'Reports']);
  });

  it('marks only the overview link as an exact match', () => {
    const user = { isPlatformAdmin: false, memberships: [{ merchantId: MERCHANT, role: 'MerchantAdmin' }] };
    expect(navFor(user, MERCHANT).merchant.filter((i) => i.exact).map((i) => i.label)).toEqual(['Overview']);
  });
});
