(function () {
  const loader = document.currentScript;
  if (!loader) return;

  const source = loader.dataset.chatUrl || new URL('chat', new URL('./', loader.src)).href;
  let chatUrl;
  try {
    chatUrl = new URL(source, loader.src);
  } catch {
    console.error('Foundry chatbot: data-chat-url must be a valid URL.');
    return;
  }
  if (chatUrl.protocol !== 'https:' && chatUrl.hostname !== 'localhost' && chatUrl.hostname !== '127.0.0.1') {
    console.error('Foundry chatbot: the chat URL must use HTTPS.');
    return;
  }

  chatUrl.searchParams.set('title', loader.dataset.title || 'AI Assistant');
  chatUrl.searchParams.set('embed', '1');
  chatUrl.searchParams.set('site', window.location.origin);

  const launcher = document.createElement('button');
  launcher.type = 'button';
  launcher.className = 'foundry-chat-launcher';
  launcher.textContent = loader.dataset.buttonText || 'Chat with us';
  launcher.setAttribute('aria-label', 'Open chat assistant');

  const frame = document.createElement('iframe');
  frame.className = 'foundry-chat-frame';
  frame.title = loader.dataset.title || 'AI Assistant';
  frame.src = chatUrl.href;
  frame.allow = 'clipboard-write';
  frame.hidden = true;

  const style = document.createElement('style');
  style.textContent = `
    .foundry-chat-launcher {
      position: fixed; right: 24px; bottom: 24px; z-index: 2147483000;
      padding: 14px 20px; border: 0; border-radius: 999px;
      color: #fff; background: #245c9e; box-shadow: 0 5px 22px #142b4940;
      font: 600 14px/1.2 system-ui, sans-serif; cursor: pointer;
    }
    .foundry-chat-launcher:hover { background: #1b4b84; }
    .foundry-chat-frame {
      position: fixed; right: 24px; bottom: 88px; z-index: 2147483000;
      width: 380px; height: min(620px, calc(100dvh - 112px));
      border: 0; border-radius: 20px; background: transparent;
      box-shadow: 0 18px 60px #10244326;
    }
    .foundry-chat-frame[hidden] { display: none; }
    @media (max-width: 480px) {
      .foundry-chat-launcher { right: 16px; bottom: 16px; }
      .foundry-chat-frame {
        inset: 0; width: 100%; height: 100dvh; max-height: 100dvh;
        border-radius: 0;
      }
    }
  `;

  launcher.addEventListener('click', function () {
    frame.hidden = !frame.hidden;
    launcher.setAttribute('aria-expanded', String(!frame.hidden));
    launcher.textContent = frame.hidden ? loader.dataset.buttonText || 'Chat with us' : 'Close chat';
  });

  window.addEventListener('message', function (event) {
    if (event.source === frame.contentWindow && event.data?.type === 'foundry-chat:close') {
      frame.hidden = true;
      launcher.setAttribute('aria-expanded', 'false');
      launcher.textContent = loader.dataset.buttonText || 'Chat with us';
    }
  });

  document.head.appendChild(style);
  document.body.append(launcher, frame);
})();
