import { bootstrapApplication } from '@angular/platform-browser';
import { appConfig } from './app/app.config';
import { App } from './app/app';

// Cesium loads its workers, assets and widgets from here (copied by angular.json). Must be set before the
// (lazy-loaded) Cesium chunk is imported.
(window as unknown as { CESIUM_BASE_URL: string }).CESIUM_BASE_URL = '/cesium/';

bootstrapApplication(App, appConfig).catch((err) => console.error(err));
