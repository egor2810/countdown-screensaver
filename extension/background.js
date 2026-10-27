// Собирает состояние со всех вкладок и отправляет самое подходящее заставке.
// Пока заставка не запущена, запросы просто не доходят — это нормально.
const ENDPOINT = "http://127.0.0.1:38211/media";
const MAX_AGE_MS = 5000;
const frames = new Map(); // "tabId:frameId" -> { ...state, t }
let lastSent = 0;

const ua = navigator.userAgent;
const BROWSER = /YaBrowser/.test(ua) ? "Яндекс Браузер"
  : /Edg\//.test(ua) ? "Microsoft Edge"
  : /OPR\//.test(ua) ? "Opera"
  : "Chrome";

chrome.runtime.onMessage.addListener((message, sender) => {
  if (!message || message.type !== "media" || !sender.tab) return;
  const key = `${sender.tab.id}:${sender.frameId || 0}`;
  if (message.state) frames.set(key, { ...message.state, t: Date.now() });
  else frames.delete(key);
  send();
});

chrome.tabs.onRemoved.addListener((tabId) => {
  for (const key of [...frames.keys()]) {
    if (key.startsWith(`${tabId}:`)) frames.delete(key);
  }
});

function send() {
  const now = Date.now();
  if (now - lastSent < 900) return;
  lastSent = now;

  for (const [key, value] of frames) {
    if (now - value.t > MAX_AGE_MS) frames.delete(key);
  }
  const all = [...frames.values()].sort((a, b) => b.t - a.t);
  const best = all.find((x) => x.playing) || all[0] || null;
  const media = best ? { ...best } : null;
  if (media) delete media.t;

  fetch(ENDPOINT, {
    method: "POST",
    headers: { "Content-Type": "text/plain" },
    body: JSON.stringify({ browser: BROWSER, media }),
  }).catch(() => {});
}
