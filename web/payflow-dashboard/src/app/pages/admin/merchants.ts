import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatSelectModule } from '@angular/material/select';
import { RouterLink } from '@angular/router';
import { Api } from '../../core/api';
import { isEmptyPage, loadable } from '../../core/loadable';
import { StateView } from '../../shared/state';
import { StatusBadge } from '../../shared/status-badge';
import { UtcPipe } from '../../shared/pipes';

@Component({
  selector: 'pf-merchants',
  imports: [FormsModule, RouterLink, MatFormFieldModule, MatInputModule, MatSelectModule, MatButtonModule, MatPaginatorModule, StateView, StatusBadge, UtcPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="pf-page-header">
      <h1>Merchants</h1>
      <p class="pf-muted">Every merchant on the platform. Open one to see its transactions, settlements and reports.</p>
      <a mat-flat-button routerLink="/admin/merchants/new">Register a merchant</a>
    </header>

    <form class="pf-filters" (ngSubmit)="apply()" role="search" aria-label="Filter merchants">
      <mat-form-field appearance="outline">
        <mat-label>Search</mat-label>
        <input matInput name="search" [(ngModel)]="search" placeholder="Name or email" />
      </mat-form-field>
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

    <pf-state [of]="list" emptyText="No merchants match these filters.">
      @if (list.value(); as page) {
        <div class="pf-table-wrap">
          <table class="pf-table">
            <caption class="pf-visually-hidden">Merchants, {{ page.total }} in total</caption>
            <thead>
              <tr><th scope="col">Trading name</th><th scope="col">Legal name</th><th scope="col">Status</th><th scope="col">Currencies</th><th scope="col">Created</th></tr>
            </thead>
            <tbody>
              @for (m of page.items; track m.id) {
                <tr>
                  <td><a [routerLink]="['/merchants', m.id]">{{ m.tradingName }}</a></td>
                  <td>{{ m.legalName }}</td>
                  <td><pf-status [value]="m.status" /></td>
                  <td>{{ currencies(m.currencies) }}</td>
                  <td>{{ m.createdAt | utc: false }}</td>
                </tr>
              }
            </tbody>
          </table>
        </div>
        <mat-paginator [length]="page.total" [pageIndex]="pageIndex()" [pageSize]="25" [hidePageSize]="true" (page)="go($event)" aria-label="Merchant pages" />
      }
    </pf-state>
  `,
})
export class MerchantsPage {
  private readonly api = inject(Api);
  protected readonly statuses = ['PendingActivation', 'Active', 'Suspended', 'Closed'];
  protected search = '';
  protected status = '';
  protected readonly pageIndex = signal(0);

  protected readonly list = loadable(
    () => this.api.merchants({ search: this.search.trim(), status: this.status, page: this.pageIndex() + 1, pageSize: 25 }),
    isEmptyPage,
  );

  constructor() {
    void this.list.load();
  }

  protected currencies(list: { currency: string }[]): string {
    return list.map((c) => c.currency).join(', ');
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
