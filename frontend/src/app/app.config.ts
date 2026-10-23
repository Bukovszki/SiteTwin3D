import { registerLocaleData } from '@angular/common';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import localeHu from '@angular/common/locales/hu';
import { ApplicationConfig, LOCALE_ID, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideRouter, withComponentInputBinding, withDisabledInitialNavigation } from '@angular/router';
import Aura from '@primeuix/themes/aura';
import { ConfirmationService, MessageService } from 'primeng/api';
import { providePrimeNG } from 'primeng/config';
import { routes } from './app.routes';
import { errorInterceptor } from './core/api/errors';

registerLocaleData(localeHu);

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    // The first navigation waits for the sign-in (App): the mode guards need the user's permissions.
    provideRouter(routes, withComponentInputBinding(), withDisabledInitialNavigation()),
    provideHttpClient(withInterceptors([errorInterceptor])),
    { provide: LOCALE_ID, useValue: 'hu' },
    MessageService,
    ConfirmationService,
    // Same theme as the MapManager UI (NFR-8).
    providePrimeNG({
      theme: { preset: Aura, options: { darkModeSelector: false } },
      translation: {
        accept: 'Igen',
        reject: 'Nem',
        choose: 'Kiválasztás',
        cancel: 'Mégse',
        emptyMessage: 'Nincs találat',
        emptyFilterMessage: 'Nincs találat',
        emptySelectionMessage: 'Nincs kiválasztva',
      },
    }),
  ],
};
