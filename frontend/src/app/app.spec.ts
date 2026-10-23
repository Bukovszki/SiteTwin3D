import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Title } from '@angular/platform-browser';
import { provideRouter } from '@angular/router';
import { ConfirmationService, MessageService } from 'primeng/api';
import { App } from './app';
import { errorInterceptor } from './core/api/errors';
import { MESSAGES_HU } from './core/i18n/messages.hu';

describe('App', () => {
  let http: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [
        provideRouter([]),
        provideHttpClient(withInterceptors([errorInterceptor])),
        provideHttpClientTesting(),
        MessageService,
        ConfirmationService,
      ],
    }).compileComponents();
    http = TestBed.inject(HttpTestingController);
  });

  it('shows the sign-in screen without a session, without an error toast', async () => {
    const fixture = TestBed.createComponent(App);
    const toasts = TestBed.inject(MessageService);
    let toasted = false;
    toasts.messageObserver.subscribe(() => (toasted = true));

    http.expectOne('/api/v1/auth/me').flush({ code: 'x' }, { status: 401, statusText: 'Unauthorized' });
    await fixture.whenStable();
    fixture.detectChanges();

    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('app-login-screen')).not.toBeNull();
    expect(element.textContent).toContain(MESSAGES_HU.auth.title);
    expect(toasted).toBe(false);
  });

  it('asks for a new password after a temporary one', async () => {
    const fixture = TestBed.createComponent(App);

    http.expectOne('/api/v1/auth/me').flush({
      id: 1, username: 'admin', displayName: 'Admin', role: 'OrgAdmin', organizationId: 1,
      organizationCode: 'demo', organizationName: 'Demo', mustChangePassword: true,
    });
    await fixture.whenStable();
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain(MESSAGES_HU.auth.changeTitle);
  });

  it('sets the document title from the message resource', () => {
    TestBed.createComponent(App);
    http.expectOne('/api/v1/auth/me').flush(null, { status: 401, statusText: 'Unauthorized' });
    expect(TestBed.inject(Title).getTitle()).toBe(MESSAGES_HU.app.productName);
  });

  afterEach(() => http.verify({ ignoreCancelled: true }));
});
