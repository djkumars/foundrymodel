import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { from, Observable, switchMap } from 'rxjs';
import { AuthService } from './auth.service';

export interface ChatMessage {
  id: string;
  role: 'user' | 'assistant';
  content: string;
  createdAt?: string;
  tokenUsage?: TokenUsage | null;
}

export interface TokenUsage {
  promptTokens: number;
  completionTokens: number;
  totalTokens: number;
}

export type ChatStreamEvent =
  | { type: 'delta'; text: string }
  | { type: 'done'; tokenUsage: TokenUsage | null };

export class ChatStreamError extends Error {
}

export interface ChatHistory {
  conversationId: string;
  messages: ChatMessage[];
  continuationToken?: string | null;
}

export interface ConversationSearchItem {
  conversationId: string;
  preview: string;
  updatedAt: string;
}

export interface ConversationSearchResponse {
  items: ConversationSearchItem[];
  continuationToken: string | null;
}

@Injectable({ providedIn: 'root' })
export class ChatService {
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthService);

  loadConversation(conversationId: string, continuationToken?: string): Observable<ChatHistory> {
    return from(this.auth.getToken()).pipe(switchMap((token) =>
      this.http.get<ChatHistory>(`/api/chat/${encodeURIComponent(conversationId)}`, {
        headers: { Authorization: `Bearer ${token}` },
        params: continuationToken ? { continuationToken } : {},
      }),
    ));
  }

  searchConversations(query: string, continuationToken?: string): Observable<ConversationSearchResponse> {
    const params: Record<string, string> = { query };
    if (continuationToken) params['continuationToken'] = continuationToken;
    return from(this.auth.getToken()).pipe(switchMap((token) =>
      this.http.get<ConversationSearchResponse>('/api/chat/conversations', {
        headers: { Authorization: `Bearer ${token}` },
        params,
      }),
    ));
  }

  send(conversationId: string, message: string): Observable<ChatStreamEvent> {
    if (!this.auth.useStreaming()) {
      return from(this.auth.getToken()).pipe(
        switchMap((token) => this.http.post<unknown>('/api/chat', { conversationId, message }, {
          headers: { Authorization: `Bearer ${token}` },
        })),
        switchMap((response) => {
          if (typeof response !== 'object' || response === null ||
              !('conversationId' in response) || typeof response.conversationId !== 'string' ||
              !('reply' in response) || typeof response.reply !== 'string' || !response.reply.trim() ||
              !('tokenUsage' in response) ||
              (response.tokenUsage !== null && !isTokenUsage(response.tokenUsage))) {
            throw new ChatStreamError('The AI service returned an invalid response. Please try again.');
          }
          const events: ChatStreamEvent[] = [
            { type: 'delta', text: response.reply },
            { type: 'done', tokenUsage: response.tokenUsage },
          ];
          return from(events);
        }),
      );
    }

    return new Observable<ChatStreamEvent>((subscriber) => {
      const abort = new AbortController();

      const readStream = async (): Promise<void> => {
        const token = await this.auth.getToken();
        if (abort.signal.aborted) return;
        const response = await fetch('/api/chat/stream', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${token}` },
          body: JSON.stringify({ conversationId, message }),
          signal: abort.signal,
        });

        if (!response.ok) {
          let message = 'Sorry, your message could not be sent. Please try again.';
          if (response.headers.get('content-type')?.includes('application/json')) {
            const body: unknown = await response.json();
            if (typeof body === 'object' && body !== null && 'error' in body && typeof body.error === 'string') {
              message = body.error;
            }
          }
          throw new ChatStreamError(message);
        }

        if (!response.body || !response.headers.get('content-type')?.includes('text/event-stream')) {
          throw new ChatStreamError('The AI service returned an invalid response. Please try again.');
        }

        const reader = response.body.getReader();
        const decoder = new TextDecoder();
        let buffer = '';
        try {
          while (true) {
            const { value, done } = await reader.read();
            buffer += decoder.decode(value, { stream: !done });
            let separator: RegExpExecArray | null;
            while ((separator = /\r?\n\r?\n/.exec(buffer))) {
              const frame = buffer.slice(0, separator.index);
              buffer = buffer.slice(separator.index + separator[0].length);
              const lines = frame.split(/\r?\n/);
              const event = lines.find((line) => line.startsWith('event:'))?.slice(6).trim();
              const data = lines.filter((line) => line.startsWith('data:'))
                .map((line) => line.slice(5).replace(/^ /, '')).join('\n');
              if (!event || !data) {
                throw new ChatStreamError('The AI service returned an invalid response. Please try again.');
              }
              const payload: unknown = JSON.parse(data);
              if (event === 'delta' && typeof payload === 'object' && payload !== null &&
                  'text' in payload && typeof payload.text === 'string') {
                subscriber.next({ type: 'delta', text: payload.text });
              } else if (event === 'done' && typeof payload === 'object' && payload !== null) {
                const usage = 'tokenUsage' in payload ? payload.tokenUsage : null;
                if (usage != null && !isTokenUsage(usage)) {
                  throw new ChatStreamError('The AI service returned invalid token usage.');
                }
                subscriber.next({ type: 'done', tokenUsage: usage ?? null });
                subscriber.complete();
                return;
              } else if (event === 'error' && typeof payload === 'object' && payload !== null &&
                         'error' in payload && typeof payload.error === 'string') {
                throw new ChatStreamError(payload.error);
              } else {
                throw new ChatStreamError('The AI service returned an invalid response. Please try again.');
              }
            }

            if (done) {
              throw new ChatStreamError('The reply was interrupted before it could be saved. Please try again.');
            }
          }

        } finally {
          reader.releaseLock();
        }
      };

      readStream().catch((error: unknown) => {
        if (!subscriber.closed) subscriber.error(error);
      });
      return () => abort.abort();
    });
  }
}

function isTokenUsage(value: unknown): value is TokenUsage {
  if (typeof value !== 'object' || value === null ||
      !('promptTokens' in value) || !('completionTokens' in value) || !('totalTokens' in value)) {
    return false;
  }
  return [value.promptTokens, value.completionTokens, value.totalTokens]
    .every((count) => typeof count === 'number' && Number.isSafeInteger(count) && count >= 0);
}
