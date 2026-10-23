import { HttpContext, HttpContextToken, HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { MessageService } from 'primeng/api';
import { catchError, throwError } from 'rxjs';
import { AuthService } from '../auth/auth.service';
import { MESSAGES } from '../i18n/messages';
import { Messages } from '../i18n/messages.hu';

/** When set, errors are not toasted: the caller (e.g. a dialog) shows them itself. */
export const SILENT_ERRORS = new HttpContextToken<boolean>(() => false);

export const silent = () => new HttpContext().set(SILENT_ERRORS, true);

/** The machine-readable code of an API problem response ("node.cycle"), if any. */
export function errorCode(error: unknown): string | null {
  return error instanceof HttpErrorResponse && typeof error.error?.code === 'string' ? error.error.code : null;
}

/** The Hungarian text of an API error: by its code from the message resource, else a generic one. */
export function errorMessage(error: unknown, messages: Messages): string {
  if (error instanceof HttpErrorResponse) {
    if (error.status === 0) return messages.errors['network'];
    const code = errorCode(error);
    if (code && messages.errors[code]) return messages.errors[code];
    if (error.status === 403) return messages.errors['forbidden'];
  }
  return messages.errors['unknown'];
}

/**
 * Toasts every API error; the caller still gets it. A 401 outside the sign-in endpoints means the session ended:
 * the sign-in screen appears instead of a toast.
 */
export const errorInterceptor: HttpInterceptorFn = (req, next) => {
  const toasts = inject(MessageService);
  const auth = inject(AuthService);
  const messages = inject(MESSAGES);
  return next(req).pipe(
    catchError((error: unknown) => {
      if (error instanceof HttpErrorResponse && error.status === 401 && !req.url.includes('/auth/')) {
        auth.sessionExpired();
        return throwError(() => error);
      }
      if (!req.context.get(SILENT_ERRORS)) {
        toasts.add({ severity: 'error', summary: messages.common.error, detail: errorMessage(error, messages), life: 8000 });
      }
      return throwError(() => error);
    }),
  );
};
