/* ============================================================
   sound-player.js — Sound 章の試聴リスト
   ------------------------------------------------------------
   各行の再生ボタン（音源ファイルへのリンク）を押すと、ページ内で再生する。
   同時に鳴らすのは1つだけ。鳴っている行をもう一度押すと止まる。

   ボタンを <a href> にしているのは、JS が動かない環境でも
   リンクとして音源が開き、そのまま聴けるようにするため。
   ============================================================ */
(() => {
  'use strict';

  const items = Array.from(document.querySelectorAll('.sound-item'));
  if (items.length === 0) return;

  const SEC_PER_MIN = 60;

  // 再生は1つの Audio を使い回す（行ごとに持つと、止め忘れで重なって鳴るため）
  const audio = new Audio();
  audio.preload = 'none';

  let current = null;    // 今鳴っている行
  let rafId = 0;         // 進捗表示の更新ループ

  /** 秒を m:ss に整形する */
  const formatTime = (sec) => {
    const total = Math.floor(sec);
    const min = Math.floor(total / SEC_PER_MIN);
    const rest = String(total % SEC_PER_MIN).padStart(2, '0');
    return `${min}:${rest}`;
  };

  const getButton = (item) => item.querySelector('.sound-play');
  const getTime = (item) => item.querySelector('.sound-time');

  /**
   * 進捗線と経過時間を描く。
   * timeupdate は数百ms間隔でしか来ず、0.1秒ほどの効果音では一度も来ないことがあるので、
   * 再生中は毎フレーム描き直す
   */
  const drawProgress = () => {
    if (current === null) return;
    const ratio = audio.duration > 0 ? audio.currentTime / audio.duration : 0;
    current.style.setProperty('--progress', String(ratio));
    const time = getTime(current);
    if (time !== null) time.textContent = formatTime(audio.currentTime);
    rafId = requestAnimationFrame(drawProgress);
  };

  /** 行の見た目と読み上げ用の状態を切り替える */
  const setPlaying = (item, playing) => {
    const button = getButton(item);
    item.classList.toggle('is-playing', playing);
    button.setAttribute('aria-pressed', String(playing));
    button.setAttribute('aria-label', playing ? button.dataset.labelStop : button.dataset.labelPlay);
    if (!playing) {
      item.style.removeProperty('--progress');
      const time = getTime(item);
      if (time !== null) time.textContent = time.dataset.duration;
    }
  };

  const stop = () => {
    cancelAnimationFrame(rafId);
    audio.pause();
    if (current !== null) setPlaying(current, false);
    current = null;
  };

  const play = (item) => {
    stop();
    current = item;
    audio.src = getButton(item).href;
    setPlaying(item, true);
    audio.play()
      .then(() => { rafId = requestAnimationFrame(drawProgress); })
      // 読み込み失敗・自動再生の制限などで鳴らなかったら、押す前の状態に戻す
      .catch(() => { if (current === item) stop(); });
  };

  audio.addEventListener('ended', stop);
  audio.addEventListener('error', () => { if (current !== null) stop(); });

  items.forEach((item) => {
    const button = getButton(item);
    // JS が動くときだけボタンとして振る舞う（リンクのままだと読み上げで「リンク」と案内される）
    button.setAttribute('role', 'button');
    button.setAttribute('aria-pressed', 'false');
    button.dataset.labelPlay = button.getAttribute('aria-label');
    button.dataset.labelStop = button.dataset.labelPlay.replace(/を再生$/, 'を停止');

    const time = getTime(item);
    if (time !== null) time.dataset.duration = time.textContent;

    button.addEventListener('click', (event) => {
      event.preventDefault();   // ページ遷移せず、その場で鳴らす
      if (current === item) stop();
      else play(item);
    });

    // role="button" に合わせて Space でも押せるようにする（Enter はリンクの既定で click になる）
    button.addEventListener('keydown', (event) => {
      if (event.key !== ' ') return;
      event.preventDefault();
      button.click();
    });
  });
})();
