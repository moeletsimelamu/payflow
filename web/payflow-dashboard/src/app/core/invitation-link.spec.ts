import { invitationLink, parseInvitationFragment } from './invitation-link';

describe('invitation links', () => {
  const token = 'CfDJ8AT6+7tHf/WM7Tr==';

  it('round-trip email and token through the fragment, keeping "+", "/" and "="', () => {
    const link = invitationLink('https://localhost:8443', 'new+tag@merchant.test', token);
    expect(link.startsWith('https://localhost:8443/accept-invitation#')).toBe(true);
    expect(link).not.toContain('?');
    expect(parseInvitationFragment(new URL(link).hash)).toEqual({ email: 'new+tag@merchant.test', token });
  });

  it('reads the router-rewritten form, where "+" is no longer encoded', () => {
    expect(parseInvitationFragment(`#email=a@b.test&token=${token}`)).toEqual({ email: 'a@b.test', token });
  });

  it('tolerates missing or malformed values', () => {
    expect(parseInvitationFragment('')).toEqual({ email: null, token: null });
    expect(parseInvitationFragment('#token=%E0%A4%A')).toEqual({ email: null, token: null });
  });
});
