import { ChangeDetectionStrategy, Component, input, output, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';

/**
 * Shows a secret (API key, webhook signing secret, invitation link) exactly once. It lives only in
 * this component's memory: it is never stored, logged or put in a URL, and it is gone once dismissed.
 */
@Component({
  selector: 'pf-secret',
  imports: [MatButtonModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="pf-secret" role="region" [attr.aria-label]="title()">
      <h3>{{ title() }}</h3>
      <p><strong>Copy it now.</strong> {{ notice() }}</p>
      <div class="pf-secret-value">
        <code [attr.aria-label]="title() + ' value'">{{ value() }}</code>
        <button mat-stroked-button type="button" (click)="copy()">{{ copied() ? 'Copied' : 'Copy' }}</button>
      </div>
      @if (copyFailed()) {
        <p class="pf-error" role="alert">Copying is not available here; select the text and copy it manually.</p>
      }
      <button mat-flat-button type="button" (click)="dismissed.emit()">I have stored it</button>
    </section>
  `,
  styles: `
    .pf-secret {
      border: 2px solid #8a6d00;
      background: #fffbea;
      border-radius: 8px;
      padding: 12px 16px;
      margin: 12px 0;
    }
    .pf-secret h3 {
      margin: 0 0 4px;
      font-size: 1rem;
    }
    .pf-secret-value {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 8px;
      margin: 8px 0 12px;
    }
    .pf-secret-value code {
      word-break: break-all;
      background: #fff;
      border: 1px solid #c4c7cf;
      padding: 6px 8px;
      border-radius: 4px;
      user-select: all;
    }
  `,
})
export class SecretPanel {
  readonly title = input.required<string>();
  readonly value = input.required<string>();
  readonly notice = input('It will not be shown again.');
  readonly dismissed = output<void>();
  protected readonly copied = signal(false);
  protected readonly copyFailed = signal(false);

  protected async copy(): Promise<void> {
    try {
      await navigator.clipboard.writeText(this.value());
      this.copied.set(true);
      this.copyFailed.set(false);
    } catch {
      this.copyFailed.set(true);
    }
  }
}
