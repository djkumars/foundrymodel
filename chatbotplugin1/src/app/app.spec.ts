import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { of, Subject } from 'rxjs';
import { vi } from 'vitest';
import { App } from './app';
import { ChatMessage, ChatService, ChatStreamError, ChatStreamEvent } from './chat.service';
import { AuthService } from './auth.service';

describe('App', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [App] }).compileComponents();
  });

  it('should create the app', () => {
    const fixture = TestBed.createComponent(App);
    expect(fixture.componentInstance).toBeTruthy();
  });

  it('should render the normal home page with a chat launcher', async () => {
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.querySelector('h1')?.textContent).toContain('Make room for');
    expect(compiled.querySelector('.chat-launcher')).toBeTruthy();
    expect(compiled.querySelector('.chat-popover iframe')?.getAttribute('src')).toContain('/chat');
  });
});

describe('App streaming chat', () => {
  let chunks: Subject<ChatStreamEvent>;
  let history: ChatMessage[];
  let submittedIds: string[];
  let earlierHistory: ChatMessage[];
  let searched: string[];
  let signedIn: ReturnType<typeof signal<boolean>>;

  beforeEach(async () => {
    chunks = new Subject<ChatStreamEvent>();
    history = [];
    submittedIds = [];
    earlierHistory = [];
    searched = [];
    signedIn = signal(true);
    window.history.replaceState({}, '', '/chat');
    vi.spyOn(window, 'requestAnimationFrame').mockImplementation(() => 0);
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [
        {
          provide: AuthService,
          useValue: {
            signedIn, ready: signal(true), error: signal(''),
            initialize: () => Promise.resolve(),
            signIn: () => { signedIn.set(true); return Promise.resolve(); },
            signOut: () => { signedIn.set(false); return Promise.resolve(); },
          },
        },
        {
          provide: ChatService,
          useValue: {
            loadConversation: (_id: string, token?: string) => of({
              conversationId: 'id',
              messages: token ? earlierHistory : history,
              continuationToken: !token && earlierHistory.length ? 'older-page' : null,
            }),
            searchConversations: (query: string) => {
              searched.push(query);
              return of({
                items: history.length ? [{ conversationId: 'id', preview: 'Earlier', updatedAt: '2026-01-01' }] : [],
                continuationToken: null,
              });
            },
            send: (id: string) => {
              submittedIds.push(id);
              return chunks.asObservable();
            },
          },
        },
      ],
    }).compileComponents();
  });

  afterEach(() => {
    vi.restoreAllMocks();
    window.history.replaceState({}, '', '/');
  });

  it('updates the same assistant message as chunks arrive and shows a partial failure', async () => {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    const input = element.querySelector('textarea')!;
    input.value = 'Hello';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    element.querySelector('form.composer')!.dispatchEvent(new Event('submit'));
    fixture.detectChanges();
    expect(element.querySelector('.typing')).toBeTruthy();

    chunks.next({ type: 'delta', text: 'First' });
    fixture.detectChanges();
    expect(element.querySelector('.typing')).toBeFalsy();
    expect(element.textContent).toContain('First');

    chunks.next({ type: 'delta', text: ' second' });
    fixture.detectChanges();
    expect(element.textContent).toContain('First second');

    chunks.error(new ChatStreamError('Save failed'));
    fixture.detectChanges();
    expect(element.querySelector('[role="alert"]')?.textContent).toContain('Save failed');
    expect(element.querySelector('[role="alert"]')?.textContent).toContain('may not have been saved');
    expect(input.disabled).toBe(false);
  });

  it('displays saved usage from history and completed streamed replies', async () => {
    history = [
      { id: 'old', role: 'assistant', content: 'Earlier', tokenUsage: { promptTokens: 7, completionTokens: 2, totalTokens: 9 } },
      { id: 'unknown', role: 'assistant', content: 'Legacy', tokenUsage: null },
    ];
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('.history__results button')!.click();
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    expect(element.textContent).toContain('7 input');
    expect(element.textContent).toContain('2 output');
    expect(element.textContent).toContain('9 total');
    expect(element.textContent).toContain('Token usage unavailable');

    const input = element.querySelector('textarea')!;
    input.value = 'Hello';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    element.querySelector('form.composer')!.dispatchEvent(new Event('submit'));
    chunks.next({ type: 'delta', text: 'New reply' });
    chunks.next({ type: 'done', tokenUsage: { promptTokens: 30, completionTokens: 4, totalTokens: 34 } });
    chunks.complete();
    fixture.detectChanges();
    const usage = element.querySelectorAll('.message__usage');
    expect(usage.length).toBe(3);
    expect(usage[2].textContent).toContain('30 input');
    expect(usage[2].textContent).toContain('4 output');
    expect(usage[2].textContent).toContain('34 total');
  });

  it('can start a new conversation after a blocked history', async () => {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    await fixture.whenStable();
    const element = fixture.nativeElement as HTMLElement;
    const input = element.querySelector('textarea')!;
    input.value = 'Hello';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    element.querySelector('form.composer')!.dispatchEvent(new Event('submit'));
    chunks.error(new ChatStreamError('A message was blocked.'));
    fixture.detectChanges();
    const previousId = submittedIds[0];
    element.querySelector<HTMLButtonElement>('.error button')!.click();
    fixture.detectChanges();
    expect(element.querySelector('[role="alert"]')).toBeNull();
    expect(element.textContent).not.toContain('Hello');
    chunks = new Subject<ChatStreamEvent>();
    input.value = 'New';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    element.querySelector('form.composer')!.dispatchEvent(new Event('submit'));
    expect(submittedIds[1]).toBeTruthy();
    expect(submittedIds[1]).not.toBe(previousId);
  });

  it('searches and opens a conversation with older history', async () => {
    history = [{ id: 'recent', role: 'user', content: 'Recent message' }];
    earlierHistory = [{ id: 'older', role: 'user', content: 'Earlier message' }];
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    const search = element.querySelector<HTMLInputElement>('#history-search')!;
    search.value = 'earlier';
    search.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    element.querySelector('.history__search')!.dispatchEvent(new Event('submit'));
    fixture.detectChanges();
    expect(searched).toContain('earlier');
    element.querySelector<HTMLButtonElement>('.history__results button')!.click();
    fixture.detectChanges();
    expect(element.textContent).toContain('Recent message');
    const earlier = Array.from(element.querySelectorAll('button'))
      .find((button) => button.textContent?.includes('Load earlier messages'))!;
    earlier.click();
    fixture.detectChanges();
    expect(element.textContent).toContain('Earlier message');
    expect(element.textContent!.indexOf('Earlier message')).toBeLessThan(element.textContent!.indexOf('Recent message'));
  });

  it('does not show an old account response after a new sign-in', async () => {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    const input = element.querySelector('textarea')!;
    input.value = 'Private question';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    element.querySelector('form.composer')!.dispatchEvent(new Event('submit'));
    signedIn.set(false);
    fixture.detectChanges();
    element.querySelector<HTMLButtonElement>('.signin button')!.click();
    await fixture.whenStable();
    fixture.detectChanges();
    chunks.next({ type: 'delta', text: 'Old account response' });
    fixture.detectChanges();
    expect(element.textContent).not.toContain('Private question');
    expect(element.textContent).not.toContain('Old account response');
  });
});
