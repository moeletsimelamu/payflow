import { ChangeDetectionStrategy, Component, input, signal } from '@angular/core';
import type { EChartsCoreOption } from 'echarts/core';
import { NgxEchartsDirective } from 'ngx-echarts';

/**
 * An ECharts chart that is never the only way to read its data: the canvas is labelled, and a
 * toggle shows the same figures as a table (projected content), for screen readers and keyboard users.
 */
@Component({
  selector: 'pf-chart',
  imports: [NgxEchartsDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <figure class="pf-chart">
      <figcaption>
        <span class="pf-chart-title">{{ title() }}</span>
        <button type="button" class="pf-link-button" (click)="asTable.set(!asTable())" [attr.aria-pressed]="asTable()">
          {{ asTable() ? 'Show chart' : 'Show data as a table' }}
        </button>
      </figcaption>
      @if (asTable()) {
        <ng-content />
      } @else {
        <div echarts class="pf-chart-canvas" role="img" [attr.aria-label]="summary()" [options]="options()" [autoResize]="true"></div>
      }
    </figure>
  `,
})
export class Chart {
  readonly title = input.required<string>();
  /** One sentence describing what the chart shows, read by screen readers. */
  readonly summary = input.required<string>();
  readonly options = input.required<EChartsCoreOption>();
  protected readonly asTable = signal(false);
}
