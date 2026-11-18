import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { Loadable } from '../core/loadable';

/**
 * The four states every data view has. Content is projected only when the data is ready, so a
 * page never shows a half-loaded table or a stale one under an error.
 */
@Component({
  selector: 'pf-state',
  imports: [MatButtonModule, MatProgressSpinnerModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @switch (of().status()) {
      @case ('loading') {
        <div class="pf-state" role="status" aria-live="polite">
          <mat-progress-spinner mode="indeterminate" diameter="28" aria-hidden="true" />
          <span>{{ loadingText() }}</span>
        </div>
      }
      @case ('error') {
        <div class="pf-state pf-state--error" role="alert">
          <p>
            <strong>{{ errorTitle() }}</strong>
            {{ of().error()?.message }}
            @if (of().error()?.problem?.correlationId; as id) {
              <span class="pf-muted">Reference: {{ id }}</span>
            }
          </p>
          <button mat-stroked-button type="button" (click)="of().load()">Try again</button>
        </div>
      }
      @case ('empty') {
        <div class="pf-state pf-state--empty" role="status">
          <p>{{ emptyText() }}</p>
          <ng-content select="[pfEmptyAction]" />
        </div>
      }
      @case ('ready') {
        <ng-content />
      }
    }
  `,
  styles: `
    .pf-state {
      display: flex;
      align-items: center;
      gap: 12px;
      padding: 24px 16px;
    }
    .pf-state--error,
    .pf-state--empty {
      flex-direction: column;
      align-items: flex-start;
    }
    .pf-state--error strong {
      display: block;
      margin-bottom: 4px;
    }
    .pf-state p {
      margin: 0;
    }
  `,
})
export class StateView {
  readonly of = input.required<Loadable<unknown>>();
  readonly loadingText = input('Loading…');
  readonly emptyText = input('Nothing to show yet.');
  readonly errorTitle = input('This could not be loaded.');
}
