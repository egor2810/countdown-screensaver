// Выполняется в контексте самой страницы. Отсюда видно то, что недоступно обычному скрипту расширения:
//  • аудио/видео-элементы, которых нет в DOM (так играют Яндекс Музыка, Spotify и др.);
//  • mediaSession.setPositionState — позиция в текущем треке, которую сайт сообщает браузеру
//    для системного плеера. Она точнее элемента: Spotify, например, играет треки подряд через
//    один поток, и время элемента не совпадает с местом в песне.
// Позицию отправляем в content.js раз в полсекунды и сразу — при смене трека, паузе или перемотке.
(() => {
  const session = navigator.mediaSession;
  const elements = new Set();
  let positionState = null;
  let metadataChangedAt = 0;
  let notifyScheduled = false;

  function post(changed) {
    try {
      window.postMessage({ __countdownSaverTimeline: timeline(), changed }, "*");
    } catch {
      // ignore
    }
  }

  // Несколько событий подряд (смена трека = metadata + positionState + play) склеиваем в одно.
  function notify() {
    if (notifyScheduled) return;
    notifyScheduled = true;
    setTimeout(() => {
      notifyScheduled = false;
      post(true);
    }, 80);
  }

  const MEDIA_EVENTS = ["play", "pause", "seeked", "ended", "durationchange", "loadedmetadata"];

  function track(element) {
    if (elements.has(element)) return;
    elements.add(element);
    for (const name of MEDIA_EVENTS) element.addEventListener(name, notify);
  }

  const originalPlay = HTMLMediaElement.prototype.play;
  HTMLMediaElement.prototype.play = function (...args) {
    track(this);
    return originalPlay.apply(this, args);
  };
  // Элементы в DOM: медиасобытия не всплывают, но ловятся на фазе перехвата.
  for (const name of MEDIA_EVENTS) {
    document.addEventListener(name, (event) => {
      if (event.target instanceof HTMLMediaElement) {
        track(event.target);
        notify();
      }
    }, true);
  }

  if (session) {
    if (typeof session.setPositionState === "function") {
      const originalSet = session.setPositionState.bind(session);
      session.setPositionState = function (state) {
        positionState = state && state.duration ? { ...state, at: performance.now() } : null;
        notify();
        return originalSet(state);
      };
    }
    // Узнаём о смене трека и паузе сразу, а не при следующем опросе.
    const proto = Object.getPrototypeOf(session);
    for (const prop of ["metadata", "playbackState"]) {
      const descriptor = Object.getOwnPropertyDescriptor(proto, prop);
      if (!descriptor || !descriptor.set) continue;
      Object.defineProperty(proto, prop, {
        ...descriptor,
        set(value) {
          descriptor.set.call(this, value);
          if (prop === "metadata") metadataChangedAt = performance.now();
          notify();
        },
      });
    }
  }

  function fromPositionState() {
    // Позиция от прошлого трека не годится: сайт мог сменить метаданные, но ещё не позицию.
    if (!positionState || positionState.at < metadataChangedAt - 1500) return null;
    const playing = session.playbackState === "playing";
    const rate = positionState.playbackRate || 1;
    const elapsed = playing ? ((performance.now() - positionState.at) / 1000) * rate : 0;
    return {
      position: Math.min(positionState.duration, (positionState.position || 0) + elapsed),
      duration: positionState.duration,
      playing: session.playbackState === "none" ? null : playing,
    };
  }

  function fromElement() {
    const list = [...elements];
    const element =
      list.find((e) => !e.paused && !e.ended && e.duration > 0) ||
      list.find((e) => e.duration > 0 && isFinite(e.duration));
    if (!element || !isFinite(element.duration) || element.duration <= 0) return null;
    return { position: element.currentTime, duration: element.duration, playing: !element.paused && !element.ended };
  }

  function timeline() {
    return fromPositionState() || fromElement();
  }

  setInterval(() => post(false), 500);
})();
