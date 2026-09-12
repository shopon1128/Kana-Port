/* ============================================================
   filter.js — 作品一覧ページのタグ絞り込み
               Games/index.html・Apps/index.html で使用
   ------------------------------------------------------------
   タグの一覧はここに持たず、各 .work-card の data-tags 属性から
   毎回組み立てる。タグの二重管理を避けるため。
   ============================================================ */
(() => {
  'use strict';

  /** 「すべて」ボタンが持つ data-tag の値（空文字＝絞り込み無し） */
  const TAG_ALL = '';
  const LABEL_ALL = 'すべて';

  /**
   * 絞り込みボタンの表示順を、意味の軸ごとにまとめて定義する。
   *
   * ここにあるのは「並び順」だけで、どの作品がどのタグを持つかは
   * 各カードの data-tags が唯一の情報源。二重管理にはならない。
   *
   * 件数順に並べると、作品が増えるたびに 2D と 3D が離れるなど
   * 並びが動いてしまうため、軸で固定する。
   * ここに無いタグは末尾のグループへ自動的に回るので、
   * タグを増やすだけならこのファイルを触らなくてよい。
   */
  const TAG_GROUPS = [
    // ジャンル。来訪者が最初に絞りたい軸なので先頭に置く
    ['アクション', 'シューティング', '探索', 'パズル', 'リズム', 'クリッカー', '対戦', '協力'],
    ['2D', '3D'],                                          // 形式
    ['Unity', 'Python', 'ゲームAI', '機械学習', 'SQLite'],  // 技術
    ['macOS'],                                             // 動作環境
    ['ブラウザ', 'ダウンロード'],                            // 入手・遊び方
  ];

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
   * 全カードを走査してタグを集計し、TAG_GROUPS の順にまとめて返す。
   * 戻り値は「グループの配列」で、空のグループは落とす
   * （例: Apps ページには形式タグが無いので、そのグループは現れない）。
   */
  const collectTagGroups = () => {
    const counts = new Map();
    const firstSeen = new Map();

    cards.forEach((card, cardIndex) => {
      tagsOf(card).forEach((tag) => {
        counts.set(tag, (counts.get(tag) ?? 0) + 1);
        if (!firstSeen.has(tag)) firstSeen.set(tag, cardIndex);
      });
    });

    // 定義済みグループ。このページに実在するタグだけを残す
    const groups = TAG_GROUPS.map((group) => group.filter((tag) => counts.has(tag)));

    // どのグループにも属さないタグは末尾へ。
    // 定義漏れがあっても絞り込みから消えないための受け皿
    const known = new Set(TAG_GROUPS.flat());
    const ungrouped = Array.from(counts.keys())
      .filter((tag) => !known.has(tag))
      .sort((a, b) => {
        const byCount = counts.get(b) - counts.get(a);
        return byCount !== 0 ? byCount : firstSeen.get(a) - firstSeen.get(b);
      });
    groups.push(ungrouped);

    return groups.filter((group) => group.length > 0);
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

  /** グループ間の区切り線。装飾なので読み上げ対象から外す */
  const createSeparator = () => {
    const sep = document.createElement('span');
    sep.className = 'filter-sep';
    sep.setAttribute('aria-hidden', 'true');
    return sep;
  };

  /** 絞り込みボタンを生成して差し込む */
  const buildFilterBar = () => {
    const fragment = document.createDocumentFragment();
    fragment.appendChild(createButton(TAG_ALL, LABEL_ALL));
    collectTagGroups().forEach((group) => {
      fragment.appendChild(createSeparator());
      group.forEach((tag) => fragment.appendChild(createButton(tag, tag)));
    });
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
