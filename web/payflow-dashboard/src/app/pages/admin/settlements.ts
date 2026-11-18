import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatSelectModule } from '@angular/material/select';
import { RouterLink } from '@angular/router';
import { Api, ApiError } from '../../core/api';
import { isEmptyPage, loadable } from '../../core/loadable';
import { SettlementBatch, SettlementRun, SettlementStatus } from '../../core/models';
import { SUPPORTED_CURRENCIES } from '../../core/money';
import { MoneyPipe, UtcPipe, WordsPipe } from '../../shared/pipes';
import { StateView } from '../../shared/state';
import { StatusBadge } from '../../shared/status-badge';

/** Settlement operations for platform admins (SET-1..4): run, review, pay out (simulated). */
@Component({
  selector: 'pf-admin-settlements',
  imports: [FormsModule, RouterLink, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatPaginatorModule, StateView, StatusBadge, MoneyPipe, UtcPipe, WordsPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="pf-page-header">
      <h1>Settlements</h1>
      <p class="pf-muted">Batches net each merchant's payments, fees and refunds up to a cut-off. Payouts are simulated.</p>
    </header>

    <section class="pf-card" aria-labelledby="run">
      <h2 id="run">Run settlement</h2>
      <form class="pf-filters" (ngSubmit)="run()" aria-label="Settlement run">
        <mat-form-field appearance="outline">
          <mat-label>Merchant id (optional)</mat-label>
          <input matInput name="merchantId" [(ngModel)]="runMerchant" placeholder="mer_…" />
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label>Currency</mat-label>
          <mat-select name="currency" [(ngModel)]="runCurrency">
            <mat-option value="">All</mat-option>
            @for (c of currencies; track c) {
              <mat-option [value]="c">{{ c }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label>Cut-off (UTC, optional)</mat-label>
          <input matInput type="datetime-local" name="cutoff" [(ngModel)]="runCutoff" />
          <mat-hint>Default: start of today (T+1)</mat-hint>
        </mat-form-field>
        <button mat-flat-button type="submit" [disabled]="running()">{{ running() ? 'Running…' : 'Run settlement' }}</button>
      </form>
      @if (runError(); as message) {
        <p class="pf-error" role="alert">{{ message }}</p>
      }
      @if (lastRun(); as r) {
        <div role="status">
          <p>Cut-off {{ r.cutoff | utc }}: {{ r.created.length }} batch{{ r.created.length === 1 ? '' : 'es' }} created, {{ r.skipped.length }} skipped.</p>
          @if (r.skipped.length) {
            <ul class="pf-list">
              @for (s of r.skipped; track s.merchantId + s.currency) {
                <li><code>{{ s.merchantId }}</code> {{ s.currency }}: {{ s.reason | words }}</li>
              }
            </ul>
          }
        </div>
      }
    </section>

    <section class="pf-card" aria-labelledby="batches">
      <h2 id="batches">Batches</h2>
      <form class="pf-filters" (ngSubmit)="apply()" aria-label="Filter batches">
        <mat-form-field appearance="outline">
          <mat-label>Status</mat-label>
          <mat-select name="status" [(ngModel)]="status">
            <mat-option value="">Any</mat-option>
            <mat-option value="Pending">Pending</mat-option>
            <mat-option value="Failed">Failed</mat-option>
            <mat-option value="Paid">Paid</mat-option>
          </mat-select>
        </mat-form-field>
        <button mat-stroked-button type="submit">Apply</button>
      </form>
      <mat-checkbox [(ngModel)]="simulateFailure" name="simulateFailure">Simulate a bank rejection on the next payout</mat-checkbox>
      @if (payMessage(); as message) {
        <p [class]="payFailed() ? 'pf-error' : 'pf-notice'" role="status">{{ message }}</p>
      }
      <pf-state [of]="list" loadingText="Loading batches…" emptyText="No batches match. Run settlement to create some.">
        @if (list.value(); as page) {
          <div class="pf-table-wrap">
            <table class="pf-table">
              <caption class="pf-visually-hidden">Settlement batches</caption>
              <thead>
                <tr><th scope="col">Created</th><th scope="col">Merchant</th><th scope="col">Status</th><th scope="col" class="pf-num">Items</th><th scope="col" class="pf-num">Net</th><th scope="col" class="pf-num">Payout</th><th scope="col">Action</th></tr>
              </thead>
              <tbody>
                @for (b of page.items; track b.id) {
                  <tr>
                    <td>{{ b.createdAt | utc }}<br /><code class="pf-muted">{{ b.id }}</code></td>
                    <td><a [routerLink]="['/merchants', b.merchantId, 'settlements', b.id]"><code>{{ b.merchantId }}</code></a></td>
                    <td><pf-status [value]="b.status" />@if (b.failureReason) { <br /><span class="pf-muted">{{ b.failureReason }}</span> }</td>
                    <td class="pf-num">{{ b.paymentCount + b.refundCount }}</td>
                    <td class="pf-num">{{ b.netAmount | money: b.currency }}</td>
                    <td class="pf-num">{{ b.payoutAmount | money: b.currency }}</td>
                    <td>
                      @if (b.status !== 'Paid') {
                        <button mat-stroked-button type="button" (click)="pay(b)" [disabled]="paying() === b.id">
                          {{ paying() === b.id ? 'Paying…' : b.status === 'Failed' ? 'Retry payout' : 'Pay out' }}
                        </button>
                      } @else {
                        <span class="pf-muted">{{ b.payoutReference }}</span>
                      }
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
          <mat-paginator [length]="page.total" [pageIndex]="pageIndex()" [pageSize]="25" [hidePageSize]="true" (page)="go($event)" aria-label="Batch pages" />
        }
      </pf-state>
    </section>
  `,
})
export class AdminSettlementsPage implements OnInit {
  private readonly api = inject(Api);
  protected readonly currencies = SUPPORTED_CURRENCIES;

  protected runMerchant = '';
  protected runCurrency = '';
  protected runCutoff = '';
  protected readonly running = signal(false);
  protected readonly runError = signal<string | null>(null);
  protected readonly lastRun = signal<SettlementRun | null>(null);

  protected status: SettlementStatus | '' = '';
  protected simulateFailure = false;
  protected readonly pageIndex = signal(0);
  protected readonly paying = signal<string | null>(null);
  protected readonly payMessage = signal<string | null>(null);
  protected readonly payFailed = signal(false);

  protected readonly list = loadable(() => this.api.settlements({ status: this.status || undefined, page: this.pageIndex() + 1, pageSize: 25 }), isEmptyPage);

  ngOnInit(): void {
    void this.list.load();
  }

  protected async run(): Promise<void> {
    this.running.set(true);
    this.runError.set(null);
    try {
      this.lastRun.set(
        await this.api.runSettlement({
          merchantId: this.runMerchant.trim() || null,
          currency: this.runCurrency || null,
          // datetime-local has no zone: treat the input as UTC, like every time on this dashboard.
          cutoff: this.runCutoff ? new Date(`${this.runCutoff}:00Z`).toISOString() : null,
        }),
      );
      void this.list.load();
    } catch (e) {
      this.runError.set(ApiError.from(e).message);
    } finally {
      this.running.set(false);
    }
  }

  protected async pay(batch: SettlementBatch): Promise<void> {
    this.paying.set(batch.id);
    this.payMessage.set(null);
    try {
      const result = await this.api.paySettlement(batch.id, this.simulateFailure);
      this.payFailed.set(result.status === 'Failed');
      this.payMessage.set(result.status === 'Paid' ? `Batch ${batch.id} paid, reference ${result.payoutReference}.` : `Payout of ${batch.id} failed: ${result.failureReason}. It can be retried.`);
      this.simulateFailure = false;
      void this.list.load();
    } catch (e) {
      this.payFailed.set(true);
      this.payMessage.set(ApiError.from(e).message);
    } finally {
      this.paying.set(null);
    }
  }

  protected apply(): void {
    this.pageIndex.set(0);
    void this.list.load();
  }

  protected go(event: PageEvent): void {
    this.pageIndex.set(event.pageIndex);
    void this.list.load();
  }
}
