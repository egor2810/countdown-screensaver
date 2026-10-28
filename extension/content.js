// Сообщает фоновому скрипту, что играет в этой вкладке: раз в секунду и сразу при изменениях.
(() => {
  // Позиция/длительность от page-hook.js (видит скрытые плееры и setPositionState).
  let hooked = null;
  let hookedAt = 0;
  window.addEventListener("message", (event) => {
    // Сайты шлют через postMessage и свои сообщения, в том числе строки — берём только наши.
    const data = event.data;
    if (event.source !== window || !data || typeof data !== "object" || !("__countdownSaverTimeline" in data)) return;
    hooked = data.__countdownSaverTimeline;
    hookedAt = Date.now();
    if (data.changed) push();
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
    else if (timeline && typeof timeline.playing === "boolean") playing = timeline.playing;
    else playing = elementPlaying;

    // Без метаданных показываем только реально звучащее видео/аудио (по заголовку вкладки).
    if (!meta && !elementPlaying) return null;

    const title = (meta && meta.title) || document.title;
    if (!title) return null;

    const finite = (x) => (typeof x === "number" && isFinite(x) && x >= 0 ? x : null);
    // Позиция от page-hook точнее (учитывает setPositionState и скрытые плееры); элемент в DOM — запасной вариант.
    const useElement = !timeline && element && finite(element.duration);
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

  function push() {
    try {
      chrome.runtime.sendMessage({ type: "media", state: snapshot() }).catch(() => {});
    } catch {
      // Расширение перезагрузили — этот экземпляр скрипта больше не нужен.
      clearInterval(timer);
    }
  }

  const timer = setInterval(push, 1000);
})();
