/* ============================================================
   filter.js — トップページ「All Works」のタグ絞り込み
   ------------------------------------------------------------
   タグの一覧はここに持たず、各 .work-card の data-tags 属性から
   毎回組み立てる。タグの二重管理を避けるため。
   ============================================================ */
(() => {
  'use strict';

  /** 「すべて」ボタンが持つ data-tag の値（空文字＝絞り込み無し） */
  const TAG_ALL = '';
  const LABEL_ALL = 'すべて';

  const filterBar = document.querySelector('#filter');
  const worksList = document.querySelector('#works-list');
  const statusText = document.querySelector('#filter-status');
  const noResult = document.querySelector('#no-result');

  // このスクリプトは index.html 専用。想定の要素が無ければ何もしない
  // （絞り込みが動かなくても、カードはHTMLに直書きなので全件見える）
  if (filterBar === null || worksList === null) return;

  const cards = Array.from(worksList.querySelectorAll('.work-card'));

  /**
   * カードの data-tags を配列にする。
   * 空タグ（"a,,b" や末尾カンマ）は落とす。
   */
  const tagsOf = (a_card) =>
    (a_card.dataset.tags ?? '')
      .split(',')
      .map((t) => t.trim())
      .filter((t) => t.length > 0);

  /**
   * 全カードを走査してタグを集計し、表示順に並べて返す。
   * 並び順は「件数の多い順 → 先に登場した順」。
   * 件数が多いタグほど絞り込みの入口として役に立つため。
   */
  const collectTags = () => {
    const counts = new Map();
    const firstSeen = new Map();

    cards.forEach((card, cardIndex) => {
      tagsOf(card).forEach((tag) => {
        counts.set(tag, (counts.get(tag) ?? 0) + 1);
        if (!firstSeen.has(tag)) firstSeen.set(tag, cardIndex);
      });
    });

    return Array.from(counts.keys()).sort((a, b) => {
      const byCount = counts.get(b) - counts.get(a);
      return byCount !== 0 ? byCount : firstSeen.get(a) - firstSeen.get(b);
    });
  };

  const createButton = (a_tag, a_label) => {
    const btn = document.createElement('button');
    btn.type = 'button';
    btn.className = 'filter-btn';
    btn.dataset.tag = a_tag;
    btn.textContent = a_label;
    btn.setAttribute('aria-pressed', 'false');
    return btn;
  };

  /** 絞り込みボタンを生成して差し込む */
  const buildFilterBar = () => {
    const fragment = document.createDocumentFragment();
    fragment.appendChild(createButton(TAG_ALL, LABEL_ALL));
    collectTags().forEach((tag) => fragment.appendChild(createButton(tag, tag)));
    filterBar.appendChild(fragment);
  };

  buildFilterBar();

  // ボタン生成後に取得する（生成前だと空になる）
  const filterButtons = Array.from(filterBar.querySelectorAll('.filter-btn'));
  const tagChips = Array.from(worksList.querySelectorAll('.tag[data-tag]'));

  /**
   * 指定タグで表示を絞り込む。TAG_ALL なら全件表示。
   */
  const applyFilter = (a_tag) => {
    let visibleCount = 0;

    cards.forEach((card) => {
      const matched = a_tag === TAG_ALL || tagsOf(card).includes(a_tag);
      card.hidden = !matched;
      if (matched) visibleCount += 1;
    });

    // 選択状態を、絞り込みボタンとカード内のタグチップの両方に反映する
    filterButtons.forEach((btn) => {
      btn.setAttribute('aria-pressed', String(btn.dataset.tag === a_tag));
    });
    tagChips.forEach((chip) => {
      chip.classList.toggle('tag--active', chip.dataset.tag === a_tag);
    });

    if (statusText !== null) {
      statusText.textContent =
        a_tag === TAG_ALL
          ? `全 ${visibleCount} 件を表示中`
          : `「${a_tag}」 ${visibleCount} 件を表示中`;
    }
    if (noResult !== null) {
      noResult.hidden = visibleCount > 0;
    }
  };

  /**
   * 絞り込みバーとカード内チップの両方から呼ぶクリック処理。
   * 同じタグをもう一度押したら解除して全件に戻す。
   */
  const handleTagClick = (a_event) => {
    const btn = a_event.target.closest('button[data-tag]');
    if (btn === null) return;

    const isActive = btn.getAttribute('aria-pressed') === 'true' ||
                     btn.classList.contains('tag--active');
    applyFilter(isActive ? TAG_ALL : btn.dataset.tag);
  };

  // 個別登録ではなくイベント委譲（カードやタグが増えても登録し直さないで済む）
  filterBar.addEventListener('click', handleTagClick);
  worksList.addEventListener('click', handleTagClick);

  applyFilter(TAG_ALL);
})();
