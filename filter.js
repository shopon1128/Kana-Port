/* ============================================================
   filter.js — 作品一覧ページのタグ絞り込み
               Games/index.html・Apps/index.html・Others/index.html で使用
   ------------------------------------------------------------
   タグの一覧はここに持たず、各 .work-card の data-tags 属性から
   毎回組み立てる。タグの二重管理を避けるため。

   絞り込みは「軸をまたいで AND、同じ軸の中では 1 つだけ」。
   例）形式=3D かつ 体制=個人制作。2D を選ぶと 3D は外れる。
   ============================================================ */
(() => {
  'use strict';

  /**
   * 絞り込みの軸と、その中のタグの表示順を定義する。
   *
   * ここにあるのは「軸の名前」と「並び順」だけで、どの作品がどのタグを
   * 持つかは各カードの data-tags が唯一の情報源。二重管理にはならない。
   *
   * 件数順に並べると、作品が増えるたびに 2D と 3D が離れるなど並びが
   * 動いてしまうため、軸で固定する。ここに無いタグは MISC_GROUP へ
   * 自動的に回るので、タグを増やすだけならこのファイルを触らなくてよい。
   *
   * 来訪者が最初に絞りたいのはジャンルなので、それを先頭に置く。
   */
  const TAG_GROUPS = [
    { name: 'ジャンル',   tags: ['アクション', 'シューティング', '探索', 'パズル', '戦略',
                                'リズム', 'クリッカー', '知育', '対戦', '協力'] },
    { name: '形式',       tags: ['2D', '3D'] },
    { name: '技術',       tags: ['Unity', 'C++', 'Python', 'ゲームAI', '機械学習',
                                'SQLite', 'Claude', 'Blender'] },
    { name: '制作体制',   tags: ['個人制作', 'チーム開発'] },
    { name: '入手・環境', tags: ['ブラウザ', 'ダウンロード', 'macOS'] },
    { name: 'その他',     tags: ['個別ページあり'] },
  ];

  /** 軸の定義から漏れたタグを受け止める軸の名前（TAG_GROUPS に実在させること） */
  const MISC_GROUP = 'その他';

  const filterBar = document.querySelector('#filter');
  const worksList = document.querySelector('#works-list');
  const statusText = document.querySelector('#filter-status');
  const noResult = document.querySelector('#no-result');

  // このスクリプトは一覧ページ専用。想定の要素が無ければ何もしない
  // （絞り込みが動かなくても、カードはHTMLに直書きなので全件見える）
  if (filterBar === null || worksList === null) return;

  const cards = Array.from(worksList.querySelectorAll('.work-card'));

  /**
   * カードの data-tags を配列にする。
   * 空タグ（"a,,b" や末尾カンマ）は落とす。
   */
  const tagsOf = (card) =>
    (card.dataset.tags ?? '')
      .split(',')
      .map((t) => t.trim())
      .filter((t) => t.length > 0);

  // タグ → 所属する軸の名前。カード内のチップを押したときに、
  // どの軸を差し替えればよいか引くのに使う
  const groupNameOf = new Map();
  TAG_GROUPS.forEach((group) => {
    group.tags.forEach((tag) => groupNameOf.set(tag, group.name));
  });

  /**
   * 全カードを走査してタグを集計し、TAG_GROUPS の順に軸を組み立てる。
   * このページに実在しないタグと、空になった軸は落とす
   * （例: Apps ページには形式タグが無いので、その軸は現れない）。
   */
  const buildGroups = () => {
    const counts = new Map();
    cards.forEach((card) => {
      tagsOf(card).forEach((tag) => counts.set(tag, (counts.get(tag) ?? 0) + 1));
    });

    const groups = TAG_GROUPS.map((group) => ({
      name: group.name,
      tags: group.tags.filter((tag) => counts.has(tag)),
    }));

    // どの軸にも属さないタグは MISC_GROUP の末尾へ。
    // 定義漏れがあっても絞り込みから消えないための受け皿
    const known = new Set(TAG_GROUPS.flatMap((group) => group.tags));
    const ungrouped = Array.from(counts.keys())
      .filter((tag) => !known.has(tag))
      .sort((a, b) => a.localeCompare(b, 'ja'));

    if (ungrouped.length > 0) {
      const misc = groups.find((group) => group.name === MISC_GROUP);
      misc.tags = misc.tags.concat(ungrouped);
      ungrouped.forEach((tag) => groupNameOf.set(tag, MISC_GROUP));
    }

    return groups.filter((group) => group.tags.length > 0);
  };

  const groups = buildGroups();
  if (groups.length === 0) return;

  /** 軸名 → 選択中のタグ。空なら絞り込み無し */
  const selected = new Map();

  /** 選択の組み合わせ（軸名→タグ）を、そのカードがすべて満たすか */
  const matches = (card, selection) => {
    const tags = tagsOf(card);
    for (const tag of selection.values()) {
      if (!tags.includes(tag)) return false;
    }
    return true;
  };

  const countMatching = (selection) =>
    cards.reduce((n, card) => n + (matches(card, selection) ? 1 : 0), 0);

  // 生成したボタンの台帳。描画のたびに件数と押下状態を書き戻す
  const buttons = [];

  // 絞り込みUIを包んでいる折りたたみ（HTML側の <details class="works-tools">）。
  // クラス名ではなく要素で辿るので、CSSの都合でクラス名が変わっても壊れない
  const toolsBox = filterBar.closest('details');

  const createButton = (groupName, tag) => {
    const btn = document.createElement('button');
    btn.type = 'button';
    btn.className = 'filter-btn';
    btn.dataset.tag = tag;
    btn.setAttribute('aria-pressed', 'false');

    const label = document.createElement('span');
    label.textContent = tag;

    // 件数。押す前に「効くタグかどうか」が分かるようにする
    const count = document.createElement('span');
    count.className = 'filter-count';

    btn.append(label, count);
    return { el: btn, groupName, tag, countEl: count };
  };

  /** 「軸名 + ボタン列」の 1 行を組み立てる */
  const createRow = (group) => {
    const row = document.createElement('div');
    row.className = 'filter-row';

    const label = document.createElement('span');
    label.className = 'filter-row-label';
    label.textContent = group.name;

    const box = document.createElement('div');
    box.className = 'filter-row-btns';
    group.tags.forEach((tag) => {
      const entry = createButton(group.name, tag);
      buttons.push(entry);
      box.appendChild(entry.el);
    });

    row.append(label, box);
    return row;
  };

  const clearBtn = document.createElement('button');
  clearBtn.type = 'button';
  clearBtn.className = 'filter-clear';
  clearBtn.textContent = 'すべて解除';
  clearBtn.hidden = true;

  const buildFilterBar = () => {
    const fragment = document.createDocumentFragment();
    groups.forEach((group) => fragment.appendChild(createRow(group)));
    fragment.appendChild(clearBtn);
    filterBar.appendChild(fragment);
  };

  buildFilterBar();

  // ボタン生成後に取得する（生成前だと空になる）
  const tagChips = Array.from(worksList.querySelectorAll('.tag[data-tag]'));

  /** 現在の選択にもとづいて、カードの表示・件数・状態表示を描き直す */
  const render = () => {
    let visibleCount = 0;
    cards.forEach((card) => {
      const matched = matches(card, selected);
      card.hidden = !matched;
      if (matched) visibleCount += 1;
    });

    // 件数は「その軸をこのタグに差し替えたら何件になるか」。
    // 0 件にしかならない組み合わせは押せなくして、空振りを防ぐ
    buttons.forEach(({ el, groupName, tag, countEl }) => {
      const isOn = selected.get(groupName) === tag;
      const trial = new Map(selected);
      trial.set(groupName, tag);
      const hits = countMatching(trial);

      el.setAttribute('aria-pressed', String(isOn));
      el.disabled = !isOn && hits === 0;
      countEl.textContent = String(hits);
    });

    const chosen = Array.from(selected.values());
    tagChips.forEach((chip) => {
      chip.classList.toggle('tag--active', chosen.includes(chip.dataset.tag));
    });
    clearBtn.hidden = chosen.length === 0;

    if (statusText !== null) {
      statusText.textContent =
        chosen.length === 0
          ? `全 ${visibleCount} 件を表示中`
          : `${chosen.map((t) => `「${t}」`).join('')} ${visibleCount} 件を表示中`;
    }
    if (noResult !== null) {
      noResult.hidden = visibleCount > 0;
    }
  };

  /**
   * 畳んだ状態でカード内のチップを押したときに、絞り込みUIを開く。
   * 選択したタグが見えないまま件数だけ減る、という状態を避ける
   */
  const revealFilterBar = () => {
    if (toolsBox !== null) toolsBox.open = true;
  };

  /** 同じタグをもう一度押したら、その軸の絞り込みだけ解除する */
  const toggleTag = (tag) => {
    const groupName = groupNameOf.get(tag);
    if (groupName === undefined) return;

    if (selected.get(groupName) === tag) {
      selected.delete(groupName);
    } else {
      selected.set(groupName, tag);
      revealFilterBar();
    }
    render();
  };

  const handleTagClick = (event) => {
    const btn = event.target.closest('button[data-tag]');
    if (btn === null || btn.disabled) return;
    toggleTag(btn.dataset.tag);
  };

  // 個別登録ではなくイベント委譲（カードやタグが増えても登録し直さないで済む）
  filterBar.addEventListener('click', handleTagClick);
  worksList.addEventListener('click', handleTagClick);
  clearBtn.addEventListener('click', () => {
    selected.clear();
    render();
  });

  render();
})();
