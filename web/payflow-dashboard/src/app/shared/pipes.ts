import { Pipe, PipeTransform } from '@angular/core';
import { formatMoney, formatRate } from '../core/money';

/** `{{ payment.amount | money: payment.currency }}` → "ZAR 1,234.50". */
@Pipe({ name: 'money' })
export class MoneyPipe implements PipeTransform {
  transform(amount: string | null | undefined, currency: string | null | undefined): string {
    return currency ? formatMoney(amount, currency) : (amount ?? '—');
  }
}

/** `{{ 0.5714 | rate }}` → "57.14%". */
@Pipe({ name: 'rate' })
export class RatePipe implements PipeTransform {
  transform(rate: number | string | null | undefined): string {
    return formatRate(rate);
  }
}

/** Splits PascalCase enum values for display: "PartiallyRefunded" → "Partially refunded". */
@Pipe({ name: 'words' })
export class WordsPipe implements PipeTransform {
  transform(value: string | null | undefined): string {
    if (!value) {
      return '—';
    }
    const spaced = value.replace(/([a-z])([A-Z])/g, '$1 $2').replace(/_/g, ' ');
    return spaced.charAt(0).toUpperCase() + spaced.slice(1).toLowerCase();
  }
}

/** UTC timestamps, unambiguous for an operations dashboard: "2026-11-18 09:12:09 UTC". */
@Pipe({ name: 'utc' })
export class UtcPipe implements PipeTransform {
  transform(value: string | null | undefined, withSeconds = true): string {
    if (!value) {
      return '—';
    }
    const date = new Date(value);
    if (Number.isNaN(date.getTime())) {
      return value;
    }
    const iso = date.toISOString();
    return `${iso.slice(0, 10)} ${iso.slice(11, withSeconds ? 19 : 16)} UTC`;
  }
}
