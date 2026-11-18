import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { WordsPipe } from './pipes';

type Tone = 'good' | 'bad' | 'warn' | 'neutral' | 'info';

const TONES: Record<string, Tone> = {
  Succeeded: 'good',
  Paid: 'good',
  Matched: 'good',
  Resolved: 'good',
  Active: 'good',
  PartiallyRefunded: 'info',
  Refunded: 'info',
  Reversed: 'info',
  Reconciled: 'info',
  Pending: 'warn',
  Processing: 'warn',
  Retrying: 'warn',
  InReview: 'warn',
  Open: 'warn',
  PendingActivation: 'warn',
  Imported: 'neutral',
  Created: 'neutral',
  Dismissed: 'neutral',
  Cancelled: 'neutral',
  Declined: 'bad',
  Failed: 'bad',
  Expired: 'bad',
  Exhausted: 'bad',
  Suspended: 'bad',
  Closed: 'bad',
};

/** A status label. Colour is never the only signal: the text always says the status. */
@Component({
  selector: 'pf-status',
  imports: [WordsPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<span class="pf-badge" [class]="'pf-badge pf-badge--' + tone()">{{ value() | words }}</span>`,
})
export class StatusBadge {
  readonly value = input.required<string>();
  readonly tone = computed<Tone>(() => TONES[this.value()] ?? 'neutral');
}
