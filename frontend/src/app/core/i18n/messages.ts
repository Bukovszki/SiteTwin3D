import { InjectionToken } from '@angular/core';
import { MESSAGES_HU, Messages } from './messages.hu';

/** UI texts of the active language; inject with `inject(MESSAGES)`. */
export const MESSAGES = new InjectionToken<Messages>('MESSAGES', {
  providedIn: 'root',
  factory: () => MESSAGES_HU,
});
