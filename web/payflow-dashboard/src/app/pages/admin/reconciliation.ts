import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatSelectModule } from '@angular/material/select';
import { Api, ApiError } from '../../core/api';
import { isEmptyPage, loadable } from '../../core/loadable';
import { DISCREPANCY_OUTCOMES, ExceptionStatus, GeneratedStatement, ReconciliationException, ReconciliationOutcome, ReconciliationRun } from '../../core/models';
import { UtcPipe, WordsPipe } from '../../shared/pipes';
import { StateView } from '../../shared/state';
import { StatusBadge } from '../../shared/status-badge';
import { ExceptionDialog } from './exception-dialog';

const INJECTABLE = DISCREPANCY_OUTCOMES.map((o) => o.charAt(0).toLowerCase() + o.slice(1));

/** Reconciliation workspace (REC-1..5): statements in, matching runs, exception queue. */
@Component({
  selector: 'pf-reconciliation',
  imports: [FormsModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatPaginatorModule, StateView, StatusBadge, UtcPipe, WordsPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="pf-page-header">
      <h1>Reconciliation</h1>
      <p class="pf-muted">Compare the simulated acquirer's statements with PayFlow's records and work the differences.</p>
    </header>

    <section class="pf-card" aria-labelledby="import">
      <h2 id="import">1. Statement</h2>
      <p class="pf-muted">Generate one from PayFlow's own data, optionally with planted discrepancies, or load a CSV file.</p>
      <form class="pf-filters" (ngSubmit)="generate()" aria-label="Generate a statement">
        <mat-form-field appearance="outline">
          <mat-label>Period start</mat-label>
          <input matInput type="date" name="periodStart" [(ngModel)]="periodStart" required />
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label>Period end</mat-label>
          <input matInput type="date" name="periodEnd" [(ngModel)]="periodEnd" required />
        </mat-form-field>
        @for (key of injectable; track key) {
          <mat-form-field appearance="outline" class="pf-narrow">
            <mat-label>{{ label(key) }}</mat-label>
            <input matInput type="number" min="0" max="100" [name]="key" [(ngModel)]="inject[key]" />
          </mat-form-field>
        }
        <button mat-stroked-button type="submit" [disabled]="busy()">Generate</button>
      </form>
      <p>
        <label class="pf-file">Or load a CSV file <input type="file" accept=".csv,text/csv" (change)="loadFile($event)" /></label>
      </p>
      @if (generated(); as g) {
        <p role="status">
          Generated {{ g.fileName }}: {{ g.lineCount }} lines.
          @if (g.injected.length) { Planted: @for (i of g.injected; track $index) { {{ i.outcome | words }} on <code>{{ i.reference }}</code>{{ $last ? '.' : ',' }} } }
        </p>
      }
      <mat-form-field appearance="outline" class="pf-full">
        <mat-label>Statement CSV</mat-label>
        <textarea matInput rows="6" name="csv" [(ngModel)]="csv" class="pf-mono" spellcheck="false"></textarea>
      </mat-form-field>
      <div class="pf-filters">
        <mat-form-field appearance="outline">
          <mat-label>Source</mat-label>
          <input matInput name="sourceName" [(ngModel)]="sourceName" />
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label>File name</mat-label>
          <input matInput name="fileName" [(ngModel)]="fileName" />
        </mat-form-field>
        <button mat-flat-button type="button" (click)="importStatement()" [disabled]="busy() || !csv.trim()">Import</button>
      </div>
      @if (importErrors().length) {
        <div class="pf-error" role="alert">
          <p>The statement was not imported:</p>
          <ul>
            @for (e of importErrors(); track $index) {
              <li>{{ e }}</li>
            }
          </ul>
        </div>
      }
      @if (message(); as m) {
        <p class="pf-notice" role="status">{{ m }}</p>
      }
    </section>

    <section class="pf-card" aria-labelledby="statements">
      <h2 id="statements">2. Statements and matching</h2>
      <pf-state [of]="statements" emptyText="No statements imported yet.">
        @if (statements.value(); as page) {
          <div class="pf-table-wrap">
            <table class="pf-table">
              <caption class="pf-visually-hidden">Imported statements</caption>
              <thead><tr><th scope="col">Imported</th><th scope="col">File</th><th scope="col">Period</th><th scope="col" class="pf-num">Lines</th><th scope="col">Status</th><th scope="col">Action</th></tr></thead>
              <tbody>
                @for (s of page.items; track s.id) {
                  <tr>
                    <td class="pf-when">{{ s.importedAt | utc }}</td>
                    <td>{{ s.fileName }}<br /><code class="pf-muted">{{ s.id }}</code></td>
                    <td>{{ s.periodStart }} – {{ s.periodEnd }}</td>
                    <td class="pf-num">{{ s.lineCount }}</td>
                    <td><pf-status [value]="s.status" /></td>
                    <td>
                      <button mat-stroked-button type="button" (click)="run(s.id)" [disabled]="busy()">{{ s.status === 'Reconciled' ? 'Run again' : 'Run matching' }}</button>
                      <button mat-button type="button" (click)="showExceptionsFor(s.id)">Exceptions</button>
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }
      </pf-state>
      @if (lastRun(); as r) {
        <div role="status" class="pf-run-summary">
          <h3>Run {{ r.id }}</h3>
          <table class="pf-table">
            <caption class="pf-visually-hidden">Outcomes of the run</caption>
            <tbody>
              @for (row of runRows(r); track row.label) {
                <tr><th scope="row">{{ row.label }}</th><td class="pf-num">{{ row.count }}</td></tr>
              }
            </tbody>
          </table>
          <p>{{ r.exceptionsCreated }} new exception{{ r.exceptionsCreated === 1 ? '' : 's' }}, {{ r.exceptionsSeenAgain }} seen again.</p>
        </div>
      }
    </section>

    <section class="pf-card" aria-labelledby="queue">
      <h2 id="queue">3. Exceptions</h2>
      <form class="pf-filters" (ngSubmit)="applyExceptions()" aria-label="Filter exceptions">
        <mat-form-field appearance="outline">
          <mat-label>Status</mat-label>
          <mat-select name="status" [(ngModel)]="exceptionStatus">
            <mat-option value="">Any</mat-option>
            @for (s of exceptionStatuses; track s) {
              <mat-option [value]="s">{{ s | words }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label>Type</mat-label>
          <mat-select name="outcome" [(ngModel)]="outcome">
            <mat-option value="">Any</mat-option>
            @for (o of outcomes; track o) {
              <mat-option [value]="o">{{ o | words }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
        @if (statementFilter()) {
          <span>Statement <code>{{ statementFilter() }}</code> <button mat-button type="button" (click)="showExceptionsFor(null)">Show all</button></span>
        }
        <button mat-stroked-button type="submit">Apply</button>
      </form>
      <pf-state [of]="exceptions" loadingText="Loading exceptions…" emptyText="No exceptions match. A clean run raises none.">
        @if (exceptions.value(); as page) {
          <div class="pf-table-wrap">
            <table class="pf-table">
              <caption class="pf-visually-hidden">Reconciliation exceptions</caption>
              <thead><tr><th scope="col">Raised</th><th scope="col">Type</th><th scope="col">Reference</th><th scope="col">Detail</th><th scope="col">Status</th><th scope="col"><span class="pf-visually-hidden">Open</span></th></tr></thead>
              <tbody>
                @for (x of page.items; track x.id) {
                  <tr>
                    <td class="pf-when">{{ x.createdAt | utc }}</td>
                    <td>{{ x.outcome | words }}</td>
                    <td><code>{{ x.reference ?? '—' }}</code></td>
                    <td>{{ x.detail }}</td>
                    <td><pf-status [value]="x.status" /></td>
                    <td><button mat-button type="button" (click)="openException(x)">{{ x.status === 'Open' || x.status === 'InReview' ? 'Work' : 'View' }}</button></td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
          <mat-paginator [length]="page.total" [pageIndex]="exceptionPage()" [pageSize]="25" [hidePageSize]="true" (page)="goExceptions($event)" aria-label="Exception pages" />
        }
      </pf-state>
    </section>
  `,
})
export class ReconciliationPage implements OnInit {
  private readonly api = inject(Api);
  private readonly dialog = inject(MatDialog);

  protected readonly injectable = INJECTABLE;
  protected readonly outcomes = DISCREPANCY_OUTCOMES;
  protected readonly exceptionStatuses: ExceptionStatus[] = ['Open', 'InReview', 'Resolved', 'Dismissed'];

  protected periodStart = today();
  protected periodEnd = today();
  protected inject: Record<string, number> = Object.fromEntries(INJECTABLE.map((k) => [k, 0]));
  protected csv = '';
  protected sourceName = 'Simulated Acquirer';
  protected fileName = '';
  protected exceptionStatus: ExceptionStatus | '' = 'Open';
  protected outcome: ReconciliationOutcome | '' = '';

  protected readonly busy = signal(false);
  protected readonly generated = signal<GeneratedStatement | null>(null);
  protected readonly importErrors = signal<string[]>([]);
  protected readonly message = signal<string | null>(null);
  protected readonly lastRun = signal<ReconciliationRun | null>(null);
  protected readonly statementFilter = signal<string | null>(null);
  protected readonly exceptionPage = signal(0);

  protected readonly statements = loadable(() => this.api.statements(), isEmptyPage);
  protected readonly exceptions = loadable(
    () =>
      this.api.exceptions({
        status: this.exceptionStatus || undefined,
        outcome: this.outcome || undefined,
        statementId: this.statementFilter() ?? undefined,
        page: this.exceptionPage() + 1,
        pageSize: 25,
      }),
    isEmptyPage,
  );

  ngOnInit(): void {
    void this.statements.load();
    void this.exceptions.load();
  }

  protected label(key: string): string {
    return key.replace(/([A-Z])/g, ' $1').replace(/^./, (c) => c.toUpperCase());
  }

  protected async generate(): Promise<void> {
    await this.busyWith(async () => {
      const g = await this.api.generateStatement({ periodStart: this.periodStart, periodEnd: this.periodEnd, inject: this.inject });
      this.generated.set(g);
      this.csv = g.csv;
      this.fileName = g.fileName;
      this.sourceName = g.sourceName;
    });
  }

  protected loadFile(event: Event): void {
    const file = (event.target as HTMLInputElement).files?.[0];
    if (!file) {
      return;
    }
    this.fileName = file.name;
    void file.text().then((text) => {
      this.csv = text;
      this.generated.set(null);
    });
  }

  protected async importStatement(): Promise<void> {
    this.importErrors.set([]);
    await this.busyWith(async () => {
      try {
        const s = await this.api.importStatement({
          sourceName: this.sourceName.trim(),
          periodStart: this.periodStart,
          periodEnd: this.periodEnd,
          fileName: this.fileName.trim() || `statement-${this.periodStart}.csv`,
          csv: this.csv,
        });
        this.message.set(`Imported ${s.fileName} (${s.lineCount} lines). Run matching below.`);
        this.csv = '';
        this.generated.set(null);
        void this.statements.load();
      } catch (e) {
        const error = ApiError.from(e);
        const lines = Object.entries(error.problem.errors ?? {}).map(([where, problems]) => `${where}: ${problems.join(' ')}`);
        this.importErrors.set(lines.length ? lines : [error.message]);
      }
    });
  }

  protected async run(statementId: string): Promise<void> {
    await this.busyWith(async () => {
      this.lastRun.set(await this.api.runReconciliation(statementId));
      this.statementFilter.set(statementId);
      void this.statements.load();
      void this.exceptions.load();
    });
  }

  protected runRows(r: ReconciliationRun): { label: string; count: number }[] {
    const c = r.counts;
    return [
      { label: 'Matched', count: c.matched },
      { label: 'Missing from statement', count: c.missingExternal },
      { label: 'Unknown to PayFlow', count: c.missingInternal },
      { label: 'Amount mismatch', count: c.amountMismatch },
      { label: 'Currency mismatch', count: c.currencyMismatch },
      { label: 'Duplicate on statement', count: c.duplicateExternal },
      { label: 'Status discrepancy', count: c.statusDiscrepancy },
    ];
  }

  protected showExceptionsFor(statementId: string | null): void {
    this.statementFilter.set(statementId);
    this.applyExceptions();
  }

  protected applyExceptions(): void {
    this.exceptionPage.set(0);
    void this.exceptions.load();
  }

  protected goExceptions(event: PageEvent): void {
    this.exceptionPage.set(event.pageIndex);
    void this.exceptions.load();
  }

  protected async openException(x: ReconciliationException): Promise<void> {
    let full: ReconciliationException;
    try {
      full = await this.api.exception(x.id); // with history
    } catch (e) {
      this.message.set(ApiError.from(e).message);
      return;
    }
    this.dialog
      .open(ExceptionDialog, { data: full, width: '640px', restoreFocus: true })
      .afterClosed()
      .subscribe((updated) => {
        if (updated) {
          this.message.set(`Exception ${updated.id} is now ${updated.status}.`);
          void this.exceptions.load();
        }
      });
  }

  private async busyWith(work: () => Promise<void>): Promise<void> {
    this.busy.set(true);
    this.message.set(null);
    try {
      await work();
    } catch (e) {
      this.message.set(ApiError.from(e).message);
    } finally {
      this.busy.set(false);
    }
  }
}

function today(): string {
  return new Date().toISOString().slice(0, 10);
}
