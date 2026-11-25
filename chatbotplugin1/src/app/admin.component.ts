import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { AuthService } from './auth.service';

interface ModelSettings {
  temperature: number | null;
}

@Component({
  selector: 'app-admin',
  imports: [FormsModule],
  templateUrl: './admin.component.html',
  styleUrl: './admin.component.css',
})
export class AdminComponent {
  protected readonly auth = inject(AuthService);
  private readonly http = inject(HttpClient);
  protected readonly busy = signal(false);
  protected readonly loaded = signal(false);
  protected readonly error = signal('');
  protected readonly notice = signal('');
  protected temperature: number | null = null;
  protected useDefault = true;

  constructor() {
    void this.initialize();
  }

  private async initialize(): Promise<void> {
    await this.auth.initialize();
    if (this.auth.signedIn()) await this.load();
  }

  protected async signIn(): Promise<void> {
    this.busy.set(true);
    this.error.set('');
    try {
      await this.auth.signIn();
    } catch (error) {
      console.error('Sign-in failed.', error);
      this.error.set('Sign-in failed. Please try again.');
      this.busy.set(false);
      return;
    }
    this.busy.set(false);
    await this.load();
  }

  protected async signOut(): Promise<void> {
    this.busy.set(true);
    try {
      await this.auth.signOut();
      this.loaded.set(false);
      this.error.set('');
      this.notice.set('');
    } catch (error) {
      console.error('Sign-out failed.', error);
      this.error.set('Sign-out failed. Please try again.');
    } finally {
      this.busy.set(false);
    }
  }

  protected async load(): Promise<void> {
    this.busy.set(true);
    this.error.set('');
    this.notice.set('');
    try {
      const token = await this.auth.getToken();
      const settings = await firstValueFrom(this.http.get<ModelSettings>('/api/admin/model-settings', {
        headers: { Authorization: `Bearer ${token}` },
      }));
      this.applySettings(settings);
      this.loaded.set(true);
    } catch (error) {
      this.loaded.set(false);
      this.showError(error, 'Model settings could not be loaded.');
    } finally {
      this.busy.set(false);
    }
  }

  protected async save(): Promise<void> {
    if (!this.useDefault && (this.temperature === null || !Number.isFinite(this.temperature) ||
      this.temperature < 0 || this.temperature > 2)) {
      this.error.set('Enter a temperature between 0 and 2.');
      return;
    }
    this.busy.set(true);
    this.error.set('');
    this.notice.set('');
    try {
      const token = await this.auth.getToken();
      const settings = await firstValueFrom(this.http.put<ModelSettings>('/api/admin/model-settings',
        { temperature: this.useDefault ? null : this.temperature },
        { headers: { Authorization: `Bearer ${token}` } }));
      this.applySettings(settings);
      this.notice.set('Settings saved for future chat requests.');
    } catch (error) {
      this.showError(error, 'Model settings could not be saved.');
    } finally {
      this.busy.set(false);
    }
  }

  private applySettings(settings: ModelSettings): void {
    if (!settings || (settings.temperature !== null &&
      (typeof settings.temperature !== 'number' || !Number.isFinite(settings.temperature) ||
        settings.temperature < 0 || settings.temperature > 2))) {
      throw new Error('The server returned invalid model settings.');
    }
    this.temperature = settings.temperature;
    this.useDefault = settings.temperature === null;
  }

  private showError(error: unknown, fallback: string): void {
    console.error(fallback, error);
    if (error instanceof HttpErrorResponse && error.status === 403) {
      this.error.set('Your account is not authorized to manage model settings.');
    } else if (error instanceof HttpErrorResponse && typeof error.error?.error === 'string') {
      this.error.set(error.error.error);
    } else {
      this.error.set(error instanceof Error && !(error instanceof HttpErrorResponse) ? error.message : fallback);
    }
  }
}
