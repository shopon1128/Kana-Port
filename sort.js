/* ============================================================
   sort.js — 作品一覧の並び替え
             Games/index.html・Apps/index.html・Others/index.html で使用
   ------------------------------------------------------------
   日付は各カードの .work-meta にある <time datetime="YYYY-MM"> が
   唯一の情報源。HTMLに書いた並び順がそのまま「推し順」になる。

   CSS の order ではなく DOM の順序を実際に入れ替える。
   見た目の順序と Tab 移動の順序がずれないようにするため。

   filter.js とは独立している。絞り込みはカードの hidden を切り替える
   だけで順序に触れず、並び替えはノードを動かすだけで hidden に触れない。
   ============================================================ */
(() => {
  'use strict';

  const SORT_DEFAULT = 'pick';

  const SORTS = [
    { key: 'pick', label: '推し順' },
    { key: 'new', label: '新しい順' },
    { key: 'old', label: '古い順' },
  ];

  /** 並び替えボタンを出す最小カード数。1件しかないページでは出しても意味がない */
  const MIN_CARDS = 2;

  const sortBar = document.querySelector('#sort');
  const worksList = document.querySelector('#works-list');
  if (sortBar === null || worksList === null) return;

  const cards = Array.from(worksList.querySelectorAll('.work-card'));
  if (cards.length < MIN_CARDS) return;

  // HTML に書かれた並び順＝推し順。ここで控えておかないと元に戻せなくなる
  const pickOrder = new Map(cards.map((card, index) => [card, index]));

  /**
   * カードの制作時期を比較用の文字列で返す。
   * 'YYYY-MM' は辞書順がそのまま時系列順になる。
   * 日付を持たないカードは '' を返し、最も古いものとして扱う。
   */
  const dateOf = (card) => {
    const time = card.querySelector('.work-meta time[datetime]');
    return time === null ? '' : time.getAttribute('datetime');
  };

  // 同じ月の作品は推し順で並べる。毎回同じ結果になるようにするため
  const byPick = (x, y) => pickOrder.get(x) - pickOrder.get(y);

  const COMPARATORS = {
    pick: byPick,
    new: (x, y) => dateOf(y).localeCompare(dateOf(x)) || byPick(x, y),
    old: (x, y) => dateOf(x).localeCompare(dateOf(y)) || byPick(x, y),
  };

  const buttons = [];

  const buildSortBar = () => {
    const label = document.createElement('span');
    label.className = 'sort-label';
    label.textContent = '並び替え';

    const box = document.createElement('div');
    box.className = 'sort-btns';

    SORTS.forEach((sort) => {
      const btn = document.createElement('button');
      btn.type = 'button';
      btn.className = 'sort-btn';
      btn.dataset.sort = sort.key;
      btn.textContent = sort.label;
      btn.setAttribute('aria-pressed', 'false');
      buttons.push(btn);
      box.appendChild(btn);
    });

    sortBar.append(label, box);
  };

  const applySort = (key) => {
    const compare = COMPARATORS[key] ?? COMPARATORS[SORT_DEFAULT];

    // append は既存ノードを「移動」させる（複製ではない）。
    // hidden などカードが持っている状態はそのまま残る
    worksList.append(...cards.slice().sort(compare));

    buttons.forEach((btn) => {
      btn.setAttribute('aria-pressed', String(btn.dataset.sort === key));
    });
  };

  buildSortBar();

  // 個別登録ではなくイベント委譲
  sortBar.addEventListener('click', (event) => {
    const btn = event.target.closest('button[data-sort]');
    if (btn === null) return;
    applySort(btn.dataset.sort);
  });

  applySort(SORT_DEFAULT);
})();
