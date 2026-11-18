import { HttpClient, HttpErrorResponse, HttpHeaders, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import {
  CurrentUser,
  Delivery,
  ExceptionStatus,
  GeneratedStatement,
  Merchant,
  Page,
  PaymentDetail,
  PaymentRefunds,
  PaymentRow,
  Problem,
  ReconciliationException,
  ReconciliationOutcome,
  ReconciliationRun,
  Refund,
  Report,
  ResolutionType,
  SettlementBatch,
  SettlementRun,
  SettlementStatus,
  Statement,
  WebhookEndpoint,
} from './models';

type Query = Record<string, string | number | boolean | null | undefined>;

function params(query: Query): HttpParams {
  let p = new HttpParams();
  for (const [key, value] of Object.entries(query)) {
    if (value !== null && value !== undefined && value !== '') {
      p = p.set(key, String(value));
    }
  }
  return p;
}

/** An error from the API, carrying the problem details PayFlow returns. */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly problem: Problem,
  ) {
    super(problem.detail ?? problem.title ?? `Request failed (${status})`);
  }

  get code(): string | undefined {
    return this.problem.code;
  }

  static from(error: unknown): ApiError {
    if (error instanceof ApiError) {
      return error;
    }
    if (error instanceof HttpErrorResponse) {
      const body = typeof error.error === 'object' && error.error !== null ? (error.error as Problem) : {};
      if (error.status === 0) {
        return new ApiError(0, { title: 'PayFlow could not be reached. Check your connection and try again.' });
      }
      return new ApiError(error.status, body);
    }
    return new ApiError(-1, { title: 'Something went wrong.' });
  }
}

/** Typed access to the same-origin dashboard API. Cookies carry the session; Angular adds X-XSRF-TOKEN. */
@Injectable({ providedIn: 'root' })
export class Api {
  private readonly http = inject(HttpClient);

  private async get<T>(url: string, query: Query = {}): Promise<T> {
    try {
      return await firstValueFrom(this.http.get<T>(url, { params: params(query) }));
    } catch (e) {
      throw ApiError.from(e);
    }
  }

  private async post<T>(url: string, body: unknown, headers?: Record<string, string>): Promise<T> {
    try {
      return await firstValueFrom(this.http.post<T>(url, body, { headers: new HttpHeaders(headers ?? {}) }));
    } catch (e) {
      throw ApiError.from(e);
    }
  }

  // Authentication
  antiforgery(): Promise<unknown> {
    return this.get('/api/auth/antiforgery');
  }
  login(email: string, password: string): Promise<CurrentUser> {
    return this.post('/api/auth/login', { email, password });
  }
  me(): Promise<CurrentUser> {
    return this.get('/api/auth/me');
  }
  logout(): Promise<unknown> {
    return this.post('/api/auth/logout', {});
  }

  // Merchants
  merchants(query: { search?: string; status?: string; page?: number; pageSize?: number } = {}): Promise<Page<Merchant>> {
    return this.get('/api/merchants', query);
  }
  merchant(merchantId: string): Promise<Merchant> {
    return this.get(`/api/merchants/${merchantId}`);
  }

  // Payments and refunds
  payments(merchantId: string, query: { status?: string; reference?: string; failureCode?: string; from?: string; to?: string; page?: number; pageSize?: number }): Promise<Page<PaymentRow>> {
    return this.get(`/api/merchants/${merchantId}/payments`, query);
  }
  payment(merchantId: string, paymentId: string): Promise<PaymentDetail> {
    return this.get(`/api/merchants/${merchantId}/payments/${paymentId}`);
  }
  refunds(merchantId: string, paymentId: string): Promise<PaymentRefunds> {
    return this.get(`/api/merchants/${merchantId}/payments/${paymentId}/refunds`);
  }
  /** The idempotency key makes a retried click safe: the API replays the first result. */
  refund(merchantId: string, paymentId: string, body: { amount: string; reason: string | null }, idempotencyKey: string): Promise<Refund> {
    return this.post(`/api/merchants/${merchantId}/payments/${paymentId}/refunds`, body, { 'Idempotency-Key': idempotencyKey });
  }

