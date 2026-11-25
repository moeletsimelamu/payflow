import { HttpClient, HttpErrorResponse, HttpHeaders, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import {
  ApiKey,
  CurrentUser,
  Delivery,
  ExceptionStatus,
  FeePlan,
  GeneratedStatement,
  IssuedApiKey,
  LoginStep,
  Member,
  MemberInvitation,
  MemberRole,
  Merchant,
  MfaEnrollment,
  MfaRecoveryCodes,
  MfaStatus,
  Page,
  PaymentDetail,
  PaymentRefunds,
  PaymentRow,
  Problem,
  ReconciliationException,
  ReconciliationOutcome,
  ReconciliationRun,
  Refund,
  RegisteredMerchant,
  RegisterMerchantRequest,
  Report,
  ResolutionType,
  SettlementBatch,
  SettlementRun,
  SettlementStatus,
  Statement,
  WebhookEndpoint,
  WebhookSecret,
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
    return this.send<T>('POST', url, body, headers);
  }

  private async send<T>(method: 'POST' | 'PUT' | 'PATCH' | 'DELETE', url: string, body?: unknown, headers?: Record<string, string>): Promise<T> {
    try {
      return await firstValueFrom(this.http.request<T>(method, url, { body, headers: new HttpHeaders(headers ?? {}) }));
    } catch (e) {
      throw ApiError.from(e);
    }
  }

  // Authentication
  antiforgery(): Promise<unknown> {
    return this.get('/api/auth/antiforgery');
  }
  /** A session (the user), or the second-factor step still needed. */
  login(email: string, password: string): Promise<CurrentUser | LoginStep> {
    return this.post('/api/auth/login', { email, password });
  }
  verifyMfa(answer: { code: string } | { recoveryCode: string }): Promise<CurrentUser> {
    return this.post('/api/auth/mfa/verify', answer);
  }
  startMfaEnrollment(): Promise<MfaEnrollment> {
    return this.post('/api/auth/mfa/enrollment', null);
  }
  confirmMfaEnrollment(code: string): Promise<MfaRecoveryCodes> {
    return this.post('/api/auth/mfa/enrollment/confirm', { code });
  }
  mfaStatus(): Promise<MfaStatus> {
    return this.get('/api/auth/mfa');
  }
  regenerateRecoveryCodes(code: string): Promise<MfaRecoveryCodes> {
    return this.post('/api/auth/mfa/recovery-codes', { code });
  }
  disableMfa(code: string): Promise<unknown> {
    return this.post('/api/auth/mfa/disable', { code });
  }
  me(): Promise<CurrentUser> {
    return this.get('/api/auth/me');
  }
  logout(): Promise<unknown> {
    return this.post('/api/auth/logout', {});
  }
  acceptInvitation(email: string, token: string, password: string): Promise<unknown> {
    return this.post('/api/auth/accept-invitation', { email, token, password });
  }

  // Merchants
  merchants(query: { search?: string; status?: string; page?: number; pageSize?: number } = {}): Promise<Page<Merchant>> {
    return this.get('/api/merchants', query);
  }
  merchant(merchantId: string): Promise<Merchant> {
    return this.get(`/api/merchants/${merchantId}`);
  }
  registerMerchant(body: RegisterMerchantRequest): Promise<RegisteredMerchant> {
    return this.post('/api/merchants', body);
  }
  changeMerchantStatus(merchantId: string, status: 'Active' | 'Suspended' | 'Closed', reason: string | null): Promise<Merchant> {
    return this.post(`/api/merchants/${merchantId}/status`, { status, reason });
  }
  setCurrencyLimits(merchantId: string, currency: string, body: { maxPaymentAmount: string; operatorRefundLimit: string | null }): Promise<Merchant> {
    return this.send('PUT', `/api/merchants/${merchantId}/currencies/${currency}`, body);
  }
  feePlans(): Promise<FeePlan[]> {
    return this.get('/api/fee-plans');
  }

  // API keys and members (MerchantAdmin)
  apiKeys(merchantId: string): Promise<ApiKey[]> {
    return this.get(`/api/merchants/${merchantId}/api-keys`);
  }
  createApiKey(merchantId: string, name: string): Promise<IssuedApiKey> {
    return this.post(`/api/merchants/${merchantId}/api-keys`, { name });
  }
  rotateApiKey(merchantId: string, keyId: string, gracePeriodHours: number): Promise<IssuedApiKey> {
    return this.post(`/api/merchants/${merchantId}/api-keys/${keyId}/rotate`, { gracePeriodHours });
  }
  revokeApiKey(merchantId: string, keyId: string): Promise<ApiKey> {
    return this.post(`/api/merchants/${merchantId}/api-keys/${keyId}/revoke`, {});
  }
  members(merchantId: string): Promise<Member[]> {
    return this.get(`/api/merchants/${merchantId}/members`);
  }
  inviteMember(merchantId: string, body: { email: string; displayName: string; role: MemberRole }): Promise<MemberInvitation> {
    return this.post(`/api/merchants/${merchantId}/members`, body);
  }
  changeMemberRole(merchantId: string, userId: string, role: MemberRole): Promise<unknown> {
    return this.send('PATCH', `/api/merchants/${merchantId}/members/${userId}`, { role });
  }
  removeMember(merchantId: string, userId: string): Promise<unknown> {
    return this.send('DELETE', `/api/merchants/${merchantId}/members/${userId}`);
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
  createWebhookEndpoint(merchantId: string, body: { url: string; description: string | null; eventTypes: string[] }): Promise<WebhookSecret> {
    return this.post(`/api/merchants/${merchantId}/webhook-endpoints`, body);
  }
  updateWebhookEndpoint(merchantId: string, endpointId: string, body: { isEnabled?: boolean; eventTypes?: string[]; description?: string | null }): Promise<WebhookEndpoint> {
    return this.send('PATCH', `/api/merchants/${merchantId}/webhook-endpoints/${endpointId}`, body);
  }
  rollWebhookSecret(merchantId: string, endpointId: string): Promise<WebhookSecret> {
    return this.post(`/api/merchants/${merchantId}/webhook-endpoints/${endpointId}/roll-secret`, {});
  }
  pingWebhookEndpoint(merchantId: string, endpointId: string): Promise<Delivery> {
    return this.post(`/api/merchants/${merchantId}/webhook-endpoints/${endpointId}/ping`, {});
  }
  redeliver(merchantId: string, deliveryId: string): Promise<Delivery> {
    return this.post(`/api/merchants/${merchantId}/webhook-deliveries/${deliveryId}/redeliver`, {});
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
