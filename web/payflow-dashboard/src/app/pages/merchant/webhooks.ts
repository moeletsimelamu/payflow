import { ChangeDetectionStrategy, Component, OnInit, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatSelectModule } from '@angular/material/select';
import { Api, ApiError } from '../../core/api';
import { isEmptyPage, loadable } from '../../core/loadable';
import { Delivery } from '../../core/models';
import { UtcPipe, WordsPipe } from '../../shared/pipes';
import { StateView } from '../../shared/state';
import { StatusBadge } from '../../shared/status-badge';

/** Webhook endpoints and delivery history with every attempt (WH-5). */
@Component({
  selector: 'pf-webhooks',
  imports: [FormsModule, MatButtonModule, MatFormFieldModule, MatSelectModule, MatPaginatorModule, StateView, StatusBadge, UtcPipe, WordsPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="pf-page-header">
      <h1>Webhooks</h1>
      <p class="pf-muted">Signed event notifications to your servers. Delivery is at least once: de-duplicate on the event id.</p>
    </header>

    <section class="pf-card" aria-labelledby="endpoints">
      <h2 id="endpoints">Endpoints</h2>
      <pf-state [of]="endpoints" emptyText="No webhook endpoints configured. Add one through the API (POST /api/merchants/{id}/webhook-endpoints).">
        <ul class="pf-list">
          @for (e of endpoints.value(); track e.id) {
            <li>
              <strong>{{ e.url }}</strong> <pf-status [value]="e.isEnabled ? 'Active' : 'Cancelled'" />
              <br /><span class="pf-muted">{{ e.description ?? 'No description' }} · events: {{ e.eventTypes.join(', ') }} · secret ends …{{ e.secretHint }}</span>
            </li>
          }
        </ul>
      </pf-state>
    </section>

    <section class="pf-card" aria-labelledby="deliveries">
      <h2 id="deliveries">Delivery history</h2>
      <form class="pf-filters" (ngSubmit)="apply()" aria-label="Filter deliveries">
        <mat-form-field appearance="outline">
          <mat-label>Status</mat-label>
          <mat-select name="status" [(ngModel)]="status">
            <mat-option value="">Any</mat-option>
            @for (s of statuses; track s) {
              <mat-option [value]="s">{{ s }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
        <button mat-stroked-button type="submit">Apply</button>
      </form>
      <pf-state [of]="deliveries" loadingText="Loading deliveries…" emptyText="No deliveries yet.">
        @if (deliveries.value(); as page) {
          <div class="pf-table-wrap">
            <table class="pf-table">
              <caption class="pf-visually-hidden">Webhook deliveries</caption>
              <thead><tr><th scope="col">Created</th><th scope="col">Event</th><th scope="col">Endpoint</th><th scope="col">Status</th><th scope="col" class="pf-num">Attempts</th><th scope="col"><span class="pf-visually-hidden">Details</span></th></tr></thead>
              <tbody>
                @for (d of page.items; track d.id) {
                  <tr>
                    <td class="pf-when">{{ d.createdAt | utc }}</td>
                    <td><code>{{ d.eventType }}</code><br /><code class="pf-muted">{{ d.eventId }}</code></td>
                    <td class="pf-break">{{ d.endpointUrl }}</td>
                    <td><pf-status [value]="d.status" />@if (d.nextAttemptAt) { <br /><span class="pf-muted">next {{ d.nextAttemptAt | utc }}</span> }</td>
                    <td class="pf-num">{{ d.attemptCount }}</td>
                    <td>
                      <button mat-button type="button" (click)="toggle(d)" [attr.aria-expanded]="openId() === d.id" [attr.aria-controls]="'attempts-' + d.id">
                        {{ openId() === d.id ? 'Hide attempts' : 'Show attempts' }}
                      </button>
                    </td>
                  </tr>
                  @if (openId() === d.id) {
                    <tr [id]="'attempts-' + d.id">
                      <td colspan="6">
                        @if (attemptsError(); as message) {
                          <p class="pf-error" role="alert">{{ message }}</p>
                        } @else if (detail(); as full) {
                          <ol class="pf-attempts">
                            @for (a of full.attempts ?? []; track a.attemptNumber) {
                              <li>
                                Attempt {{ a.attemptNumber }} at {{ a.startedAt | utc }}, {{ a.durationMs }} ms:
                                @if (a.responseStatus) { HTTP {{ a.responseStatus }} } @else { {{ a.errorKind | words }} }
                                @if (a.errorMessage) { <span class="pf-muted">— {{ a.errorMessage }}</span> }
                                @if (a.responseBodyExcerpt) { <pre class="pf-excerpt">{{ a.responseBodyExcerpt }}</pre> }
                              </li>
                            } @empty {
                              <li>No attempts yet.</li>
                            }
                          </ol>
                        } @else {
                          <p role="status">Loading attempts…</p>
                        }
                      </td>
                    </tr>
                  }
                }
              </tbody>
            </table>
          </div>
          <mat-paginator [length]="page.total" [pageIndex]="pageIndex()" [pageSize]="25" [hidePageSize]="true" (page)="go($event)" aria-label="Delivery pages" />
        }
      </pf-state>
    </section>
  `,
})
export class WebhooksPage implements OnInit {
  private readonly api = inject(Api);
  readonly merchantId = input.required<string>();
  protected readonly statuses = ['Pending', 'Retrying', 'Succeeded', 'Exhausted', 'Cancelled'];
  protected status = '';
  protected readonly pageIndex = signal(0);
  protected readonly openId = signal<string | null>(null);
  protected readonly detail = signal<Delivery | null>(null);
  protected readonly attemptsError = signal<string | null>(null);

  protected readonly endpoints = loadable(() => this.api.webhookEndpoints(this.merchantId()), (list) => list.length === 0);
  protected readonly deliveries = loadable(() => this.api.deliveries(this.merchantId(), { status: this.status, page: this.pageIndex() + 1, pageSize: 25 }), isEmptyPage);

  ngOnInit(): void {
    void this.endpoints.load();
    void this.deliveries.load();
  }

  protected apply(): void {
    this.pageIndex.set(0);
    void this.deliveries.load();
  }

  protected go(event: PageEvent): void {
    this.pageIndex.set(event.pageIndex);
    void this.deliveries.load();
  }

  protected async toggle(d: Delivery): Promise<void> {
    if (this.openId() === d.id) {
      this.openId.set(null);
      return;
    }
    this.openId.set(d.id);
    this.detail.set(null);
    this.attemptsError.set(null);
    try {
      const full = await this.api.delivery(this.merchantId(), d.id);
      if (this.openId() === d.id) {
        this.detail.set(full);
      }
    } catch (e) {
      this.attemptsError.set(ApiError.from(e).message);
    }
  }
}
