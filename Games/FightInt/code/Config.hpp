#pragma once


// Config.hpp
//   ゲーム全体で共有する定数。ロジック側にマジックナンバーを残さないために、
//   ルール・画面レイアウト・リソースパスの 3 つの名前空間に分けて集約する。

#include <SFML/Graphics/Color.hpp>
#include <SFML/System/Vector2.hpp>

#include <array>
#include <cstdint>


// ゲームルールに関する定数
//   盤面サイズや MP コストなど、見た目を変えても影響しない値。

namespace Rule {

inline constexpr int GRID_SIZE = 10;  // 盤面は 10x10 マス
inline constexpr int EMPTY_CELL = -1; // 駒が乗っていないマスを表す番兵値

inline constexpr int TEAM_1 = 1;
inline constexpr int TEAM_2 = 2;
inline constexpr int TEAM_COUNT = 2;

inline constexpr int MP_PER_TURN = 4;     // ターン開始時に配られる MP
inline constexpr int DEF_ATTACK_COST = 1; // ジョブ固有スキルを持たない場合の攻撃 MP コスト
inline constexpr int DEF_DAMAGE = 1;
inline constexpr int DEF_MOVE_COST = 1;
inline constexpr int HEAL_COST = 1;
inline constexpr int HEAL_AMOUNT = 1;
inline constexpr int FREE_MOVE_LIMIT = 2; // 魔術師・ヴァンが 1 ターンに使える無料移動の回数

inline constexpr int MAX_LOG_LINES = 7; // ログ欄に残す行数（プレイヤーごと）

// --- 演出の時間（秒）---
// いずれも演出中はプレイヤーの操作とゲーム進行を止める
inline constexpr float TURN_BANNER_SECONDS = 1.2f; // ターン開始表示
inline constexpr float HIT_STOP_SECONDS = 0.42f;   // 被弾時のヒットストップ
inline constexpr float HIT_BLINK_INTERVAL = 0.06f; // 被弾マスの点滅の切り替え間隔

// 決着を決めた一撃だけは、通常より遅く点滅させてから駒を消し、
// 少し間を置いてリザルトへ移る
inline constexpr float FINISH_BLINK_SECONDS = 1.2f;   // 最後の一体が点滅している時間
inline constexpr float FINISH_BLINK_INTERVAL = 0.16f; // スローモーション（通常より遅い点滅）
inline constexpr float FINISH_PAUSE_SECONDS = 0.8f;   // 消滅してからリザルトへ移るまでの間

// --- ジョブ別パラメータ ---
inline constexpr int SWORD_HP = 3;
inline constexpr int KNIGHT_HP = 4;
inline constexpr int ARCHER_HP = 2;
inline constexpr int MAGICIAN_HP = 2;
inline constexpr int DOCTOR_HP = 3;
inline constexpr int VAN_HP = 3;
inline constexpr int SWORD_POWER_COST = 2; // 剣士は MP に余裕があれば強攻撃に切り替える
inline constexpr int SWORD_POWER_DAMAGE = 2;

// --- 初期配置 ---
inline constexpr int JOBS_PER_TEAM = 6;
inline constexpr int SPAWN_AREA_WIDTH = 2;                          // 各陣営の初期配置は盤面端の 2 列
inline constexpr int TEAM_2_SPAWN_X = GRID_SIZE - SPAWN_AREA_WIDTH; // 2P 側の初期配置の左端

// カーソルの初期位置。各プレイヤーが自陣側から操作を始められるように、
// 手番ごとに別々のカーソルを持たせる（最後にいたマスから再開するため）
inline constexpr sf::Vector2i TEAM_1_START_CURSOR{0, 0};
inline constexpr sf::Vector2i TEAM_2_START_CURSOR{GRID_SIZE - 1, 0};

// 上・左・右・下。弓兵の索敵と行動範囲の計算で同じ順序を使う。
// 順序を変えると、敵が複数隣接した時に弓兵が撃つ相手が変わる点に注意
inline constexpr std::array<sf::Vector2i, 4> ORTHOGONAL_DIRS = {sf::Vector2i{0, -1}, sf::Vector2i{-1, 0},
                                                                sf::Vector2i{1, 0}, sf::Vector2i{0, 1}};

// 相手チームの番号を返す
inline constexpr int OpponentOf(int a_team)
{
    return (a_team == TEAM_1) ? TEAM_2 : TEAM_1;
}

} // namespace Rule


