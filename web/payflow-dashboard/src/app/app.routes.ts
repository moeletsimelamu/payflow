import { Component, inject } from '@angular/core';
import { Router, RouterLink, Routes } from '@angular/router';
import { Auth, merchantAdmin, merchantMember, platformAdmin, signedIn } from './core/auth';

@Component({
  selector: 'pf-no-access',
  imports: [RouterLink],
  template: `<h1>No access</h1>
    <p>Your account has no access to that page. If you were just added to a merchant, sign out and sign in again.</p>
    <p><a routerLink="/">Go to your home page</a></p>`,
})
class NoAccessPage {}

@Component({
  selector: 'pf-not-found',
  imports: [RouterLink],
  template: `<h1>Page not found</h1>
    <p><a routerLink="/">Go to your home page</a></p>`,
})
class NotFoundPage {}

/** Sends each user to their own landing page (platform admins: merchants; members: their merchant). */
const home = async () => {
  // inject() only works synchronously: take both before the first await.
  const auth = inject(Auth);
  const router = inject(Router);
  await auth.ensureLoaded();
  return router.parseUrl(auth.homeUrl());
};

export const routes: Routes = [
  { path: 'login', title: 'Sign in · PayFlow', loadComponent: () => import('./pages/login').then((m) => m.LoginPage) },
  { path: 'accept-invitation', title: 'Set up your account · PayFlow', loadComponent: () => import('./pages/accept-invitation').then((m) => m.AcceptInvitationPage) },
  {
    path: '',
    loadComponent: () => import('./layout/shell').then((m) => m.Shell),
    canActivate: [signedIn],
    children: [
      { path: '', pathMatch: 'full', canActivate: [home], children: [] },
      {
        path: 'admin',
        canActivate: [platformAdmin],
        children: [
          { path: 'merchants', title: 'Merchants · PayFlow', loadComponent: () => import('./pages/admin/merchants').then((m) => m.MerchantsPage) },
          { path: 'merchants/new', title: 'Register a merchant · PayFlow', loadComponent: () => import('./pages/admin/register-merchant').then((m) => m.RegisterMerchantPage) },
          { path: 'settlements', title: 'Settlements · PayFlow', loadComponent: () => import('./pages/admin/settlements').then((m) => m.AdminSettlementsPage) },
          { path: 'reconciliation', title: 'Reconciliation · PayFlow', loadComponent: () => import('./pages/admin/reconciliation').then((m) => m.ReconciliationPage) },
          { path: 'reports', title: 'Platform reports · PayFlow', loadComponent: () => import('./pages/reports').then((m) => m.ReportsPage) },
        ],
      },
      {
        path: 'merchants/:merchantId',
        canActivate: [merchantMember],
        children: [
          { path: '', title: 'Overview · PayFlow', loadComponent: () => import('./pages/merchant/overview').then((m) => m.MerchantOverviewPage) },
          { path: 'payments', title: 'Transactions · PayFlow', loadComponent: () => import('./pages/merchant/payments').then((m) => m.PaymentsPage) },
          { path: 'payments/:paymentId', title: 'Transaction · PayFlow', loadComponent: () => import('./pages/merchant/payment-detail').then((m) => m.PaymentDetailPage) },
          { path: 'settlements', title: 'Settlements · PayFlow', loadComponent: () => import('./pages/merchant/settlements').then((m) => m.MerchantSettlementsPage) },
          { path: 'settlements/:settlementId', title: 'Settlement · PayFlow', loadComponent: () => import('./pages/merchant/settlements').then((m) => m.SettlementDetailPage) },
          { path: 'webhooks', title: 'Webhooks · PayFlow', canActivate: [merchantAdmin], loadComponent: () => import('./pages/merchant/webhooks').then((m) => m.WebhooksPage) },
          { path: 'api-keys', title: 'API keys · PayFlow', canActivate: [merchantAdmin], loadComponent: () => import('./pages/merchant/api-keys').then((m) => m.ApiKeysPage) },
          { path: 'team', title: 'Team · PayFlow', canActivate: [merchantAdmin], loadComponent: () => import('./pages/merchant/members').then((m) => m.MembersPage) },
          { path: 'reports', title: 'Reports · PayFlow', loadComponent: () => import('./pages/reports').then((m) => m.ReportsPage) },
        ],
      },
      { path: 'no-access', component: NoAccessPage, title: 'No access · PayFlow' },
      { path: '**', component: NotFoundPage, title: 'Not found · PayFlow' },
    ],
  },
];