  // Webhooks
  webhookEndpoints(merchantId: string): Promise<WebhookEndpoint[]> {
    return this.get(`/api/merchants/${merchantId}/webhook-endpoints`);
  }
  deliveries(merchantId: string, query: { status?: string; eventType?: string; page?: number; pageSize?: number }): Promise<Page<Delivery>> {
    return this.get(`/api/merchants/${merchantId}/webhook-deliveries`, query);
  }
  delivery(merchantId: string, deliveryId: string): Promise<Delivery> {
    return this.get(`/api/merchants/${merchantId}/webhook-deliveries/${deliveryId}`);
  }

  // Settlement
  merchantSettlements(merchantId: string, query: { currency?: string; status?: SettlementStatus; page?: number }): Promise<Page<SettlementBatch>> {
    return this.get(`/api/merchants/${merchantId}/settlements`, query);
  }
  merchantSettlement(merchantId: string, settlementId: string): Promise<SettlementBatch> {
    return this.get(`/api/merchants/${merchantId}/settlements/${settlementId}`);
  }
  settlementCsvUrl(merchantId: string, settlementId: string): string {
    return `/api/merchants/${merchantId}/settlements/${settlementId}/items.csv`;
  }
  settlements(query: { merchantId?: string; currency?: string; status?: SettlementStatus; page?: number; pageSize?: number }): Promise<Page<SettlementBatch>> {
    return this.get('/api/settlements', query);
  }
  runSettlement(body: { merchantId?: string | null; currency?: string | null; cutoff?: string | null }): Promise<SettlementRun> {
    return this.post('/api/settlements/runs', body);
  }
  paySettlement(settlementId: string, simulateFailure: boolean): Promise<SettlementBatch> {
    return this.post(`/api/settlements/${settlementId}/pay`, { simulateFailure });
  }

  // Reconciliation
  statements(page = 1): Promise<Page<Statement>> {
    return this.get('/api/reconciliation/statements', { page });
  }
  generateStatement(body: { periodStart: string; periodEnd: string; inject: Partial<Record<string, number>> }): Promise<GeneratedStatement> {
    return this.post('/api/reconciliation/statements/generate', body);
  }
  importStatement(body: { sourceName: string; periodStart: string; periodEnd: string; fileName: string; csv: string }): Promise<Statement> {
    return this.post('/api/reconciliation/statements', body);
  }
  runReconciliation(statementId: string): Promise<ReconciliationRun> {
    return this.post(`/api/reconciliation/statements/${statementId}/runs`, {});
  }
  exceptions(query: { status?: ExceptionStatus; outcome?: ReconciliationOutcome; statementId?: string; page?: number; pageSize?: number }): Promise<Page<ReconciliationException>> {
    return this.get('/api/reconciliation/exceptions', query);
  }
  exception(exceptionId: string): Promise<ReconciliationException> {
    return this.get(`/api/reconciliation/exceptions/${exceptionId}`);
  }
  reviewException(exceptionId: string, note: string | null): Promise<ReconciliationException> {
    return this.post(`/api/reconciliation/exceptions/${exceptionId}/review`, { note });
  }
  resolveException(
    exceptionId: string,
    body: { resolutionType: ResolutionType; note: string; adjustment?: { amount: string; direction: 'IncreaseReceivable' | 'DecreaseReceivable' } },
  ): Promise<ReconciliationException> {
    return this.post(`/api/reconciliation/exceptions/${exceptionId}/resolve`, body);
  }
  dismissException(exceptionId: string, note: string): Promise<ReconciliationException> {
    return this.post(`/api/reconciliation/exceptions/${exceptionId}/dismiss`, { note });
  }

  // Reports
  report(scope: { merchantId?: string | null; asPlatform: boolean }, query: { currency: string; from?: string; to?: string }): Promise<Report> {
    return scope.asPlatform
      ? this.get('/api/reports', { ...query, merchantId: scope.merchantId })
      : this.get(`/api/merchants/${scope.merchantId}/reports`, query);
  }
}