// 画面レイアウトに関する定数
//   値を変えても勝敗やルールには影響しない、見た目だけの設定。
//
//   ウィンドウの高さは、macOS のメニューバーとタイトルバーを引いても
//   ノート PC の画面に収まるよう 780 に抑えている（900 だと下端が見切れる）。
//
//   横 1200 を「左ログ欄 / 盤面 / 右ログ欄」の 3 列に割る:
//      12..304 : 1P のログ欄
//     320..880 : 盤面（56px * 10 マス = 560）
//     896..1188: 2P のログ欄

namespace Layout {

inline constexpr unsigned int WINDOW_WIDTH = 1200;
inline constexpr unsigned int WINDOW_HEIGHT = 780;

// 1マスの一辺。駒の絵は 64px だが、この値に合わせて拡縮して描く
inline constexpr int TILE_SIZE = 56;
inline constexpr float BOARD_OFFSET_X = 320.0f; // 盤面描画の左余白
inline constexpr float BOARD_OFFSET_Y = 112.0f; // 盤面描画の上余白

// --- 配色 ---
// 真っ黒 + 原色はコントラストが強すぎるので、暗い青灰でまとめて
// 彩度を落とし、強調したい要素（操作ガイド・行動対象）にだけ色を使う
inline constexpr sf::Color BACKGROUND_COLOR{22, 24, 30};
inline constexpr sf::Color BOARD_CELL_COLOR{34, 37, 46};   // 盤面のマスの地
inline constexpr sf::Color BOARD_GRID_COLOR{68, 74, 88};   // 白い罫線はきついので灰青
inline constexpr sf::Color PANEL_COLOR{30, 33, 41};        // ログ欄の地
inline constexpr sf::Color PANEL_BORDER_COLOR{56, 62, 76};
inline constexpr sf::Color TEXT_COLOR{204, 210, 220};      // 本文
inline constexpr sf::Color MUTED_TEXT_COLOR{136, 144, 158}; // 補助的な案内
inline constexpr sf::Color ACCENT_COLOR{240, 196, 104};     // 押せるキーの案内（琥珀）

// チーム色。暗い地の上で読めるよう、純色ではなく明るめにする。
// 2Pは駒の絵（シアン）に寄せた青
inline constexpr sf::Color TEAM_1_COLOR{238, 96, 96};
inline constexpr sf::Color TEAM_2_COLOR{96, 176, 240};

inline constexpr sf::Color TeamColor(int a_team)
{
    return (a_team == Rule::TEAM_1) ? TEAM_1_COLOR : TEAM_2_COLOR;
}

// --- 左右の見出し（1P / 2P）---
// 手番でない側は暗くして、どちらの番かを一目で分かるようにする
inline constexpr unsigned int TEAM_LABEL_FONT_SIZE = 44;
inline constexpr float TEAM_LABEL_Y = 4.0f;
inline constexpr float TEAM_1_LEFT_X = 26.0f;    // 1P は左寄せ
inline constexpr float TEAM_2_RIGHT_X = 1174.0f; // 2P は右寄せ（この X が右端になる）
inline constexpr sf::Color INACTIVE_TEAM_COLOR{72, 78, 92};

// --- 残り MP ---
// 数字だと目に入らないので、盤面の真上に手番側の色の丸を並べて残量を示す。
// 数えなくても残りが分かり、視線が盤面から大きく外れない位置に置く
inline constexpr int MP_PIP_COUNT = Rule::MP_PER_TURN;
inline constexpr float MP_PIP_RADIUS = 11.0f;
inline constexpr float MP_PIP_SPACING = 34.0f; // 丸の左端どうしの間隔
inline constexpr float MP_ROW_Y = 60.0f;       // 丸の上端
inline constexpr float MP_PIP_OUTLINE_THICKNESS = 2.0f;
inline constexpr unsigned int MP_LABEL_FONT_SIZE = 22;
inline constexpr float MP_LABEL_GAP = 16.0f;  // 「MP」の文字と丸の間
inline constexpr float MP_LABEL_Y = 54.0f;
inline constexpr sf::Color MP_PIP_EMPTY_COLOR{40, 44, 54}; // 使い切った分

// --- 画面の上下中央に出す操作ガイド ---
inline constexpr unsigned int HINT_FONT_SIZE = 28;
inline constexpr float TOP_HINT_Y = 10.0f;
inline constexpr float BOTTOM_HINT_Y = 692.0f;

// --- ログ欄 ---
inline constexpr sf::Vector2f LOG_PANEL_SIZE{292.0f, 560.0f};
inline constexpr sf::Vector2f LOG_PANEL_1_POS{12.0f, 112.0f};
inline constexpr sf::Vector2f LOG_PANEL_2_POS{896.0f, 112.0f};
inline constexpr float LOG_PANEL_BORDER_THICKNESS = 1.0f;

inline constexpr unsigned int LOG_HEADER_FONT_SIZE = 17;
inline constexpr unsigned int LOG_FONT_SIZE = 17;
inline constexpr sf::Vector2f LOG_HEADER_OFFSET{14.0f, 8.0f};

// パネル上端から最初のエントリまで。MAX_LOG_LINES 件が
// LOG_PANEL_SIZE.y に収まるよう、44 + 70 * 7 = 534 <= 560 を保つこと
inline constexpr float LOG_ENTRY_FIRST_Y = 44.0f;
inline constexpr float LOG_ENTRY_HEIGHT = 70.0f; // 1 エントリ（2 行）の高さ
inline constexpr float LOG_ICON_SIZE = 28.0f;    // ログ内のアイコンの一辺

// 1エントリ内の配置（エントリ左上からの相対座標）
inline constexpr sf::Vector2f LOG_ACTOR_ICON_OFFSET{14.0f, 0.0f};
inline constexpr sf::Vector2f LOG_ACTOR_TEXT_OFFSET{50.0f, 4.0f};
inline constexpr sf::Vector2f LOG_ARROW_OFFSET{16.0f, 34.0f};
inline constexpr sf::Vector2f LOG_TARGET_ICON_OFFSET{38.0f, 32.0f};
inline constexpr sf::Vector2f LOG_TARGET_TEXT_OFFSET{74.0f, 36.0f};
inline constexpr sf::Vector2f LOG_NOTE_OFFSET{14.0f, 18.0f}; // 「もう一度」など単独メッセージ

// --- タイトル / リザルト ---
inline constexpr unsigned int TITLE_FONT_SIZE = 68;
inline constexpr unsigned int RESULT_FONT_SIZE = 70;
inline constexpr unsigned int NORMAL_FONT_SIZE = 28;
inline constexpr float TITLE_Y = 140.0f;
inline constexpr float TITLE_HINT_Y = 578.0f;
inline constexpr float RESULT_Y = 200.0f;
inline constexpr float RESULT_HINT_Y = 620.0f;

// タイトル: 1P と 2P からランダムに 2 体ずつ。
// 一列に並べず左右へ散らし、外側を高く内側を低くして画面の四隅寄りを埋める。
inline constexpr int TITLE_PIECES_PER_TEAM = 2;
inline constexpr float TITLE_PIECE_SIZE = 150.0f;

// 描画順は「1P の 2 体 → 2P の 2 体」なので、左 2 つ・右 2 つの順に書く。

inline constexpr std::array<sf::Vector2f, TITLE_PIECES_PER_TEAM * Rule::TEAM_COUNT> TITLE_PIECE_POSITIONS = {
    sf::Vector2f{60.0f, 288.0f},   // 1P 外側（上）
    sf::Vector2f{190.0f, 545.0f},  // 1P 内側（下）
    sf::Vector2f{990.0f, 288.0f},  // 2P 外側（上）
    sf::Vector2f{860.0f, 545.0f}}; // 2P 内側（下）

// リザルト: 勝った側の全ジョブを並べる。倒れた駒を暗く出すので、
inline constexpr float RESULT_PIECE_SIZE = 96.0f;
inline constexpr float RESULT_PIECE_SPACING = 130.0f;
inline constexpr float RESULT_PIECE_Y = 360.0f;
inline constexpr unsigned int RESULT_SURVIVOR_FONT_SIZE = 22;
inline constexpr float RESULT_SURVIVOR_Y = 486.0f;

// 倒れた駒は暗くして、生き残りと見分けられるようにする
inline constexpr sf::Color FALLEN_SPRITE_TINT{62, 66, 78};

// --- 盤面の演出 ---
inline constexpr float GRID_OUTLINE_THICKNESS = 1.0f;
// カーソルと行動対象の枠は内側に描く。外側に出すと隣のマスに重なるため
inline constexpr float CURSOR_OUTLINE_THICKNESS = -3.0f;
inline constexpr float ACTION_MARKER_THICKNESS = -3.0f;

// 掴んでいる駒が残り MP で到達できるマスの地の色。
// 無料移動（魔術師の縦・ヴァンの横）で行けるマスは、同じ緑系のまま明るくして
inline constexpr sf::Color REACHABLE_CELL_COLOR{32, 62, 56};
inline constexpr sf::Color FREE_MOVE_CELL_COLOR{62, 142, 112};

// 騎士の肩代わり範囲
inline constexpr sf::Color GUARD_WASH_COLOR{198, 212, 235, 16};

// 今いるマスから攻撃できる相手 / 回復できる味方を囲む枠の色。
// 攻撃枠は 1P の駒（赤）と紛れないよう、赤ではなくオレンジにしている
inline constexpr sf::Color ATTACK_MARKER_COLOR{255, 142, 48};
inline constexpr sf::Color HEAL_MARKER_COLOR{124, 222, 152};

// 駒を掴んでいる間のカーソル色
inline constexpr sf::Color CURSOR_HOLD_COLOR{255, 212, 92};

// 行動済みで掴めなくなった駒の色。スプライトに乗算されるので暗くなる
inline constexpr sf::Color ACTED_SPRITE_TINT{92, 96, 108};

// 被弾したマスを光らせる色
inline constexpr sf::Color HIT_FLASH_COLOR{255, 242, 206};

// --- ターン開始演出 ---
// 画面全体は暗くせず、中央の不透明な帯だけを出す。帯の上下を手番の色で
// 縁取ることで、盤面を暗くしなくても意図した表示に見えるようにする
inline constexpr unsigned int TURN_BANNER_FONT_SIZE = 56;
inline constexpr sf::Vector2f TURN_BANNER_BOX_POS{0.0f, 330.0f};
inline constexpr sf::Vector2f TURN_BANNER_BOX_SIZE{static_cast<float>(WINDOW_WIDTH), 120.0f};
inline constexpr float TURN_BANNER_TEXT_Y = 348.0f;
inline constexpr sf::Color TURN_BANNER_BOX_COLOR{26, 29, 38};
inline constexpr float TURN_BANNER_EDGE_THICKNESS = 2.0f;

} // namespace Layout


