// Заставка knitERP при открытии сайта: анимация логотипа и счётчик процентов под ней.
// Показывается раз в день на устройстве, не на печатных формах и не при системной настройке «уменьшить движение».
// Без кнопки «Пропустить» (по просьбе владельца продукта); закрыть раньше — щелчком по заставке или Escape. Если видео не запустилось за 1,5 с — сразу открывается сайт.
(() => {
  const box = document.getElementById('kn-intro');
  if (!box) return;
  const day = new Date().toISOString().slice(0, 10);
  const key = 'kniterp-intro-day';
  let seen = false;
  try { seen = localStorage.getItem(key) === day; } catch { /* приватный режим — показываем */ }
  const motion = window.matchMedia('(prefers-reduced-motion: reduce)');
  if (seen || motion.matches || location.pathname.startsWith('/print/')) { box.remove(); return; }

  const video = box.querySelector('video');
  const num = box.querySelector('.kn-intro-num');
  const bar = box.querySelector('.kn-intro-bar span');
  let closed = false;
  let frame = 0;
  let shown = 0;

  const close = () => {
    if (closed) return;
    closed = true;
    cancelAnimationFrame(frame);
    clearTimeout(startTimer);
    document.removeEventListener('keydown', onKey);
    try { localStorage.setItem(key, day); } catch { /* ничего */ }
    box.classList.add('kn-intro-out');
    setTimeout(() => box.remove(), 350);
  };
  const onKey = (e) => { if (e.key === 'Escape' || e.key === 'Enter' || e.key === ' ') { e.preventDefault(); close(); } };

  // Счётчик плавно догоняет ход видео: 0 → 100 %.
  const tick = () => {
    const target = video.duration ? Math.min(100, Math.round((video.currentTime / video.duration) * 100)) : 0;
    shown += Math.max(0, target - shown) * 0.35;
    const value = Math.min(100, Math.round(shown));
    num.textContent = value;
    bar.style.width = value + '%';
    box.querySelector('.kn-intro-count').setAttribute('aria-valuenow', String(value));
    if (!closed) frame = requestAnimationFrame(tick);
  };

  const startTimer = setTimeout(close, 1500);
  video.addEventListener('playing', () => {
    clearTimeout(startTimer);
    box.classList.add('kn-intro-on');
    frame = requestAnimationFrame(tick);
  }, { once: true });
  video.addEventListener('ended', () => {
    num.textContent = '100';
    bar.style.width = '100%';
    setTimeout(close, 250);
  }, { once: true });
  video.addEventListener('error', close, { once: true });
  box.addEventListener('click', close);
  document.addEventListener('keydown', onKey);
  motion.addEventListener?.('change', (e) => { if (e.matches) close(); });

  box.hidden = false;
  video.muted = true;
  video.play().catch(close);
})();
