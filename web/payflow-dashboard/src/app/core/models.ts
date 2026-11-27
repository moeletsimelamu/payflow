/** Shapes returned by the PayFlow dashboard API (docs/API.md). Amounts are decimal strings. */

export type MemberRole = 'MerchantAdmin' | 'MerchantOperator';

export interface Membership {
  merchantId: string;
  role: MemberRole;
}

export interface CurrentUser {
  id: string;
  email: string;
  displayName: string;
  isPlatformAdmin: boolean;
  memberships: Membership[];
  mfaEnabled: boolean;
}

/** A verified password that still needs a second factor (202 from /api/auth/login). */
export interface LoginStep {
  next: 'mfa' | 'mfa_enrollment';
}

export interface MfaEnrollment {
  secret: string;
  provisioningUri: string;
}

export interface MfaRecoveryCodes {
  recoveryCodes: string[];
  notice: string;
  user?: CurrentUser | null;
}

export interface MfaStatus {
  enabled: boolean;
  required: boolean;
  recoveryCodesRemaining: number;
}

export interface Page<T> {
  items: T[];
  page: number;
  pageSize: number;
  total: number;
}

export interface MerchantCurrency {
  currency: string;
  maxPaymentAmount: string;
  operatorRefundLimit: string | null;
}

export interface Merchant {
  id: string;
  legalName: string;
  tradingName: string;
  contactEmail: string;
  countryCode: string;
  status: 'PendingActivation' | 'Active' | 'Suspended' | 'Closed';
  statusReason: string | null;
  feePlanId: string;
  currencies: MerchantCurrency[];
  createdAt: string;
  updatedAt: string;
}

export type PaymentStatus =
  | 'Created'
  | 'Processing'
  | 'Pending'
  | 'Succeeded'
  | 'Declined'
  | 'Failed'
  | 'Expired'
  | 'PartiallyRefunded'
  | 'Refunded'
  | 'Reversed';

export const PAYMENT_STATUSES: PaymentStatus[] = [
  'Pending',
  'Succeeded',
  'Declined',
  'Failed',
  'Expired',
  'PartiallyRefunded',
  'Refunded',
  'Reversed',
];

export interface PaymentRow {
  id: string;
  merchantId: string;
  merchantReference: string;
  amount: string;
  currency: string;
  status: PaymentStatus;
  fee: string | null;
  failureCode: string | null;
  createdAt: string;
}

export interface StatusChange {
  from: PaymentStatus | null;
  to: PaymentStatus;
  reason: string;
  actorType: string;
  actorId: string | null;
  occurredAt: string;
}

export interface Payment {
  id: string;
  merchantReference: string;
  amount: string;
  currency: string;
  status: PaymentStatus;
  paymentMethod: string;
  fee: string | null;
  net: string | null;
  refundedAmount: string;
  refundableAmount: string;
  failureCode: string | null;
  failureReason: string | null;
  description: string | null;
  createdAt: string;
  completedAt: string | null;
  simulated: boolean;
  statusHistory: StatusChange[] | null;
}

export interface LedgerPosting {
  account: string;
  direction: 'Debit' | 'Credit';
  amount: string;
}

export interface LedgerEntry {
  entryType: string;
  description: string;
  occurredAt: string;
  postings: LedgerPosting[];
}

export interface PaymentDetail {
  payment: Payment;
  merchantId: string;
  idempotencyKey: string;
  feePlanId: string;
  feeRate: string;
  processingStartedAt: string | null;
  processingMilliseconds: number | null;
  ledger: LedgerEntry[];
}

export interface Refund {
  id: string;
  paymentId: string;
  amount: string;
  currency: string;
  status: 'Pending' | 'Succeeded' | 'Failed';
  reason: string | null;
  failureReason: string | null;
  requestedByType: string;
  createdAt: string;
  completedAt: string | null;
}

export interface Reversal {
  id: string;
  amount: string;
  feeReturned: string;
  currency: string;
  reason: string;
  createdAt: string;
}

