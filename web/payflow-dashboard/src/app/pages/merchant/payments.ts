import { ChangeDetectionStrategy, Component, OnInit, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatSelectModule } from '@angular/material/select';
import { RouterLink } from '@angular/router';
import { Api } from '../../core/api';
import { isEmptyPage, loadable } from '../../core/loadable';
import { PAYMENT_STATUSES } from '../../core/models';
import { MoneyPipe, UtcPipe, WordsPipe } from '../../shared/pipes';
import { StateView } from '../../shared/state';
import { StatusBadge } from '../../shared/status-badge';

export const PAGE_SIZE = 25;

/** Transaction explorer (TXN-1): filter by status, reference, failure code and creation date. */
@Component({
  selector: 'pf-payments',
  imports: [FormsModule, RouterLink, MatFormFieldModule, MatInputModule, MatSelectModule, MatButtonModule, MatPaginatorModule, StateView, StatusBadge, MoneyPipe, UtcPipe, WordsPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="pf-page-header">
      <h1>Transactions</h1>
      <p class="pf-muted">Newest first. Times are UTC.</p>
    </header>

    <form class="pf-filters" (ngSubmit)="apply()" role="search" aria-label="Filter transactions">
      <mat-form-field appearance="outline">
        <mat-label>Status</mat-label>
        <mat-select name="status" [(ngModel)]="status">
          <mat-option value="">Any</mat-option>
          @for (s of statuses; track s) {
            <mat-option [value]="s">{{ s | words }}</mat-option>
          }
        </mat-select>
      </mat-form-field>
      <mat-form-field appearance="outline">
        <mat-label>Merchant reference</mat-label>
        <input matInput name="reference" [(ngModel)]="reference" />
      </mat-form-field>
      <mat-form-field appearance="outline">
        <mat-label>Failure code</mat-label>
        <input matInput name="failureCode" [(ngModel)]="failureCode" placeholder="e.g. insufficient_funds" />
      </mat-form-field>
      <mat-form-field appearance="outline">
        <mat-label>Created from</mat-label>
        <input matInput type="date" name="from" [(ngModel)]="from" />
      </mat-form-field>
      <mat-form-field appearance="outline">
        <mat-label>Created to</mat-label>
        <input matInput type="date" name="to" [(ngModel)]="to" />
      </mat-form-field>
      <button mat-flat-button type="submit">Apply</button>
      <button mat-button type="button" (click)="clear()">Clear</button>
    </form>

    <pf-state [of]="list" loadingText="Loading transactions…" emptyText="No transactions match these filters.">
      @if (list.value(); as page) {
        <p class="pf-muted" aria-live="polite">{{ page.total }} transaction{{ page.total === 1 ? '' : 's' }}</p>
        <div class="pf-table-wrap">
          <table class="pf-table">
            <caption class="pf-visually-hidden">Transactions</caption>
            <thead>
              <tr>
                <th scope="col">Created</th>
                <th scope="col">Reference</th>
                <th scope="col">Status</th>
                <th scope="col" class="pf-num">Amount</th>
                <th scope="col" class="pf-num">Fee</th>
                <th scope="col">Failure code</th>
              </tr>
            </thead>
            <tbody>
              @for (p of page.items; track p.id) {
                <tr>
                  <td class="pf-when">{{ p.createdAt | utc }}</td>
                  <td><a [routerLink]="[p.id]">{{ p.merchantReference }}</a><br /><code class="pf-muted">{{ p.id }}</code></td>
                  <td><pf-status [value]="p.status" /></td>
                  <td class="pf-num">{{ p.amount | money: p.currency }}</td>
                  <td class="pf-num">{{ p.fee | money: p.currency }}</td>
                  <td>@if (p.failureCode) { <code>{{ p.failureCode }}</code> }</td>
                </tr>
              }
            </tbody>
          </table>
        </div>
        <mat-paginator [length]="page.total" [pageIndex]="pageIndex()" [pageSize]="pageSize" [hidePageSize]="true" (page)="go($event)" aria-label="Transaction pages" />
      }
    </pf-state>
  `,
})
export class PaymentsPage implements OnInit {
  private readonly api = inject(Api);
  readonly merchantId = input.required<string>();

  protected readonly statuses = PAYMENT_STATUSES;
  protected readonly pageSize = PAGE_SIZE;
  protected status = '';
  protected reference = '';
  protected failureCode = '';
  protected from = '';
  protected to = '';
  protected readonly pageIndex = signal(0);

  protected readonly list = loadable(
    () =>
      this.api.payments(this.merchantId(), {
        status: this.status,
        reference: this.reference.trim(),
        failureCode: this.failureCode.trim(),
        // Inclusive days in UTC: from 00:00 of the first day to 00:00 after the last.
        from: this.from ? `${this.from}T00:00:00Z` : undefined,
        to: this.to ? nextDay(this.to) : undefined,
        page: this.pageIndex() + 1,
        pageSize: PAGE_SIZE,
      }),
    isEmptyPage,
  );

  ngOnInit(): void {
    void this.list.load();
  }

  protected apply(): void {
    this.pageIndex.set(0);
    void this.list.load();
  }

  protected clear(): void {
    this.status = this.reference = this.failureCode = this.from = this.to = '';
    this.apply();
  }

  protected go(event: PageEvent): void {
    this.pageIndex.set(event.pageIndex);
    void this.list.load();
  }
}

function nextDay(day: string): string {
  const d = new Date(`${day}T00:00:00Z`);
  d.setUTCDate(d.getUTCDate() + 1);
  return d.toISOString();
}