// リソースファイルのパス
//   実行時のカレントディレクトリからの相対パス。
//   .app から起動した場合は Platform::ChangeToResourceDirectory() が
//   カレントを Resources へ移したうえで解決される。

namespace Resource {

inline constexpr int TEXTURE_COUNT = Rule::JOBS_PER_TEAM * 2; // 1P/2P で別絵柄を持つ

// 日本語を含む文字列を描くため、和文グリフを持つフォントを使う。
// 描画側は sf::String::fromUtf8() を通すこと（Renderer::MakeText 参照）
inline constexpr const char* FONT_PATH = "Fonts/NotoSansJP-Light.ttf";
inline constexpr const char* HELP_IMAGE_PATH = "Image/help.png";
inline constexpr const char* SE_CLICK_PATH = "Sound/click.ogg";
inline constexpr const char* SE_MOVE_PATH = "Sound/move.ogg";
inline constexpr const char* SE_ATTACK_PATH = "Sound/attack.ogg";
inline constexpr const char* SE_WIN_PATH = "Sound/win.ogg";

// 駒テクスチャは「1P の 6 ジョブ → 2P の 6 ジョブ」の順に並べる。
// この並び順が Game::CreateTeam のオフセット計算の前提になっている。
inline constexpr std::array<const char*, TEXTURE_COUNT> TEXTURE_FILES = {
    "Image/c11.png", "Image/c12.png", "Image/c13.png", "Image/c14.png", "Image/c15.png", "Image/c16.png",
    "Image/c21.png", "Image/c22.png", "Image/c23.png", "Image/c24.png", "Image/c25.png", "Image/c26.png"};

} // namespace Resource