export interface PaymentRefunds {
  paymentId: string;
  refunds: Refund[];
  reversal: Reversal | null;
}

export interface ApiKey {
  id: string;
  name: string;
  prefix: string;
  state: 'Active' | 'Expiring' | 'Expired' | 'Revoked';
  createdAt: string;
  createdBy: string;
  lastUsedAt: string | null;
  expiresAt: string | null;
  revokedAt: string | null;
}

/** Returned once, when a key is created or rotated: the secret is never retrievable again. */
export interface IssuedApiKey {
  key: ApiKey;
  secret: string;
  notice: string;
}

export interface Member {
  userId: string;
  email: string;
  displayName: string;
  role: MemberRole;
  addedAt: string;
  hasAcceptedInvitation: boolean;
}

export interface MemberInvitation {
  userId: string;
  email: string;
  role: MemberRole;
  /** Only for users without a password yet; shown once. */
  invitationToken: string | null;
}

export interface FeePlan {
  id: string;
  name: string;
  version: number;
  percentageRate: string;
  terms: { currency: string; fixedFee: string; minPaymentAmount: string }[];
  createdAt: string;
}

export interface RegisterMerchantRequest {
  legalName: string;
  tradingName: string;
  contactEmail: string;
  countryCode: string;
  currencies: { currency: string; maxPaymentAmount: string; operatorRefundLimit: string | null }[];
  feePlanId: string | null;
  adminEmail: string | null;
  adminDisplayName: string | null;
}

export interface RegisteredMerchant {
  merchant: Merchant;
  adminInvitation: MemberInvitation | null;
}

