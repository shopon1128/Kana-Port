/* ============================================================
   hero-video.js — ページ冒頭のループ映像
   ------------------------------------------------------------
   音なしのループ映像を自動再生し、一時停止ボタンで止められるようにする。
   5秒を超えて自動で動き続ける映像には停止手段が要る（WCAG 2.2.2）。

   自動再生を HTML の autoplay 属性ではなくここで行うのは、
   「動きを減らす」設定の閲覧者には再生を始めず、ポスターの静止画を見せるため。
   JS が動かない環境でも、ポスターが表示されるだけで内容は欠けない。
   ============================================================ */
(() => {
  'use strict';

  const video = document.querySelector('.hero-video');
  const toggle = document.querySelector('.hero-toggle');
  if (video === null || toggle === null) return;

  const LABEL_PAUSE = '一時停止';
  const LABEL_PLAY = '再生';

  /** ボタンの文言を、押したときに起きることに合わせる */
  const syncLabel = () => {
    toggle.textContent = video.paused ? LABEL_PLAY : LABEL_PAUSE;
  };

  const play = async () => {
    try {
      await video.play();
    } catch (error) {
      // ブラウザが自動再生を拒んだ場合（省データ設定など）。ボタンが「再生」のまま残るので、
      // 閲覧者は止まっていることが分かり、押せば再生できる
      console.warn('hero-video: 自動再生できませんでした', error);
    }
    syncLabel();
  };

  video.addEventListener('play', syncLabel);
  video.addEventListener('pause', syncLabel);

  toggle.addEventListener('click', () => {
    if (video.paused) {
      play();
    } else {
      video.pause();
    }
  });

  toggle.hidden = false;

  const reduceMotion = window.matchMedia('(prefers-reduced-motion: reduce)');
  if (reduceMotion.matches) {
    syncLabel();
  } else {
    play();
  }
})();
