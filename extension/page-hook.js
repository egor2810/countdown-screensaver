// Выполняется в контексте самой страницы. Многие плееры (например, Яндекс Музыка) играют через
// аудио-элемент, которого нет в DOM, а позицию сообщают только через mediaSession.setPositionState —
// обычному скрипту расширения это не видно. Здесь перехватываем и то и другое и раз в полсекунды
// отправляем позицию/длительность в content.js через postMessage.
(() => {
  const elements = new Set();
  const originalPlay = HTMLMediaElement.prototype.play;
  HTMLMediaElement.prototype.play = function (...args) {
    elements.add(this);
    return originalPlay.apply(this, args);
  };

  let positionState = null;
  const session = navigator.mediaSession;
  if (session && typeof session.setPositionState === "function") {
    const originalSet = session.setPositionState.bind(session);
    session.setPositionState = function (state) {
      positionState = state && state.duration ? { ...state, at: performance.now() } : null;
      return originalSet(state);
    };
  }

  function timeline() {
    const list = [...elements];
    const element =
      list.find((e) => !e.paused && !e.ended && e.duration > 0) ||
      list.find((e) => e.duration > 0 && isFinite(e.duration));
    if (element && isFinite(element.duration) && element.duration > 0) {
      return { position: element.currentTime, duration: element.duration, playing: !element.paused && !element.ended };
    }
    if (positionState) {
      const playing = session.playbackState === "playing";
      const rate = positionState.playbackRate || 1;
      const elapsed = playing ? ((performance.now() - positionState.at) / 1000) * rate : 0;
      return {
        position: Math.min(positionState.duration, (positionState.position || 0) + elapsed),
        duration: positionState.duration,
        playing,
      };
    }
    return null;
  }

  setInterval(() => {
    try {
      window.postMessage({ __countdownSaverTimeline: timeline() }, "*");
    } catch {
      // ignore
    }
  }, 500);
})();
