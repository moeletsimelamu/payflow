import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { AbstractControl, FormArray, FormBuilder, ReactiveFormsModule, ValidationErrors, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { RouterLink } from '@angular/router';
import { Api, ApiError } from '../../core/api';
import { invitationLink } from '../../core/invitation-link';
import { FeePlan, RegisteredMerchant } from '../../core/models';
import { SUPPORTED_CURRENCIES, validateAmount } from '../../core/money';
import { SecretPanel } from '../../shared/secret-panel';

/** A form-level amount check in the row's currency (BigInt, never floating point). */
function amountIn(row: AbstractControl, field: string, optional: boolean): ValidationErrors | null {
  const value = String(row.get(field)?.value ?? '');
  if (optional && value.trim() === '') {
    return null;
  }
  return validateAmount(value, String(row.get('currency')?.value ?? 'ZAR')) === null ? null : { [field]: true };
}

/** Register a merchant (MER-1): legal details, currencies and limits, and optionally its first admin. */
@Component({
  selector: 'pf-register-merchant',
  imports: [ReactiveFormsModule, RouterLink, MatFormFieldModule, MatInputModule, MatSelectModule, MatButtonModule, SecretPanel],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <p><a routerLink="/admin/merchants">← Merchants</a></p>
    <header class="pf-page-header">
      <h1>Register a merchant</h1>
      <p class="pf-muted">The merchant starts as Pending activation. Activate it from its overview once checked.</p>
    </header>

    @if (registered(); as r) {
      <section class="pf-card" role="status" aria-labelledby="done">
        <h2 id="done">{{ r.merchant.tradingName }} is registered</h2>
        <p>Merchant id <code>{{ r.merchant.id }}</code>, status Pending activation.</p>
        @if (invitationLink(); as link) {
          <pf-secret
            title="Invitation link for {{ r.adminInvitation?.email }}"
            [value]="link"
            notice="Send it to the merchant's admin yourself; PayFlow sends no e-mail. It works once and expires in 3 days."
            (dismissed)="invitationLink.set(null)"
          />
        }
        <p><a [routerLink]="['/merchants', r.merchant.id]">Open the merchant</a> · <button mat-button type="button" (click)="reset()">Register another</button></p>
      </section>
    } @else {
      <form [formGroup]="form" (ngSubmit)="submit()" novalidate class="pf-card">
        <h2>Business</h2>
        <div class="pf-filters">
          <mat-form-field appearance="outline">
            <mat-label>Legal name</mat-label>
            <input matInput formControlName="legalName" required />
          </mat-form-field>
          <mat-form-field appearance="outline">
            <mat-label>Trading name</mat-label>
            <input matInput formControlName="tradingName" required />
          </mat-form-field>
          <mat-form-field appearance="outline">
            <mat-label>Contact email</mat-label>
            <input matInput type="email" formControlName="contactEmail" required />
          </mat-form-field>
          <mat-form-field appearance="outline" class="pf-narrow">
            <mat-label>Country (ISO)</mat-label>
            <input matInput formControlName="countryCode" maxlength="2" required />
            <mat-hint>e.g. ZA</mat-hint>
          </mat-form-field>
          <mat-form-field appearance="outline">
            <mat-label>Fee plan</mat-label>
            <mat-select formControlName="feePlanId">
              <mat-option [value]="null">Default (Standard)</mat-option>
              @for (p of feePlans(); track p.id) {
                <mat-option [value]="p.id">{{ p.name }} v{{ p.version }} ({{ p.percentageRate }})</mat-option>
              }
            </mat-select>
          </mat-form-field>
        </div>

        <h2>Currencies and limits</h2>
        <div formArrayName="currencies">
          @for (row of currencies.controls; track $index; let i = $index) {
            <fieldset class="pf-fieldset pf-filters" [formGroupName]="i">
              <legend>Currency {{ i + 1 }}</legend>
              <mat-form-field appearance="outline" class="pf-narrow">
                <mat-label>Currency</mat-label>
                <mat-select formControlName="currency">
                  @for (c of supported; track c) {
                    <mat-option [value]="c">{{ c }}</mat-option>
                  }
                </mat-select>
              </mat-form-field>
              <mat-form-field appearance="outline">
                <mat-label>Max payment</mat-label>
                <input matInput inputmode="decimal" formControlName="maxPaymentAmount" required />
              </mat-form-field>
              <mat-form-field appearance="outline">
                <mat-label>Operator refund limit (optional)</mat-label>
                <input matInput inputmode="decimal" formControlName="operatorRefundLimit" />
              </mat-form-field>
              @if (row.hasError('maxPaymentAmount') && row.touched) {
                <p class="pf-error" role="alert">Max payment must be a positive {{ row.value.currency }} amount.</p>
              }
              @if (row.hasError('operatorRefundLimit') && row.touched) {
                <p class="pf-error" role="alert">The operator limit must be a positive {{ row.value.currency }} amount.</p>
              }
              @if (currencies.length > 1) {
                <button mat-button type="button" (click)="currencies.removeAt(i)">Remove {{ row.value.currency }}</button>
              }
            </fieldset>
          }
        </div>
        <button mat-stroked-button type="button" (click)="addCurrency()">Add a currency</button>
        @if (duplicateCurrency()) {
          <p class="pf-error" role="alert">Each currency can be listed once.</p>
        }

        <h2>First merchant admin (optional)</h2>
        <div class="pf-filters">
          <mat-form-field appearance="outline">
            <mat-label>Email</mat-label>
            <input matInput type="email" formControlName="adminEmail" />
          </mat-form-field>
          <mat-form-field appearance="outline">
            <mat-label>Name</mat-label>
            <input matInput formControlName="adminDisplayName" />
          </mat-form-field>
        </div>

        @if (error(); as message) {
          <div class="pf-error" role="alert">
            <p>{{ message }}</p>
            @if (fieldErrors().length) {
              <ul>
                @for (e of fieldErrors(); track $index) {
                  <li>{{ e }}</li>
                }
              </ul>
            }
          </div>
        }
        <button mat-flat-button type="submit" [disabled]="busy()">{{ busy() ? 'Registering…' : 'Register merchant' }}</button>
      </form>
    }
  `,
})
export class RegisterMerchantPage implements OnInit {
  private readonly api = inject(Api);
  private readonly fb = inject(FormBuilder).nonNullable;
  protected readonly supported = SUPPORTED_CURRENCIES;
  protected readonly feePlans = signal<FeePlan[]>([]);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly fieldErrors = signal<string[]>([]);
  protected readonly registered = signal<RegisteredMerchant | null>(null);
  protected readonly invitationLink = signal<string | null>(null);

  protected readonly form = this.fb.group({
    legalName: ['', [Validators.required, Validators.maxLength(200)]],
    tradingName: ['', [Validators.required, Validators.maxLength(200)]],
    contactEmail: ['', [Validators.required, Validators.email]],
    countryCode: ['ZA', [Validators.required, Validators.pattern(/^[A-Za-z]{2}$/)]],
    feePlanId: this.fb.control<string | null>(null),
    currencies: this.fb.array([this.currencyRow('ZAR', '50000.00', '1000.00')]),
    adminEmail: ['', Validators.email],
    adminDisplayName: [''],
  });

  get currencies(): FormArray {
    return this.form.controls.currencies;
  }

  ngOnInit(): void {
    this.api.feePlans().then((plans) => this.feePlans.set(plans), () => undefined);
  }

  protected addCurrency(): void {
    const used = new Set(this.currencies.controls.map((c) => c.value.currency));
    this.currencies.push(this.currencyRow(SUPPORTED_CURRENCIES.find((c) => !used.has(c)) ?? 'USD', '', ''));
  }

  protected duplicateCurrency(): boolean {
    const codes = this.currencies.controls.map((c) => c.value.currency);
    return new Set(codes).size !== codes.length;
  }

  protected async submit(): Promise<void> {
    if (this.form.invalid || this.duplicateCurrency()) {
      this.form.markAllAsTouched();
      this.error.set('Check the highlighted fields.');
      this.fieldErrors.set([]);
      return;
    }
    const v = this.form.getRawValue();
    const adminEmail = v.adminEmail.trim();
    this.busy.set(true);
    this.error.set(null);
    this.fieldErrors.set([]);
    try {
      const result = await this.api.registerMerchant({
        legalName: v.legalName.trim(),
        tradingName: v.tradingName.trim(),
        contactEmail: v.contactEmail.trim(),
        countryCode: v.countryCode.trim().toUpperCase(),
        feePlanId: v.feePlanId,
        currencies: v.currencies.map((c: { currency: string; maxPaymentAmount: string; operatorRefundLimit: string }) => ({
          currency: c.currency,
          maxPaymentAmount: c.maxPaymentAmount.trim(),
          operatorRefundLimit: c.operatorRefundLimit.trim() || null,
        })),
        adminEmail: adminEmail || null,
        adminDisplayName: adminEmail ? v.adminDisplayName.trim() || adminEmail : null,
      });
      this.registered.set(result);
      const invite = result.adminInvitation;
      if (invite?.invitationToken) {
        this.invitationLink.set(invitationLink(location.origin, invite.email, invite.invitationToken));
      }
    } catch (e) {
      const error = ApiError.from(e);
      this.error.set(error.message);
      this.fieldErrors.set(Object.entries(error.problem.errors ?? {}).map(([field, problems]) => `${field}: ${problems.join(' ')}`));
    } finally {
      this.busy.set(false);
    }
  }

  protected reset(): void {
    this.registered.set(null);
    this.invitationLink.set(null);
    this.form.reset();
    this.currencies.clear();
    this.currencies.push(this.currencyRow('ZAR', '50000.00', '1000.00'));
  }

  private currencyRow(currency: string, max: string, limit: string) {
    return this.fb.group(
      { currency: [currency, Validators.required], maxPaymentAmount: [max, Validators.required], operatorRefundLimit: [limit] },
      { validators: [(g: AbstractControl) => amountIn(g, 'maxPaymentAmount', false), (g: AbstractControl) => amountIn(g, 'operatorRefundLimit', true)] },
    );
  }
}