export interface WebhookEndpoint {
  id: string;
  url: string;
  description: string | null;
  eventTypes: string[];
  isEnabled: boolean;
  secretHint: string;
  previousSecretExpiresAt: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface WebhookSecret {
  endpoint: WebhookEndpoint;
  secret: string;
  notice: string;
}

/** Event types an endpoint can subscribe to (WebhookEventTypes.Subscribable). */
export const WEBHOOK_EVENT_TYPES = [
  'payment.pending',
  'payment.succeeded',
  'payment.declined',
  'payment.failed',
  'payment.expired',
  'payment.reversed',
  'refund.succeeded',
  'refund.failed',
  'settlement.paid',
  'webhook.ping',
];

export interface DeliveryAttempt {
  attemptNumber: number;
  startedAt: string;
  durationMs: number;
  responseStatus: number | null;
  responseBodyExcerpt: string | null;
  errorKind: string | null;
  errorMessage: string | null;
}

export interface Delivery {
  id: string;
  eventId: string;
  eventType: string;
  endpointId: string;
  endpointUrl: string;
  status: 'Pending' | 'Retrying' | 'Succeeded' | 'Exhausted' | 'Cancelled';
  attemptCount: number;
  nextAttemptAt: string | null;
  lastAttemptAt: string | null;
  succeededAt: string | null;
  createdAt: string;
  attempts: DeliveryAttempt[] | null;
}

export type SettlementStatus = 'Pending' | 'Paid' | 'Failed';

export interface SettlementItem {
  type: 'Payment' | 'Refund';
  sourceId: string;
  paymentId: string;
  gross: string;
  fee: string;
  net: string;
  completedAt: string;
}

export interface SettlementBatch {
  id: string;
  merchantId: string;
  currency: string;
  status: SettlementStatus;
  cutoffAt: string;
  paymentCount: number;
  refundCount: number;
  grossPayments: string;
  totalFees: string;
  totalRefunds: string;
  carriedForwardIn: string;
  netAmount: string;
  payoutAmount: string;
  carriedForwardOut: string;
  createdAt: string;
  paidAt: string | null;
  payoutReference: string | null;
  failureReason: string | null;
  simulated: boolean;
  items: SettlementItem[] | null;
}

export interface SettlementRun {
  cutoff: string;
  created: SettlementBatch[];
  skipped: { merchantId: string; currency: string; reason: string }[];
}

export type ReconciliationOutcome =
  | 'Matched'
  | 'MissingExternal'
  | 'MissingInternal'
  | 'AmountMismatch'
  | 'CurrencyMismatch'
  | 'DuplicateExternal'
  | 'StatusDiscrepancy';

export const DISCREPANCY_OUTCOMES: ReconciliationOutcome[] = [
  'MissingExternal',
  'MissingInternal',
  'AmountMismatch',
  'CurrencyMismatch',
  'DuplicateExternal',
  'StatusDiscrepancy',
];

export interface Statement {
  id: string;
  sourceName: string;
  periodStart: string;
  periodEnd: string;
  fileName: string;
  fileSha256: string;
  lineCount: number;
  status: 'Imported' | 'Reconciled';
  importedBy: string;
  importedAt: string;
  lastRunId: string | null;
}

export interface OutcomeCounts {
  matched: number;
  missingExternal: number;
  missingInternal: number;
  amountMismatch: number;
  currencyMismatch: number;
  duplicateExternal: number;
  statusDiscrepancy: number;
}

export interface ReconciliationRun {
  id: string;
  statementId: string;
  startedAt: string;
  completedAt: string;
  startedBy: string;
  counts: OutcomeCounts;
  exceptionsCreated: number;
  exceptionsSeenAgain: number;
}

export interface GeneratedStatement {
  sourceName: string;
  periodStart: string;
  periodEnd: string;
  fileName: string;
  lineCount: number;
  csv: string;
  injected: { outcome: ReconciliationOutcome; reference: string; detail: string }[];
}

export type ExceptionStatus = 'Open' | 'InReview' | 'Resolved' | 'Dismissed';

export type ResolutionType = 'AcceptedExternal' | 'AcceptedInternal' | 'AdjustmentPosted' | 'ExternalCorrectionRequested' | 'FalsePositive';

export interface ReconciliationException {
  id: string;
  outcome: ReconciliationOutcome;
  status: ExceptionStatus;
  statementId: string;
  reference: string | null;
  externalId: string | null;
  internalType: string | null;
  internalId: string | null;
  internalAmount: string | null;
  internalCurrency: string | null;
  externalAmount: string | null;
  externalCurrency: string | null;
  detail: string;
  assignedTo: string | null;
  resolutionType: ResolutionType | null;
  resolutionNote: string | null;
  adjustmentAmount: string | null;
  adjustmentDirection: 'IncreaseReceivable' | 'DecreaseReceivable' | null;
  resolvedBy: string | null;
  resolvedAt: string | null;
  timesSeen: number;
  createdAt: string;
  updatedAt: string;
  history: { from: ExceptionStatus | null; to: ExceptionStatus; note: string | null; actor: string; occurredAt: string }[] | null;
}

export interface Report {
  merchantId: string | null;
  currency: string;
  from: string;
  to: string;
  generatedAt: string;
  volume: {
    payments: number;
    succeeded: number;
    succeededAmount: string;
    days: { date: string; payments: number; succeeded: number; succeededAmount: string }[];
  };
  outcomes: {
    succeeded: number;
    reversed: number;
    declined: number;
    failed: number;
    expired: number;
    inFlight: number;
    successRate: number;
    declineRate: number;
    failureRate: number;
    topFailureCodes: { code: string; count: number }[];
  };
  refunds: { count: number; amount: string; failed: number; refundRate: number };
  settlements: { batches: number; pending: number; paid: number; failed: number; paidOut: string; awaitingPayout: string; carriedForward: string };
  reconciliation: { unresolved: number; byOutcome: { outcome: string; open: number }[]; byAge: { label: string; open: number }[] } | null;
  performance: {
    processedPayments: number;
    processingP50Ms: number | null;
    processingP95Ms: number | null;
    webhookAttempts: number;
    webhookSuccessRate: number;
    webhookP50Ms: number | null;
    webhookP95Ms: number | null;
  };
}

/** RFC 9457 problem details as PayFlow returns them. */
export interface Problem {
  title?: string;
  detail?: string;
  status?: number;
  code?: string;
  correlationId?: string;
  errors?: Record<string, string[]>;
}
