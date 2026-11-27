import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule } from '@angular/material/dialog';
import { firstValueFrom } from 'rxjs';

export interface ConfirmData {
  title: string;
  message: string;
  confirmLabel: string;
  /** Destructive actions get the warning colour. */
  destructive?: boolean;
}

/** An accessible confirmation (never window.confirm): focus starts on Cancel, the safe choice. */
@Component({
  selector: 'pf-confirm',
  imports: [MatDialogModule, MatButtonModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>{{ data.title }}</h2>
    <mat-dialog-content><p>{{ data.message }}</p></mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button type="button" [mat-dialog-close]="false" cdkFocusInitial>Cancel</button>
      <button mat-flat-button type="button" [mat-dialog-close]="true" [class.pf-danger]="data.destructive">{{ data.confirmLabel }}</button>
    </mat-dialog-actions>
  `,
})
export class ConfirmDialog {
  protected readonly data = inject<ConfirmData>(MAT_DIALOG_DATA);
}

export async function confirmAction(dialog: MatDialog, data: ConfirmData): Promise<boolean> {
  return (await firstValueFrom(dialog.open(ConfirmDialog, { data, width: '440px', restoreFocus: true }).afterClosed())) === true;
}
