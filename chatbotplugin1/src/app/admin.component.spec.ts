import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { vi } from 'vitest';
import { AdminComponent } from './admin.component';
import { AuthService } from './auth.service';

describe('AdminComponent', () => {
  let http: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [AdminComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: AuthService,
          useValue: {
            signedIn: signal(true), ready: signal(true), error: signal(''),
            initialize: () => Promise.resolve(),
            getToken: () => Promise.resolve('test-token'),
          },
        },
      ],
    }).compileComponents();
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    http.verify();
    vi.restoreAllMocks();
  });

  it('loads settings and saves a shared temperature, then restores the default', async () => {
    const fixture = TestBed.createComponent(AdminComponent);
    fixture.detectChanges();
    await Promise.resolve();
    await Promise.resolve();
    const request = http.expectOne('/api/admin/model-settings');
    expect(request.request.headers.get('Authorization')).toBe('Bearer test-token');
    request.flush({ temperature: null });
    await fixture.whenStable();
    fixture.detectChanges();

    const element = fixture.nativeElement as HTMLElement;
    const useDefault = element.querySelector<HTMLInputElement>('input[type=checkbox]')!;
    useDefault.click();
    fixture.detectChanges();
    const input = element.querySelector<HTMLInputElement>('#temperature')!;
    input.value = '0.7';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    element.querySelector('form')!.dispatchEvent(new Event('submit'));
    await Promise.resolve();
    const update = http.expectOne('/api/admin/model-settings');
    expect(update.request.method).toBe('PUT');
    expect(update.request.body).toEqual({ temperature: 0.7 });
    update.flush({ temperature: 0.7 });
    await fixture.whenStable();
    fixture.detectChanges();
    expect(element.textContent).toContain('Settings saved');

    useDefault.click();
    fixture.detectChanges();
    element.querySelector('form')!.dispatchEvent(new Event('submit'));
    await Promise.resolve();
    const reset = http.expectOne('/api/admin/model-settings');
    expect(reset.request.body).toEqual({ temperature: null });
    reset.flush({ temperature: null });
    await fixture.whenStable();
  });

  it('does not expose settings when the API denies access', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});
    const fixture = TestBed.createComponent(AdminComponent);
    fixture.detectChanges();
    await Promise.resolve();
    await Promise.resolve();
    http.expectOne('/api/admin/model-settings').flush({}, { status: 403, statusText: 'Forbidden' });
    await fixture.whenStable();
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('[role=alert]')?.textContent).toContain('not authorized');
    expect(element.querySelector('form')).toBeNull();
  });
});
