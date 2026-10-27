// Раз в секунду сообщает фоновому скрипту, что играет в этой вкладке.
(() => {
  // Позиция/длительность от page-hook.js (видит скрытые плееры и setPositionState).
  let hooked = null;
  let hookedAt = 0;
  window.addEventListener("message", (event) => {
    if (event.source !== window || !event.data || !("__countdownSaverTimeline" in event.data)) return;
    hooked = event.data.__countdownSaverTimeline;
    hookedAt = Date.now();
  });

  function pickArtwork(list) {
    let best = null;
    let bestSize = -1;
    for (const art of list || []) {
      if (!art || !art.src) continue;
      const size = Math.max(0, ...String(art.sizes || "")
        .split(/\s+/)
        .map((s) => parseInt(s, 10) || 0));
      if (size > bestSize) {
        best = art.src;
        bestSize = size;
      }
    }
    try {
      return best ? new URL(best, location.href).href : null;
    } catch {
      return null;
    }
  }

  function findMediaElement() {
    const elements = [...document.querySelectorAll("audio, video")];
    return (
      elements.find((e) => !e.paused && !e.ended) ||
      elements.find((e) => e.duration > 0) ||
      null
    );
  }

  function snapshot() {
    const session = navigator.mediaSession;
    const meta = session && session.metadata;
    const element = findMediaElement();
    const timeline = hooked && Date.now() - hookedAt < 3000 ? hooked : null;
    const elementPlaying =
      !!(element && !element.paused && !element.ended && !element.muted) || !!(timeline && timeline.playing);

    let playing;
    if (session && session.playbackState === "playing") playing = true;
    else if (session && session.playbackState === "paused") playing = false;
    else playing = elementPlaying;

    // Без метаданных показываем только реально звучащее видео/аудио (по заголовку вкладки).
    if (!meta && !elementPlaying) return null;

    const title = (meta && meta.title) || document.title;
    if (!title) return null;

    const finite = (x) => (typeof x === "number" && isFinite(x) && x >= 0 ? x : null);
    const useElement = element && finite(element.duration) && (!timeline || !element.paused);
    return {
      title,
      artist: (meta && meta.artist) || "",
      artwork: meta ? pickArtwork(meta.artwork) : null,
      playing,
      position: useElement ? finite(element.currentTime) : timeline ? finite(timeline.position) : null,
      duration: useElement ? finite(element.duration) : timeline ? finite(timeline.duration) : null,
      site: location.hostname,
    };
  }

  const timer = setInterval(() => {
    try {
      chrome.runtime.sendMessage({ type: "media", state: snapshot() }).catch(() => {});
    } catch {
      // Расширение перезагрузили — этот экземпляр скрипта больше не нужен.
      clearInterval(timer);
    }
  }, 1000);
})();
