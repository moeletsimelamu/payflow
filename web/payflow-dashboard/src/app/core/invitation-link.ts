/**
 * Invitation links carry the email and one-time token in the URL fragment
 * (/accept-invitation#email=…&token=…): browsers never send fragments to a server, so the token
 * stays out of nginx, Caddy and API logs.
 *
 * Encoding and decoding use encodeURIComponent/decodeURIComponent, never URLSearchParams: the
 * Angular router rewrites the address bar on its first navigation and leaves "+" unencoded in
 * fragments, and URLSearchParams would then read that "+" (common in tokens) as a space.
 */
export function invitationLink(origin: string, email: string, token: string): string {
  return `${origin}/accept-invitation#email=${encodeURIComponent(email)}&token=${encodeURIComponent(token)}`;
}

export function parseInvitationFragment(hash: string): { email: string | null; token: string | null } {
  const values: Record<string, string> = {};
  for (const part of hash.replace(/^#/, '').split('&')) {
    const at = part.indexOf('=');
    if (at > 0) {
      try {
        values[part.slice(0, at)] = decodeURIComponent(part.slice(at + 1));
      } catch {
        // Malformed escape: leave the field empty for the user to paste.
      }
    }
  }
  return { email: values['email'] ?? null, token: values['token'] ?? null };
}
