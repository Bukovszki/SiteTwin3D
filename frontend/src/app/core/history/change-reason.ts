import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { catchError, from, switchMap, throwError } from 'rxjs';
import { errorCode } from '../api/errors';

export interface ChangeReason {
  reason: string;
  /** yyyy-MM-dd; since when the change is so in reality. */
  validFrom: string;
}

export const REASON_HEADER = 'X-Change-Reason';
export const VALID_FROM_HEADER = 'X-Change-Valid-From';

/** Today in the user's time zone, yyyy-MM-dd. */
export const today = () => new Date().toLocaleDateString('sv-SE');

/**
 * Asks for the reason and valid-from day of a change of a finalized record (one question for requests running at
 * the same time, e.g. the moves of an editing session saved together). The dialog in the app root shows `open`.
 */
@Injectable({ providedIn: 'root' })
export class ChangeReasonService {
  readonly open = signal(false);
  readonly reason = signal('');
  readonly validFrom = signal(today());
  private pending: Promise<ChangeReason | null> | null = null;
  private resolve?: (answer: ChangeReason | null) => void;

  ask(): Promise<ChangeReason | null> {
    if (this.pending) return this.pending;
    this.reason.set('');
    this.validFrom.set(today());
    this.open.set(true);
    this.pending = new Promise((resolve) => (this.resolve = resolve));
    return this.pending;
  }

  answer(answer: ChangeReason | null): void {
    this.open.set(false);
    this.resolve?.(answer);
    this.pending = null;
    this.resolve = undefined;
  }
}

/**
 * A change of a finalized record answered with 428 change.reason_required: the reason is asked for and the request
 * repeated with it. Cancelling ends in a change.cancelled error (the error interceptor shows it). Must be inside the
 * error interceptor, so that the first 428 is not toasted.
 */
export const changeReasonInterceptor: HttpInterceptorFn = (req, next) => {
  const reasons = inject(ChangeReasonService);
  return next(req).pipe(
    catchError((error: unknown) => {
      if (
        !(error instanceof HttpErrorResponse) ||
        error.status !== 428 ||
        errorCode(error) !== 'change.reason_required' ||
        req.headers.has(REASON_HEADER)
      )
        return throwError(() => error);
      return from(reasons.ask()).pipe(
        switchMap((answer) =>
          answer
            ? next(
                req.clone({
                  setHeaders: {
                    [REASON_HEADER]: encodeURIComponent(answer.reason),
                    [VALID_FROM_HEADER]: answer.validFrom,
                  },
                }),
              )
            : throwError(
                () =>
                  new HttpErrorResponse({
                    status: 428,
                    url: req.url,
                    error: { code: 'change.cancelled' },
                  }),
              ),
        ),
      );
    }),
  );
};
