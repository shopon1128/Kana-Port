#pragma once

#include "Config.hpp"
#include "MoveRules.hpp" // CellFlags

#include <SFML/Graphics/Color.hpp>
#include <SFML/Graphics/Font.hpp>
#include <SFML/Graphics/RectangleShape.hpp>
#include <SFML/Graphics/RenderWindow.hpp>
#include <SFML/Graphics/Text.hpp>
#include <SFML/System/Vector2.hpp>
#include <SFML/Window/Event.hpp>

#include <array>
#include <optional>
#include <string>
#include <vector>

namespace sf {
class RenderTarget;
class Texture;
} // namespace sf
class Board;

// 描画に渡すゲーム状態
//   Renderer は Game を知らず、これらの値だけを読んで絵を作る。
//   描画側からゲーム状態を書き換えられないようにするための受け渡し用の型。

// ログ 1 件。行動した駒と対象の駒を絵と名前の両方で示す。
// アイコンだけだと魔術師と医者が紛らわしいため、名前を併記する。
struct LogMessage {
    int actorTeam{};

    // note が空でない場合は「もう一度」のような 1 行メッセージとして表示し、
    // 以下の駒の情報は使わない
    std::string note{};

    const sf::Texture* actorIcon{nullptr}; // 参照先は Game のテクスチャ配列（プログラム終了まで有効）
    std::string actorName{};
    const sf::Texture* targetIcon{nullptr};
    std::string targetName{};
    std::string amountText{}; // ダメージなら "2"、回復なら "+1"
};

// プレイヤーごとのログ（添字は team - TEAM_1）
using LogBook = std::array<std::vector<LogMessage>, Rule::TEAM_COUNT>;

// タイトル・リザルトに並べる駒の絵。左から順に描かれる
struct ShowcasePiece {
    const sf::Texture* texture{nullptr}; // 参照先は Game のテクスチャ配列
    bool fallen{false};                  // 倒れた駒は暗くする
};
using Showcase = std::vector<ShowcasePiece>;

struct HudState {
    int turn{}; // 現在の手番
    int mp{};   // 手番側の残り MP
    int cursorX{};
    int cursorY{};
    bool hasSelection{};      // 駒を掴んでいるか
    bool canCatch{};          // カーソル位置の駒を掴めるか
    bool showTurnBanner{};  // ターン開始演出を表示中か（この間は操作不可）
    CellFlags reachable{};  // 掴んでいる駒が到達できるマス
    CellFlags freeMove{};   // そのうち MP を消費せずに行けるマス
    CellFlags attackable{}; // 今いるマスから攻撃できる敵のマス
    CellFlags healable{};   // 今いるマスから回復できる味方のマス（医者）

    // 騎士の肩代わり範囲（両チーム分をまとめたもの）。駒を掴んでいなくても常に表示する
    CellFlags guarded{};

    // 被弾演出。hitCell が盤内を指す間だけ、そのマスを点滅させる
    sf::Vector2i hitCell{-1, -1};
    bool hitFlashOn{}; // 点滅の「光っている」フェーズか
};

// 描画
//   ウィンドウとフォントを所有し、画面に出すものはすべてここを通す。
//   実際の描画処理は sf::RenderTarget に対して書くので、ウィンドウにも
//   オフスクリーン（RenderTexture）にも同じ絵を出せる。

class Renderer {
public:
    Renderer();

    // --- ウィンドウ ---
    bool IsOpen() const { return _window.isOpen(); }
    void Close() { _window.close(); }
    std::optional<sf::Event> PollEvent() { return _window.pollEvent(); }

    // --- 画面ごとの描画（1 フレーム分を完結させる）---
    void DrawTitle(const Showcase& a_pieces);
    void DrawGame(const Board& a_board, const HudState& a_hud, const LogBook& a_logs);
    void DrawResult(int a_winner, const Showcase& a_pieces);

    // ヘルプ画像を別ウィンドウで表示する（閉じるまで戻らない）
    void ShowHelp();

