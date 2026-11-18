import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatRadioModule } from '@angular/material/radio';
import { MatSelectModule } from '@angular/material/select';
import { Api, ApiError } from '../../core/api';
import { ReconciliationException, ResolutionType } from '../../core/models';
import { fromMinorUnits, toMinorUnits, validateAmount } from '../../core/money';
import { MoneyPipe, UtcPipe, WordsPipe } from '../../shared/pipes';
import { StatusBadge } from '../../shared/status-badge';

type Action = 'review' | 'resolve' | 'adjust' | 'dismiss';

/** Work one reconciliation exception (REC-4): review, resolve (optionally with a ledger adjustment) or dismiss. */
@Component({
  selector: 'pf-exception-dialog',
  imports: [FormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatRadioModule, StatusBadge, MoneyPipe, UtcPipe, WordsPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>{{ x().outcome | words }} <pf-status [value]="x().status" /></h2>
    <mat-dialog-content>
      <p>{{ x().detail }}</p>
      <dl class="pf-dl">
        <dt>Reference</dt><dd><code>{{ x().reference ?? '—' }}</code></dd>
        <dt>Acquirer id</dt><dd><code>{{ x().externalId ?? '—' }}</code></dd>
        <dt>PayFlow amount</dt><dd>{{ x().internalAmount | money: x().internalCurrency }}</dd>
        <dt>Statement amount</dt><dd>{{ x().externalAmount | money: x().externalCurrency }}</dd>
        <dt>Seen in runs</dt><dd>{{ x().timesSeen }}</dd>
      </dl>
      @if (x().history?.length) {
        <h3>History</h3>
        <ol class="pf-timeline">
          @for (h of x().history; track $index) {
            <li><span class="pf-timeline-when">{{ h.occurredAt | utc }}</span> <pf-status [value]="h.to" /> <span class="pf-muted">{{ h.actor }}@if (h.note) { — {{ h.note }} }</span></li>
          }
        </ol>
      }

      @if (open()) {
        <fieldset class="pf-fieldset">
          <legend>Action</legend>
          <mat-radio-group [ngModel]="action()" (ngModelChange)="action.set($event)" name="action" aria-label="Action">
            @if (x().status === 'Open') {
              <mat-radio-button value="review">Start review</mat-radio-button>
            }
            <mat-radio-button value="resolve">Resolve</mat-radio-button>
            <mat-radio-button value="adjust">Resolve with a ledger adjustment</mat-radio-button>
            <mat-radio-button value="dismiss">Dismiss as a false positive</mat-radio-button>
          </mat-radio-group>
        </fieldset>

        @if (action() === 'resolve') {
          <mat-form-field appearance="outline" class="pf-full">
            <mat-label>Resolution</mat-label>
            <mat-select [ngModel]="resolution()" (ngModelChange)="resolution.set($event)" name="resolution">
              <mat-option value="AcceptedInternal">PayFlow is right; the statement is wrong</mat-option>
              <mat-option value="AcceptedExternal">The acquirer is right</mat-option>
              <mat-option value="ExternalCorrectionRequested">Correction requested from the acquirer</mat-option>
            </mat-select>
          </mat-form-field>
        }
        @if (action() === 'adjust') {
          <p class="pf-muted">Posts a ReconciliationAdjustment between reconciliation suspense and the acquirer receivable, in {{ x().externalCurrency ?? x().internalCurrency }}. Merchant balances are not touched.</p>
          <mat-form-field appearance="outline">
            <mat-label>Amount ({{ currency() }})</mat-label>
            <input matInput inputmode="decimal" name="amount" [ngModel]="amount()" (ngModelChange)="amount.set($event)" />
          </mat-form-field>
          <mat-form-field appearance="outline">
            <mat-label>Direction</mat-label>
            <mat-select [ngModel]="direction()" (ngModelChange)="direction.set($event)" name="direction">
              <mat-option value="IncreaseReceivable">The acquirer owes more</mat-option>
              <mat-option value="DecreaseReceivable">The acquirer owes less</mat-option>
            </mat-select>
          </mat-form-field>
          @if (amountProblem(); as p) {
            <p class="pf-error" role="alert">{{ p }}</p>
          }
        }
        <mat-form-field appearance="outline" class="pf-full">
          <mat-label>Note{{ action() === 'review' ? ' (optional)' : '' }}</mat-label>
          <textarea matInput rows="3" maxlength="1000" name="note" [ngModel]="note()" (ngModelChange)="note.set($event)"></textarea>
        </mat-form-field>
        @if (error(); as message) {
          <p class="pf-error" role="alert">{{ message }}</p>
        }
      } @else {
        <p>
          Closed as {{ x().resolutionType | words }} by {{ x().resolvedBy }} on {{ x().resolvedAt | utc }}.
          @if (x().adjustmentAmount) { Adjustment: {{ x().adjustmentAmount | money: currency() }} ({{ x().adjustmentDirection | words }}). }
        </p>
        <p>{{ x().resolutionNote }}</p>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button type="button" mat-dialog-close>{{ open() ? 'Cancel' : 'Close' }}</button>
      @if (open()) {
        <button mat-flat-button type="button" (click)="submit()" [disabled]="busy()">{{ busy() ? 'Saving…' : 'Save' }}</button>
      }
    </mat-dialog-actions>
  `,
})
export class ExceptionDialog {
  private readonly api = inject(Api);
  private readonly ref = inject<MatDialogRef<ExceptionDialog, ReconciliationException>>(MatDialogRef);
  protected readonly x = signal(inject<ReconciliationException>(MAT_DIALOG_DATA));

  protected readonly open = computed(() => this.x().status === 'Open' || this.x().status === 'InReview');
  protected readonly currency = computed(() => this.x().externalCurrency ?? this.x().internalCurrency ?? 'ZAR');
  protected readonly action = signal<Action>(this.x().status === 'Open' ? 'review' : 'resolve');
  protected readonly resolution = signal<ResolutionType>('AcceptedInternal');
  protected readonly amount = signal(this.suggestedAdjustment());
  protected readonly direction = signal<'IncreaseReceivable' | 'DecreaseReceivable'>('IncreaseReceivable');
  protected readonly note = signal('');
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  protected readonly amountProblem = computed(() => {
    if (this.action() !== 'adjust') {
      return null;
    }
    const p = validateAmount(this.amount(), this.currency());
    return p === null ? null : p === 'precision' ? `Too many decimal places for ${this.currency()}.` : 'Enter a positive amount.';
  });

  protected async submit(): Promise<void> {
    const note = this.note().trim();
    if (this.action() !== 'review' && !note) {
      this.error.set('A note explaining the decision is required.');
      return;
    }
    if (this.amountProblem()) {
      return;
    }

    this.busy.set(true);
    this.error.set(null);
    const id = this.x().id;
    try {
      const updated =
        this.action() === 'review'
          ? await this.api.reviewException(id, note || null)
          : this.action() === 'dismiss'
            ? await this.api.dismissException(id, note)
            : this.action() === 'adjust'
              ? await this.api.resolveException(id, { resolutionType: 'AdjustmentPosted', note, adjustment: { amount: this.amount().trim(), direction: this.direction() } })
              : await this.api.resolveException(id, { resolutionType: this.resolution(), note });
      this.ref.close(updated);
    } catch (e) {
      const error = ApiError.from(e);
      this.error.set(error.code === 'concurrent_update' || error.code === 'invalid_exception_transition' ? `${error.message} Close and reopen to see its current state.` : error.message);
    } finally {
      this.busy.set(false);
    }
  }

  /** For an amount mismatch, the difference is the obvious starting point. */
  private suggestedAdjustment(): string {
    const x = this.x();
    const currency = x.externalCurrency ?? x.internalCurrency;
    if (x.outcome !== 'AmountMismatch' || !x.internalAmount || !x.externalAmount || !currency) {
      return '';
    }
    const external = toMinorUnits(x.externalAmount, currency);
    const internal = toMinorUnits(x.internalAmount, currency);
    if (external === null || internal === null) {
      return '';
    }
    const diff = external > internal ? external - internal : internal - external;
    return fromMinorUnits(diff, currency);
  }
}
