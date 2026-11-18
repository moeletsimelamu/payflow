import { formatMoney, formatRate, fromMinorUnits, toMinorUnits, validateAmount } from './money';

describe('formatMoney', () => {
  it.each([
    ['ZAR', '1234.5', 'ZAR 1,234.50'],
    ['ZAR', '313.26', 'ZAR 313.26'],
    ['USD', '0.1', 'USD 0.10'],
    ['EUR', '1000000', 'EUR 1,000,000.00'],
    ['GBP', '19.99', 'GBP 19.99'],
    ['JPY', '1235', 'JPY 1,235'],
    ['JPY', '500.0000', 'JPY 500'],
    ['KWD', '1.5', 'KWD 1.500'],
    ['KWD', '12345.678', 'KWD 12,345.678'],
  ])('%s %s → %s', (currency, amount, expected) => {
    expect(formatMoney(amount, currency)).toBe(expected);
  });

  it('drops the database scale but never rounds', () => {
    expect(formatMoney('250.0000', 'ZAR')).toBe('ZAR 250.00');
    expect(formatMoney('250.0050', 'ZAR')).toBe('ZAR 250.0050'); // shown as received, not rounded to 250.01
  });

  it('shows negatives and zero plainly', () => {
    expect(formatMoney('-40.00', 'ZAR')).toBe('ZAR -40.00');
    expect(formatMoney('-100', 'ZAR')).toBe('ZAR -100.00');
    expect(formatMoney('-0.00', 'ZAR')).toBe('ZAR 0.00');
    expect(formatMoney('0', 'JPY')).toBe('JPY 0');
  });

  it('is exact where binary floating point is not', () => {
    // 0.1 + 0.2 !== 0.3 in IEEE 754; strings and BigInt keep money exact.
    expect(formatMoney('9007199254740993.01', 'USD')).toBe('USD 9,007,199,254,740,993.01');
    expect(fromMinorUnits((toMinorUnits('0.10', 'USD') ?? 0n) + (toMinorUnits('0.20', 'USD') ?? 0n), 'USD')).toBe('0.30');
  });

  it('handles missing or malformed values without throwing', () => {
    expect(formatMoney(null, 'ZAR')).toBe('—');
    expect(formatMoney('', 'ZAR')).toBe('—');
    expect(formatMoney('1e3', 'ZAR')).toBe('ZAR 1e3');
    expect(formatMoney('10', 'XYZ')).toBe('XYZ 10');
  });
});

describe('validateAmount', () => {
  it.each([
    ['', 'ZAR', undefined, 'required'],
    ['abc', 'ZAR', undefined, 'format'],
    ['1,000', 'ZAR', undefined, 'format'],
    ['10.001', 'ZAR', undefined, 'precision'],
    ['10.5', 'JPY', undefined, 'precision'],
    ['0', 'ZAR', undefined, 'positive'],
    ['-5', 'ZAR', undefined, 'positive'],
    ['60.01', 'ZAR', '60.00', 'too_large'],
    ['60.00', 'ZAR', '60.00', null],
    ['1.234', 'KWD', '2.000', null],
    ['500', 'JPY', '500', null],
    ['40', 'ZAR', '60.0000', null],
  ])('%j in %s (max %s) → %s', (input, currency, max, expected) => {
    expect(validateAmount(input, currency, max)).toBe(expected);
  });
});

describe('minor units', () => {
  it('round-trips each currency', () => {
    expect(toMinorUnits('40.00', 'ZAR')).toBe(4000n);
    expect(toMinorUnits('1.5', 'KWD')).toBe(1500n);
    expect(toMinorUnits('500', 'JPY')).toBe(500n);
    expect(fromMinorUnits(4000n, 'ZAR')).toBe('40.00');
    expect(fromMinorUnits(5n, 'ZAR')).toBe('0.05');
    expect(fromMinorUnits(1500n, 'KWD')).toBe('1.500');
    expect(fromMinorUnits(500n, 'JPY')).toBe('500');
    expect(fromMinorUnits(-4000n, 'ZAR')).toBe('-40.00');
  });
});

describe('formatRate', () => {
  it.each([
    [0.5714, '57.14%'],
    [0.0971, '9.71%'],
    [1, '100.00%'],
    [0, '0.00%'],
    ['0.1429', '14.29%'],
  ])('%s → %s', (rate, expected) => {
    expect(formatRate(rate)).toBe(expected);
  });
});
