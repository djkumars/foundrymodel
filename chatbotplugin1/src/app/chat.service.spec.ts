import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ChatService, ChatStreamError, ChatStreamEvent } from './chat.service';
import { vi } from 'vitest';
import { AuthService } from './auth.service';

describe('ChatService streaming', () => {
  let service: ChatService;
  const encoder = new TextEncoder();

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [
      provideHttpClient(),
      provideHttpClientTesting(),
      { provide: AuthService, useValue: { getToken: () => Promise.resolve('test-token'), useStreaming: () => true } },
    ] });
    service = TestBed.inject(ChatService);
  });

  describe('ChatService conversation search', () => {
    it('sends the signed-in bearer token and search parameters', async () => {
      const http = TestBed.inject(HttpTestingController);
      const result = new Promise<void>((resolve, reject) => service.searchConversations('topic', 'next').subscribe({
        next: (page) => {
          expect(page.items[0].preview).toBe('topic match');
          resolve();
        },
        error: reject,
      }));
      await Promise.resolve();
      const request = http.expectOne((req) => req.url === '/api/chat/conversations');
      expect(request.request.headers.get('Authorization')).toBe('Bearer test-token');
      expect(request.request.params.get('query')).toBe('topic');
      expect(request.request.params.get('continuationToken')).toBe('next');
      request.flush({ items: [{ conversationId: 'id', preview: 'topic match', updatedAt: '2026-01-01' }], continuationToken: null });
      await result;
      http.verify();
    });
  });

  afterEach(() => vi.unstubAllGlobals());

  function mockStream(chunks: Uint8Array[]): void {
    const body = new ReadableStream<Uint8Array>({
      start(controller) {
        chunks.forEach((chunk) => controller.enqueue(chunk));
        controller.close();
      },
    });
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(body, {
      headers: { 'Content-Type': 'text/event-stream' },
    })));
  }

  it('emits split UTF-8 chunks as deltas before completing', async () => {
    const bytes = encoder.encode('event: delta\ndata: {"text":"Hi 🌍"}\n\n' +
      'event: delta\ndata: {"text":"!"}\n\n' +
      'event: done\ndata: {"conversationId":"id","tokenUsage":{"promptTokens":12,"completionTokens":3,"totalTokens":15}}\n\n');
    const emoji = bytes.indexOf(0xf0);
    mockStream([bytes.slice(0, emoji + 2), bytes.slice(emoji + 2, emoji + 4), bytes.slice(emoji + 4)]);

    const received: ChatStreamEvent[] = [];
    await new Promise<void>((resolve, reject) => service.send('id', 'hello').subscribe({
      next: (chunk) => received.push(chunk),
      error: reject,
      complete: resolve,
    }));
    expect(fetch).toHaveBeenCalledWith('/api/chat/stream', expect.objectContaining({
      headers: { 'Content-Type': 'application/json', Authorization: 'Bearer test-token' },
    }));
    expect(received).toEqual([
      { type: 'delta', text: 'Hi 🌍' },
      { type: 'delta', text: '!' },
      { type: 'done', tokenUsage: { promptTokens: 12, completionTokens: 3, totalTokens: 15 } },
    ]);
  });

  it('reports an error after partial output', async () => {
    mockStream([encoder.encode('event: delta\ndata: {"text":"partial"}\n\n' +
      'event: error\ndata: {"error":"Save failed"}\n\n')]);
    const received: ChatStreamEvent[] = [];
    const error = await new Promise<unknown>((resolve) => service.send('id', 'hello').subscribe({
      next: (chunk) => received.push(chunk),
      error: resolve,
    }));
    expect(received).toEqual([{ type: 'delta', text: 'partial' }]);
    expect(error).toBeInstanceOf(ChatStreamError);
    expect((error as Error).message).toBe('Save failed');
  });

  it('rejects a stream that ends without completion', async () => {
    mockStream([encoder.encode('event: delta\ndata: {"text":"partial"}\n\n')]);
    const error = await new Promise<unknown>((resolve) => service.send('id', 'hello').subscribe({
      error: resolve,
    }));
    expect(error).toBeInstanceOf(ChatStreamError);
    expect((error as Error).message).toContain('interrupted');
  });

  it('handles unavailable usage without inventing token counts', async () => {
    mockStream([encoder.encode('event: done\ndata: {"conversationId":"id","tokenUsage":null}\n\n')]);
    const received: ChatStreamEvent[] = [];
    await new Promise<void>((resolve, reject) => service.send('id', 'hello').subscribe({
      next: (event) => received.push(event),
      error: reject,
      complete: resolve,
    }));
    expect(received).toEqual([{ type: 'done', tokenUsage: null }]);
  });

  it('rejects invalid token counts', async () => {
    mockStream([encoder.encode('event: done\ndata: {"tokenUsage":{"promptTokens":-1,"completionTokens":3,"totalTokens":2}}\n\n')]);
    const error = await new Promise<unknown>((resolve) => service.send('id', 'hello').subscribe({
      error: resolve,
    }));
    expect(error).toBeInstanceOf(ChatStreamError);
  });

  it('does not send a request after cancellation while waiting for a token', async () => {
    let resolveToken!: (token: string) => void;
    vi.spyOn(TestBed.inject(AuthService), 'getToken').mockReturnValue(
      new Promise<string>((resolve) => { resolveToken = resolve; }),
    );
    const fetchMock = vi.fn();
    vi.stubGlobal('fetch', fetchMock);
    const subscription = service.send('id', 'hello').subscribe();
    subscription.unsubscribe();
    resolveToken('test-token');
    await Promise.resolve();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('uses the authenticated JSON endpoint when streaming is disabled', async () => {
    vi.spyOn(TestBed.inject(AuthService), 'useStreaming').mockReturnValue(false);
    const http = TestBed.inject(HttpTestingController);
    const received: ChatStreamEvent[] = [];
    const finished = new Promise<void>((resolve, reject) => service.send('id', 'hello').subscribe({
      next: (event) => received.push(event), error: reject, complete: resolve,
    }));
    await Promise.resolve();
    const request = http.expectOne('/api/chat');
    expect(request.request.method).toBe('POST');
    expect(request.request.headers.get('Authorization')).toBe('Bearer test-token');
    request.flush({ conversationId: 'id', reply: 'Complete answer', tokenUsage: { promptTokens: 2, completionTokens: 3, totalTokens: 5 } });
    await finished;
    expect(received).toEqual([
      { type: 'delta', text: 'Complete answer' },
      { type: 'done', tokenUsage: { promptTokens: 2, completionTokens: 3, totalTokens: 5 } },
    ]);
    http.verify();
  });

  it('rejects invalid usage in a non-streaming response', async () => {
    vi.spyOn(TestBed.inject(AuthService), 'useStreaming').mockReturnValue(false);
    const http = TestBed.inject(HttpTestingController);
    const failed = new Promise<unknown>((resolve) => service.send('id', 'hello').subscribe({ error: resolve }));
    await Promise.resolve();
    http.expectOne('/api/chat').flush({ conversationId: 'id', reply: 'answer', tokenUsage: { promptTokens: -1, completionTokens: 1, totalTokens: 0 } });
    expect(await failed).toBeInstanceOf(ChatStreamError);
    http.verify();
  });
});
