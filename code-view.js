/* ============================================================
   code-view.js — 引き出し式のコード表示
   ------------------------------------------------------------
   <details> を開いたときに、対応する code/*.py を読み込んで表示する。
   HTML にコードを貼り付けないのは、code/ の実ファイルと二重管理になり
   片方だけ直してズレる事故を避けるため。

   開閉そのものは <details> のネイティブ機能なので、JS が動かなくても
   引き出しは開く（中身は「GitHub で見る」リンクから読める）。
   ============================================================ */
(() => {
  'use strict';

  const drawers = Array.from(document.querySelectorAll('.code-drawer'));
  if (drawers.length === 0) return;

  /**
   * コードを1回だけ読み込む。
   * fetch の text() は Content-Type に関係なく常に UTF-8 で復号するため、
   * GitHub Pages が .py を octet-stream で返しても文字化けしない。
   */
  const loadCode = async (a_drawer) => {
    const target = a_drawer.querySelector('code[data-src]');
    // 読み込み済み・要素なしは何もしない
    if (target === null || target.dataset.loaded === 'true') return;

    // 多重実行の防止。開閉を素早く繰り返しても fetch は1回だけ
    target.dataset.loaded = 'true';
    target.textContent = '読み込み中…';

    try {
      const res = await fetch(target.dataset.src);
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      // textContent で入れる（innerHTML は使わない。コード中の < > がタグ扱いになるため）
      target.textContent = await res.text();
    } catch (err) {
      target.dataset.loaded = 'false';   // 次に開いたとき再試行できるよう戻す
      target.textContent =
        `コードを読み込めませんでした（${err.message}）。下の「GitHub で見る」からご覧ください。`;
    }
  };

  // toggle イベントはバブリングしないため、委譲ではなく各要素に登録する
  drawers.forEach((drawer) => {
    drawer.addEventListener('toggle', () => {
      if (drawer.open) loadCode(drawer);
    });
  });
})();
