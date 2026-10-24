import { Component, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Button } from 'primeng/button';
import { Dialog } from 'primeng/dialog';
import { InputText } from 'primeng/inputtext';
import { Textarea } from 'primeng/textarea';
import { MESSAGES } from '../i18n/messages';
import { ChangeReasonService, today } from './change-reason';

/** Reason and valid-from day of a change of a finalized record (asked by the change-reason interceptor). */
@Component({
  selector: 'app-change-reason-dialog',
  imports: [FormsModule, Button, Dialog, InputText, Textarea],
  template: `
    <p-dialog
      [header]="m.history.reasonTitle"
      [visible]="reasons.open()"
      (visibleChange)="!$event && reasons.answer(null)"
      [modal]="true"
      [style]="{ width: '30rem' }"
      [closable]="true"
    >
      <div class="form-grid">
        <small class="muted">{{ m.history.reasonHint }}</small>
        <div class="field">
          <label for="change-reason">{{ m.history.reason }}</label>
          <textarea
            pTextarea
            id="change-reason"
            rows="3"
            [ngModel]="reasons.reason()"
            (ngModelChange)="reasons.reason.set($event)"
            autofocus
          ></textarea>
        </div>
        <div class="field">
          <label for="change-valid-from">{{ m.history.validFrom }}</label>
          <input
            pInputText
            id="change-valid-from"
            type="date"
            [max]="max"
            [ngModel]="reasons.validFrom()"
            (ngModelChange)="reasons.validFrom.set($event)"
          />
          <small class="muted">{{ m.history.validFromHint }}</small>
        </div>
      </div>
      <ng-template #footer>
        <p-button
          [label]="m.common.cancel"
          severity="secondary"
          [text]="true"
          (onClick)="reasons.answer(null)"
        />
        <p-button
          [label]="m.history.record"
          icon="pi pi-check"
          [disabled]="!reasons.reason().trim() || !reasons.validFrom()"
          (onClick)="
            reasons.answer({ reason: reasons.reason().trim(), validFrom: reasons.validFrom() })
          "
        />
      </ng-template>
    </p-dialog>
  `,
})
export class ChangeReasonDialog {
  protected readonly m = inject(MESSAGES);
  protected readonly reasons = inject(ChangeReasonService);
  protected readonly max = today();
}
