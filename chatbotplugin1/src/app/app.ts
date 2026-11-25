import { HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, ElementRef, effect, HostListener, inject, signal, viewChild } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { ChatMessage, ChatService, ChatStreamError, ConversationSearchItem } from './chat.service';
import { AuthService } from './auth.service';
import { AdminComponent } from './admin.component';

@Component({
  imports: [FormsModule, AdminComponent],
  selector: 'app-root',
  styleUrl: './app.css',
  templateUrl: './app.html',
})
export class App {
  private readonly chatService = inject(ChatService);
  protected readonly auth = inject(AuthService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly messageList = viewChild<ElementRef<HTMLDivElement>>('messageList');
  private skipAutoScroll = false;
  private sessionGeneration = 0;

  protected readonly isChatPage = window.location.pathname.replace(/\/+$/, '') === '/chat';
  protected readonly isAdminPage = window.location.pathname.replace(/\/+$/, '') === '/admin';
  private conversationId = '';
  protected readonly isChatOpen = signal(false);
  protected readonly title = signal(this.readTitle());
  protected readonly messages = signal<ChatMessage[]>([
    { id: 'greeting', role: 'assistant', content: 'Hi there! What can I help you with today?' },
  ]);
  protected readonly isLoading = signal(false);
  protected readonly isStreaming = signal(false);
  protected readonly isHistoryLoading = signal(false);
  protected readonly error = signal('');
  protected readonly conversations = signal<ConversationSearchItem[]>([]);
  protected readonly searchLoading = signal(false);
  protected readonly authBusy = signal(false);
  protected readonly continuationToken = signal<string | null>(null);
  protected readonly historyContinuationToken = signal<string | null>(null);
  protected searchDraft = '';
  private activeQuery = '';
  protected draft = '';

  constructor() {
    if (this.isChatPage) void this.initializeChat();

    effect(() => {
      this.messages();
      if (this.skipAutoScroll) {
        this.skipAutoScroll = false;
        return;
      }
      const element = this.messageList()?.nativeElement;
      if (element) {
        requestAnimationFrame(() => element.scrollTo({ top: element.scrollHeight, behavior: 'smooth' }));
      }
    });
  }

  protected sendMessage(): void {
    const content = this.draft.trim();
    if (!content || this.isLoading() || this.isHistoryLoading()) return;

    this.messages.update((messages) => [
      ...messages,
      { id: window.crypto.randomUUID(), role: 'user', content },
    ]);
    this.draft = '';
    this.error.set('');
    this.isLoading.set(true);

    const replyId = window.crypto.randomUUID();
    const generation = this.sessionGeneration;
    this.chatService.send(this.conversationId, content).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (event) => {
        if (generation !== this.sessionGeneration) return;
        if (event.type === 'done') {
          this.messages.update((messages) => messages.map((message) =>
            message.id === replyId ? { ...message, tokenUsage: event.tokenUsage } : message,
          ));
          return;
        }
        if (!this.isStreaming()) {
          this.isStreaming.set(true);
          this.messages.update((messages) => [
            ...messages,
            { id: replyId, role: 'assistant', content: event.text },
          ]);
        } else {
          this.messages.update((messages) => messages.map((message) =>
            message.id === replyId ? { ...message, content: message.content + event.text } : message,
          ));
        }
      },
      error: (error: unknown) => {
        if (generation !== this.sessionGeneration) return;
        const partial = this.isStreaming();
        this.error.set(`${this.getErrorMessage(error)}${partial ? ' The partial reply may not have been saved.' : ''}`);
        this.isLoading.set(false);
        this.isStreaming.set(false);
      },
      complete: () => {
        if (generation !== this.sessionGeneration) return;
        this.isLoading.set(false);
        this.isStreaming.set(false);
        this.searchConversations();
      },
    });
  }

  protected onInputKeydown(event: KeyboardEvent): void {
    if (event.key === 'Enter' && !event.shiftKey) {
      event.preventDefault();
      this.sendMessage();
    }
  }

  protected closeChat(): void {
    if (window.parent !== window) window.parent.postMessage({ type: 'foundry-chat:close' }, '*');
    else this.isChatOpen.set(false);
  }

  protected toggleChat(): void {
    this.isChatOpen.update((open) => !open);
  }

  protected startNewConversation(): void {
    if (this.isLoading() || this.isHistoryLoading()) return;
    this.conversationId = window.crypto.randomUUID();
    this.historyContinuationToken.set(null);
    this.messages.set([{ id: 'greeting', role: 'assistant', content: 'Hi there! What can I help you with today?' }]);
    this.draft = '';
    this.error.set('');
  }

  protected async signIn(): Promise<void> {
    if (this.authBusy()) return;
    this.authBusy.set(true);
    this.sessionGeneration++;
    this.isLoading.set(false);
    this.isStreaming.set(false);
    this.isHistoryLoading.set(false);
    this.searchLoading.set(false);
    this.messages.set([]);
    this.conversations.set([]);
    this.continuationToken.set(null);
    this.historyContinuationToken.set(null);
    this.searchDraft = '';
    this.error.set('');
    try {
      await this.auth.signIn();
      this.startNewConversation();
      this.searchConversations();
    } catch (error) {
      console.error('Sign-in failed.', error);
      this.error.set('Sign-in failed. Please try again.');
    } finally {
      this.authBusy.set(false);
    }
  }

  protected async signOut(): Promise<void> {
    if (this.authBusy()) return;
    this.authBusy.set(true);
    try {
      await this.auth.signOut();
      this.sessionGeneration++;
      this.conversationId = '';
      this.messages.set([]);
      this.conversations.set([]);
      this.continuationToken.set(null);
      this.searchDraft = '';
      this.error.set('');
    } catch (error) {
      console.error('Sign-out failed.', error);
      this.error.set('Sign-out failed. Please try again.');
    } finally {
      this.authBusy.set(false);
    }
  }

  protected searchConversations(loadMore = false): void {
    if (!this.auth.signedIn() || this.searchLoading()) return;
    if (!loadMore) {
      this.activeQuery = this.searchDraft.trim();
      this.conversations.set([]);
      this.continuationToken.set(null);
    }
    this.searchLoading.set(true);
    const generation = this.sessionGeneration;
    this.chatService.searchConversations(this.activeQuery, loadMore ? this.continuationToken() ?? undefined : undefined)
      .pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
        next: (result) => {
          if (generation !== this.sessionGeneration) return;
          this.conversations.update((items) => loadMore ? [...items, ...result.items] : result.items);
          this.continuationToken.set(result.continuationToken);
          this.searchLoading.set(false);
        },
        error: (error: unknown) => {
          if (generation !== this.sessionGeneration) return;
          this.error.set(this.getErrorMessage(error));
          this.searchLoading.set(false);
        },
      });
  }

  protected loadConversation(conversationId: string, loadEarlier = false): void {
    if (this.isLoading() || this.isHistoryLoading()) return;
    this.isHistoryLoading.set(true);
    const generation = this.sessionGeneration;
    this.chatService.loadConversation(conversationId, loadEarlier ? this.historyContinuationToken() ?? undefined : undefined)
      .pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (conversation) => {
        if (generation !== this.sessionGeneration) return;
        this.conversationId = conversation.conversationId;
        this.historyContinuationToken.set(conversation.continuationToken ?? null);
        const element = this.messageList()?.nativeElement;
        const previousHeight = element?.scrollHeight ?? 0;
        const previousTop = element?.scrollTop ?? 0;
        if (loadEarlier) this.skipAutoScroll = true;
        this.messages.set(loadEarlier
          ? [...conversation.messages, ...this.messages()]
          : conversation.messages.length
            ? conversation.messages
            : [{ id: 'greeting', role: 'assistant', content: 'Hi there! What can I help you with today?' }],
        );
        if (loadEarlier && element) {
          requestAnimationFrame(() => { element.scrollTop = previousTop + element.scrollHeight - previousHeight; });
        }
        this.error.set('');
        this.isHistoryLoading.set(false);
      },
      error: (error: unknown) => {
        if (generation !== this.sessionGeneration) return;
        this.error.set(this.getErrorMessage(error));
        this.isHistoryLoading.set(false);
      },
    });
  }

  protected loadEarlierMessages(): void {
    if (this.historyContinuationToken()) this.loadConversation(this.conversationId, true);
  }

  private async initializeChat(): Promise<void> {
    await this.auth.initialize();
    if (this.auth.signedIn()) {
      this.startNewConversation();
      this.searchConversations();
    }
  }

  @HostListener('window:message', ['$event'])
  protected onChatMessage(event: MessageEvent): void {
    const frame = document.querySelector<HTMLIFrameElement>('.chat-popover iframe');
    if (event.source === frame?.contentWindow && event.data?.type === 'foundry-chat:close') {
      this.isChatOpen.set(false);
    }
  }

  private readTitle(): string {
    const requestedTitle = new URLSearchParams(window.location.search).get('title')?.trim();
    return requestedTitle?.slice(0, 60) || 'AI Assistant';
  }

  private getErrorMessage(error: unknown): string {
    if (error instanceof ChatStreamError) return error.message;
    if (error instanceof HttpErrorResponse && typeof error.error?.error === 'string') {
      return error.error.error;
    }
    return 'Sorry, your message could not be sent. Please try again.';
  }
}