    // 各画面をオフスクリーンに描いて画像として保存する。
    // ウィンドウの表示状態に依存しないので、レイアウト確認や不具合報告に使える。
    bool SaveGameImage(const Board& a_board, const HudState& a_hud, const LogBook& a_logs,
                       const std::string& a_path);
    bool SaveTitleImage(const Showcase& a_pieces, const std::string& a_path);
    bool SaveResultImage(int a_winner, const Showcase& a_pieces, const std::string& a_path);

private:
    // a_text は UTF-8。sf::String は std::string をロケール依存の ANSI として
    // 解釈してしまうため、ここで必ず fromUtf8() を通す。
    sf::Text MakeText(const std::string& a_text, unsigned int a_size, sf::Color a_color) const;

    void DrawText(sf::RenderTarget& a_target, const std::string& a_text, unsigned int a_size,
                  sf::Vector2f a_position, sf::Color a_color = sf::Color::White) const;
    // 横位置をウィンドウ中央に合わせて描く（文字数が変わっても中央に収まる）
    void DrawTextCentered(sf::RenderTarget& a_target, const std::string& a_text, unsigned int a_size, float a_y,
                          sf::Color a_color = sf::Color::White) const;
    // a_rightX が右端になるように描く
    void DrawTextRightAligned(sf::RenderTarget& a_target, const std::string& a_text, unsigned int a_size,
                              float a_rightX, float a_y, sf::Color a_color = sf::Color::White) const;

    // --- 画面の組み立て（描画先を選ばない）---
    void RenderTitle(sf::RenderTarget& a_target, const Showcase& a_pieces) const;
    void RenderGame(sf::RenderTarget& a_target, const Board& a_board, const HudState& a_hud,
                    const LogBook& a_logs) const;
    void RenderResult(sf::RenderTarget& a_target, int a_winner, const Showcase& a_pieces) const;

    // 駒の絵を横一列に並べて中央に置く（リザルト用）
    void RenderShowcase(sf::RenderTarget& a_target, const Showcase& a_pieces, float a_y, float a_size,
                        float a_spacing) const;

    // 駒の絵を Layout::TITLE_PIECE_POSITIONS の位置へ散らして置く（タイトル用）
    void RenderTitleShowcase(sf::RenderTarget& a_target, const Showcase& a_pieces) const;

    void RenderStatusBar(sf::RenderTarget& a_target, const HudState& a_hud) const;
    // 残り MP を丸の数で示す（数字より一目で分かるため）
    void RenderMpGauge(sf::RenderTarget& a_target, const HudState& a_hud) const;
    void RenderBoard(sf::RenderTarget& a_target, const Board& a_board, const HudState& a_hud) const;
    void RenderCursor(sf::RenderTarget& a_target, const HudState& a_hud) const;
    void RenderLogPanel(sf::RenderTarget& a_target, int a_team, sf::Vector2f a_panelPos,
                        const std::vector<LogMessage>& a_logs) const;
    void RenderLogEntry(sf::RenderTarget& a_target, const LogMessage& a_log, sf::Vector2f a_entryPos) const;
    void RenderTurnBanner(sf::RenderTarget& a_target, int a_turn) const;

    // ログ内のアイコンを a_size 四方に縮めて描く
    static void DrawIcon(sf::RenderTarget& a_target, const sf::Texture& a_texture, sf::Vector2f a_position,
                         float a_size, sf::Color a_color = sf::Color::White);

    // マスの左上座標（ピクセル）を返す
    static sf::Vector2f CellToPixel(int a_x, int a_y);
    static sf::RectangleShape MakeTile(int a_x, int a_y, sf::Color a_fillColor, float a_outlineThickness,
                                       sf::Color a_outlineColor);

    // 今いるマスから行動できる相手を枠で囲む（駒の上に重ねる）
    void RenderActionMarkers(sf::RenderTarget& a_target, const HudState& a_hud) const;

    sf::RenderWindow _window;
    sf::Font _font{};
};
