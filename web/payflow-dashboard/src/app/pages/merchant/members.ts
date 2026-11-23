import { ChangeDetectionStrategy, Component, OnInit, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { Api, ApiError } from '../../core/api';
import { Auth } from '../../core/auth';
import { invitationLink } from '../../core/invitation-link';
import { loadable } from '../../core/loadable';
import { Member, MemberRole } from '../../core/models';
import { confirmAction } from '../../shared/confirm-dialog';
import { UtcPipe } from '../../shared/pipes';
import { SecretPanel } from '../../shared/secret-panel';
import { StateView } from '../../shared/state';

/** The merchant's users and roles (MER-3). The API keeps at least one MerchantAdmin. */
@Component({
  selector: 'pf-members',
  imports: [FormsModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule, StateView, SecretPanel, UtcPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="pf-page-header">
      <h1>Team</h1>
      <p class="pf-muted">Merchant admins manage keys, webhooks and the team; operators view transactions and refund up to the operator limit.</p>
    </header>

    @if (invitation(); as i) {
      <pf-secret [title]="'Invitation link for ' + i.email" [value]="i.link" notice="Send it to them yourself; PayFlow sends no e-mail. It works once and expires in 3 days." (dismissed)="invitation.set(null)" />
    }

    <section class="pf-card" aria-labelledby="invite">
      <h2 id="invite">Invite someone</h2>
      <form class="pf-filters" (ngSubmit)="invite()" aria-label="Invite a team member">
        <mat-form-field appearance="outline">
          <mat-label>Email</mat-label>
          <input matInput type="email" name="email" [ngModel]="email()" (ngModelChange)="email.set($event)" required />
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label>Name</mat-label>
          <input matInput name="displayName" [ngModel]="displayName()" (ngModelChange)="displayName.set($event)" required />
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label>Role</mat-label>
          <mat-select name="role" [ngModel]="role()" (ngModelChange)="role.set($event)">
            <mat-option value="MerchantOperator">Operator</mat-option>
            <mat-option value="MerchantAdmin">Merchant admin</mat-option>
          </mat-select>
        </mat-form-field>
        <button mat-flat-button type="submit" [disabled]="busy()">Invite</button>
      </form>
      @if (message(); as m) {
        <p [class]="failed() ? 'pf-error' : 'pf-notice'" role="status">{{ m }}</p>
      }
    </section>

    <section class="pf-card" aria-labelledby="team">
      <h2 id="team">Members</h2>
      <pf-state [of]="members" emptyText="No members yet.">
        <div class="pf-table-wrap">
          <table class="pf-table">
            <caption class="pf-visually-hidden">Team members</caption>
            <thead><tr><th scope="col">Name</th><th scope="col">Email</th><th scope="col">Role</th><th scope="col">Invitation</th><th scope="col">Added</th><th scope="col"><span class="pf-visually-hidden">Actions</span></th></tr></thead>
            <tbody>
              @for (m of members.value(); track m.userId) {
                <tr>
                  <td>{{ m.displayName }}@if (m.userId === me()) { <span class="pf-muted"> (you)</span> }</td>
                  <td>{{ m.email }}</td>
                  <td>
                    <label class="pf-visually-hidden" [for]="'role-' + m.userId">Role of {{ m.displayName }}</label>
                    <select [id]="'role-' + m.userId" [value]="m.role" (change)="changeRole(m, $any($event.target).value)" [disabled]="busy()">
                      <option value="MerchantAdmin">Merchant admin</option>
                      <option value="MerchantOperator">Operator</option>
                    </select>
                  </td>
                  <td>{{ m.hasAcceptedInvitation ? 'Accepted' : 'Pending' }}</td>
                  <td class="pf-when">{{ m.addedAt | utc: false }}</td>
                  <td><button mat-button type="button" class="pf-danger-text" (click)="remove(m)" [disabled]="busy()">Remove</button></td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      </pf-state>
    </section>
  `,
})
export class MembersPage implements OnInit {
  private readonly api = inject(Api);
  private readonly auth = inject(Auth);
  private readonly dialog = inject(MatDialog);
  readonly merchantId = input.required<string>();

  protected readonly members = loadable(() => this.api.members(this.merchantId()), (list) => list.length === 0);
  protected readonly email = signal('');
  protected readonly displayName = signal('');
  protected readonly role = signal<MemberRole>('MerchantOperator');
  protected readonly invitation = signal<{ email: string; link: string } | null>(null);
  protected readonly busy = signal(false);
  protected readonly message = signal<string | null>(null);
  protected readonly failed = signal(false);
  protected readonly me = () => this.auth.user()?.id;

  ngOnInit(): void {
    void this.members.load();
  }

  protected async invite(): Promise<void> {
    const email = this.email().trim();
    const displayName = this.displayName().trim();
    if (!email || !displayName) {
      this.report('Enter their email and name.', true);
      return;
    }
    await this.run(async () => {
      const result = await this.api.inviteMember(this.merchantId(), { email, displayName, role: this.role() });
      if (result.invitationToken) {
        this.invitation.set({ email: result.email, link: invitationLink(location.origin, result.email, result.invitationToken) });
        this.report(`${email} invited. Send them the invitation link.`, false);
      } else {
        this.report(`${email} already has a PayFlow account and now has access to this merchant.`, false);
      }
      this.email.set('');
      this.displayName.set('');
    });
  }

  protected async changeRole(member: Member, role: MemberRole): Promise<void> {
    if (role === member.role) {
      return;
    }
    await this.run(async () => {
      await this.api.changeMemberRole(this.merchantId(), member.userId, role);
      this.report(`${member.displayName} is now ${role === 'MerchantAdmin' ? 'a merchant admin' : 'an operator'}.`, false);
    });
  }

  protected async remove(member: Member): Promise<void> {
    const ok = await confirmAction(this.dialog, {
      title: `Remove ${member.displayName}?`,
      message: `${member.email} loses access to this merchant within a minute.`,
      confirmLabel: 'Remove',
      destructive: true,
    });
    if (ok) {
      await this.run(async () => {
        await this.api.removeMember(this.merchantId(), member.userId);
        this.report(`${member.displayName} removed.`, false);
      });
    }
  }

  private async run(work: () => Promise<void>): Promise<void> {
    this.busy.set(true);
    try {
      await work();
    } catch (e) {
      const error = ApiError.from(e);
      this.report(error.code === 'last_merchant_admin' ? 'A merchant needs at least one merchant admin.' : error.message, true);
    } finally {
      this.busy.set(false);
      void this.members.load(); // also resets a role select the API refused
    }
  }

  private report(text: string, failed: boolean): void {
    this.failed.set(failed);
    this.message.set(text);
  }
}
