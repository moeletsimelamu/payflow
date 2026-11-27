import { createHmac } from 'node:crypto';

const ALPHABET = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
const STEP_SECONDS = 30;

function base32Decode(text: string): Buffer {
  const bytes: number[] = [];
  let value = 0;
  let bits = 0;
  for (const c of text.replace(/[\s=-]/g, '').toUpperCase()) {
    const index = ALPHABET.indexOf(c);
    if (index < 0) {
      throw new Error('The TOTP secret is not Base32');
    }
    value = (value << 5) | index;
    bits += 5;
    if (bits >= 8) {
      bytes.push((value >>> (bits - 8)) & 0xff);
      bits -= 8;
    }
  }
  return Buffer.from(bytes);
}

function code(secret: string, step: number): string {
  const counter = Buffer.alloc(8);
  counter.writeBigUInt64BE(BigInt(step));
  const hash = createHmac('sha1', base32Decode(secret)).update(counter).digest();
  const offset = hash[hash.length - 1] & 0x0f;
  const binary = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
  return String(binary % 1_000_000).padStart(6, '0');
}

/** The last step this process used per secret: PayFlow accepts each code once (RFC 6238 §5.2). */
const lastUsed = new Map<string, number>();

/**
 * A fresh authenticator code (RFC 6238: HMAC-SHA1, 30 s, 6 digits). If this process already used the
 * current step's code, waits for the next step rather than sending a code PayFlow would refuse.
 */
export async function freshTotp(secret: string): Promise<string> {
  for (;;) {
    const now = Date.now() / 1000;
    const step = Math.floor(now / STEP_SECONDS);
    if (step > (lastUsed.get(secret) ?? -1)) {
      lastUsed.set(secret, step);
      return code(secret, step);
    }
    await new Promise((r) => setTimeout(r, ((step + 1) * STEP_SECONDS - now) * 1000 + 250));
  }
}
