import { ChangeDetectionStrategy, Component, OnInit, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatSelectModule } from '@angular/material/select';
import { Api, ApiError } from '../../core/api';
import { isEmptyPage, loadable } from '../../core/loadable';
import { Delivery, WEBHOOK_EVENT_TYPES, WebhookEndpoint } from '../../core/models';
import { SecretPanel } from '../../shared/secret-panel';
import { UtcPipe, WordsPipe } from '../../shared/pipes';
import { StateView } from '../../shared/state';
import { StatusBadge } from '../../shared/status-badge';

/** Webhook endpoints and delivery history with every attempt (WH-5). */
@Component({
  selector: 'pf-webhooks',
  imports: [FormsModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatPaginatorModule, StateView, StatusBadge, SecretPanel, UtcPipe, WordsPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="pf-page-header">
      <h1>Webhooks</h1>
      <p class="pf-muted">Signed event notifications to your servers. Delivery is at least once: de-duplicate on the event id.</p>
    </header>

    @if (secret(); as sec) {
      <pf-secret [title]="'Signing secret for ' + sec.url" [value]="sec.value" notice="Use it to verify the PayFlow-Signature header. It cannot be shown again." (dismissed)="secret.set(null)" />
    }

    <section class="pf-card" aria-labelledby="endpoints">
      <h2 id="endpoints">Endpoints</h2>
      <pf-state [of]="endpoints" emptyText="No webhook endpoints yet. Add one below.">
        <ul class="pf-list pf-endpoints">
          @for (e of endpoints.value(); track e.id) {
            <li>
              <strong class="pf-break">{{ e.url }}</strong> <pf-status [value]="e.isEnabled ? 'Active' : 'Cancelled'" />
              <br /><span class="pf-muted">{{ e.description ?? 'No description' }} · events: {{ e.eventTypes.join(', ') }} · secret ends …{{ e.secretHint }}</span>
              @if (e.previousSecretExpiresAt) {
                <br /><span class="pf-muted">The previous secret also signs until {{ e.previousSecretExpiresAt | utc }}.</span>
              }
              <div class="pf-actions">
                <button mat-button type="button" (click)="toggle(e)" [disabled]="busy()">{{ e.isEnabled ? 'Disable' : 'Enable' }}</button>
                <button mat-button type="button" (click)="roll(e)" [disabled]="busy() || !e.isEnabled">Roll secret</button>
                <button mat-button type="button" (click)="ping(e)" [disabled]="busy() || !e.isEnabled">Send ping</button>
              </div>
            </li>
          }
        </ul>
      </pf-state>

      <h3>Add an endpoint</h3>
      <form (ngSubmit)="create()" aria-label="Add a webhook endpoint">
        <div class="pf-filters">
          <mat-form-field appearance="outline" class="pf-wide">
            <mat-label>HTTPS URL</mat-label>
            <input matInput name="url" type="url" [ngModel]="url()" (ngModelChange)="url.set($event)" placeholder="https://example.com/payflow/webhooks" required />
          </mat-form-field>
          <mat-form-field appearance="outline">
            <mat-label>Description (optional)</mat-label>
            <input matInput name="description" maxlength="200" [ngModel]="description()" (ngModelChange)="description.set($event)" />
          </mat-form-field>
        </div>
        <fieldset class="pf-fieldset">
          <legend>Events</legend>
          <label class="pf-check"><input type="checkbox" [checked]="allEvents()" (change)="allEvents.set($any($event.target).checked)" /> All events (<code>*</code>)</label>
          @if (!allEvents()) {
            @for (t of eventTypes; track t) {
              <label class="pf-check"><input type="checkbox" [checked]="chosen().has(t)" (change)="choose(t, $any($event.target).checked)" /> <code>{{ t }}</code></label>
            }
          }
        </fieldset>
        <button mat-flat-button type="submit" [disabled]="busy()">Add endpoint</button>
      </form>
      @if (message(); as m) {
        <p [class]="failed() ? 'pf-error' : 'pf-notice'" role="status">{{ m }}</p>
      }
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
                      <button mat-button type="button" (click)="toggleAttempts(d)" [attr.aria-expanded]="openId() === d.id" [attr.aria-controls]="'attempts-' + d.id">
                        {{ openId() === d.id ? 'Hide attempts' : 'Show attempts' }}
                      </button>
                      @if (d.status === 'Exhausted' || d.status === 'Succeeded' || d.status === 'Cancelled') {
                        <button mat-button type="button" (click)="redeliver(d)" [disabled]="busy()">Redeliver</button>
                      }
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

  protected readonly eventTypes = WEBHOOK_EVENT_TYPES;
  protected readonly url = signal('');
  protected readonly description = signal('');
  protected readonly allEvents = signal(true);
  protected readonly chosen = signal(new Set<string>());
  protected readonly secret = signal<{ url: string; value: string } | null>(null);
  protected readonly busy = signal(false);
  protected readonly message = signal<string | null>(null);
  protected readonly failed = signal(false);

  protected readonly endpoints = loadable(() => this.api.webhookEndpoints(this.merchantId()), (list) => list.length === 0);
  protected readonly deliveries = loadable(() => this.api.deliveries(this.merchantId(), { status: this.status, page: this.pageIndex() + 1, pageSize: 25 }), isEmptyPage);

  ngOnInit(): void {
    void this.endpoints.load();
    void this.deliveries.load();
  }

  protected choose(type: string, on: boolean): void {
    const next = new Set(this.chosen());
    if (on) {
      next.add(type);
    } else {
      next.delete(type);
    }
    this.chosen.set(next);
  }

  protected async create(): Promise<void> {
    const url = this.url().trim();
    const eventTypes = this.allEvents() ? ['*'] : [...this.chosen()];
    // Only a basic shape check here: the API's SSRF guard decides which URLs are allowed (https to a
    // public host in production; the local demo also allows the http sink on the Compose network).
    if (!/^https?:\/\/[^\s/]+/i.test(url)) {
      this.report('Enter a full URL, starting with https://.', true);
      return;
    }
    if (eventTypes.length === 0) {
      this.report('Choose at least one event.', true);
      return;
    }
    await this.run(async () => {
      const created = await this.api.createWebhookEndpoint(this.merchantId(), { url, description: this.description().trim() || null, eventTypes });
      this.secret.set({ url: created.endpoint.url, value: created.secret });
      this.url.set('');
      this.description.set('');
      this.report('Endpoint added.', false);
    });
  }

  protected async toggle(e: WebhookEndpoint): Promise<void> {
    await this.run(async () => {
      await this.api.updateWebhookEndpoint(this.merchantId(), e.id, { isEnabled: !e.isEnabled });
      this.report(e.isEnabled ? 'Endpoint disabled; its pending deliveries were cancelled.' : 'Endpoint enabled.', false);
    });
  }

  protected async roll(e: WebhookEndpoint): Promise<void> {
    await this.run(async () => {
      const rolled = await this.api.rollWebhookSecret(this.merchantId(), e.id);
      this.secret.set({ url: rolled.endpoint.url, value: rolled.secret });
      this.report('New secret issued; the old one keeps signing for 24 hours.', false);
    });
  }

  protected async ping(e: WebhookEndpoint): Promise<void> {
    await this.run(async () => {
      await this.api.pingWebhookEndpoint(this.merchantId(), e.id);
      this.report('Ping queued; it appears in the delivery history below.', false);
    });
  }

  protected async redeliver(d: Delivery): Promise<void> {
    await this.run(async () => {
      await this.api.redeliver(this.merchantId(), d.id);
      this.report(`Redelivery of ${d.eventType} queued with a fresh attempt budget.`, false);
    });
  }

  private async run(work: () => Promise<void>): Promise<void> {
    this.busy.set(true);
    try {
      await work();
      void this.endpoints.load();
      void this.deliveries.load();
    } catch (err) {
      const error = ApiError.from(err);
      this.report(
        error.code === 'webhook_url_not_allowed'
          ? 'That URL is not allowed: it must be https to a public host (no localhost, internal names or private addresses).'
          : error.message,
        true,
      );
    } finally {
      this.busy.set(false);
    }
  }

  private report(text: string, failed: boolean): void {
    this.failed.set(failed);
    this.message.set(text);
  }

  protected apply(): void {
    this.pageIndex.set(0);
    void this.deliveries.load();
  }

  protected go(event: PageEvent): void {
    this.pageIndex.set(event.pageIndex);
    void this.deliveries.load();
  }

  protected async toggleAttempts(d: Delivery): Promise<void> {
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
