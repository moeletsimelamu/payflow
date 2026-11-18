/**
 * Money in the dashboard is always a decimal string from the API ("1234.50"). It is never converted
 * to a JavaScript number: binary floating point cannot represent most decimal amounts exactly.
 * Comparison and validation work on BigInt minor units instead.
 */

/** ISO 4217 minor units for the currencies PayFlow supports (mirrors SharedKernel `Currency`). */
export const MINOR_UNITS: Readonly<Record<string, number>> = {
  ZAR: 2,
  USD: 2,
  EUR: 2,
  GBP: 2,
  JPY: 0,
  KWD: 3,
};

export const SUPPORTED_CURRENCIES = Object.keys(MINOR_UNITS);

const DECIMAL = /^(-?)(\d+)(?:\.(\d+))?$/;

export function minorUnits(currency: string): number {
  const units = MINOR_UNITS[currency];
  if (units === undefined) {
    throw new Error(`Unsupported currency ${currency}`);
  }
  return units;
}

/**
 * Formats an API amount for display: `ZAR 1,234.50`, `JPY 1,235`, `KWD 1.500`, `ZAR -40.00`.
 * The fraction is padded to the currency's minor units; extra trailing zeros (the database scale)
 * are dropped. A value that cannot be shown exactly is returned as received rather than rounded.
 */
export function formatMoney(amount: string | null | undefined, currency: string): string {
  if (amount === null || amount === undefined || amount === '') {
    return '—';
  }

  const match = DECIMAL.exec(amount.trim());
  if (!match || MINOR_UNITS[currency] === undefined) {
    return `${currency} ${amount}`;
  }

  const [, sign, whole, rawFraction = ''] = match;
  const units = MINOR_UNITS[currency];
  let fraction = rawFraction;
  if (fraction.length > units) {
    const extra = fraction.slice(units);
    if (/[^0]/.test(extra)) {
      return `${currency} ${amount}`; // more precision than the currency has: show it untouched
    }
    fraction = fraction.slice(0, units);
  }
  fraction = fraction.padEnd(units, '0');

  const grouped = whole.replace(/^0+(?=\d)/, '').replace(/\B(?=(\d{3})+(?!\d))/g, ',');
  const isZero = /^0*$/.test(whole) && /^0*$/.test(fraction);
  const signText = sign && !isZero ? '-' : '';
  return `${currency} ${signText}${grouped}${units > 0 ? '.' + fraction : ''}`;
}

/** Parses an amount typed by a user into minor units, or null if it is not a valid positive amount. */
export function toMinorUnits(input: string, currency: string): bigint | null {
  const match = DECIMAL.exec(input.trim());
  const units = MINOR_UNITS[currency];
  if (!match || units === undefined) {
    return null;
  }

  const [, sign, whole, fraction = ''] = match;
  if (fraction.length > units) {
    return null;
  }

  const value = BigInt(whole + fraction.padEnd(units, '0'));
  return sign ? -value : value;
}

/** The inverse of toMinorUnits: 4000n ZAR → "40.00". */
export function fromMinorUnits(value: bigint, currency: string): string {
  const units = minorUnits(currency);
  const negative = value < 0n;
  const digits = (negative ? -value : value).toString().padStart(units + 1, '0');
  const text = units ? `${digits.slice(0, -units)}.${digits.slice(-units)}` : digits;
  return negative ? `-${text}` : text;
}

export type AmountProblem = 'required' | 'format' | 'precision' | 'positive' | 'too_large';

/** Validates a refund-style amount: positive, within the currency's precision, not above `max`. */
export function validateAmount(input: string, currency: string, max?: string): AmountProblem | null {
  const text = input.trim();
  if (text === '') {
    return 'required';
  }

  if (!DECIMAL.test(text)) {
    return 'format';
  }

  const value = toMinorUnits(text, currency);
  if (value === null) {
    return 'precision';
  }

  if (value <= 0n) {
    return 'positive';
  }

  if (max !== undefined) {
    const limit = toMinorUnits(max, currency) ?? toMinorUnits(trimScale(max), currency);
    if (limit !== null && value > limit) {
      return 'too_large';
    }
  }

  return null;
}

function trimScale(amount: string): string {
  return amount.includes('.') ? amount.replace(/0+$/, '').replace(/\.$/, '') : amount;
}

/** Formats a 0–1 fraction from the API ("0.5714") as a percentage ("57.14%") without floating point. */
export function formatRate(rate: number | string | null | undefined): string {
  if (rate === null || rate === undefined) {
    return '—';
  }

  // Rates are display-only fractions with 4 decimals (the API rounds them); not money.
  const text = typeof rate === 'number' ? rate.toFixed(4) : rate;
  const match = DECIMAL.exec(text);
  if (!match) {
    return '—';
  }

  const [, sign, whole, fraction = ''] = match;
  const basisPoints = BigInt(whole + fraction.padEnd(4, '0').slice(0, 4)); // 0.5714 → 5714
  const percent = basisPoints / 100n;
  const hundredths = (basisPoints % 100n).toString().padStart(2, '0');
  return `${sign}${percent}.${hundredths}%`;
}
