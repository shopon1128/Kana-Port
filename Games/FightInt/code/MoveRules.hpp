#pragma once

#include "Config.hpp"

#include <SFML/System/Vector2.hpp>

#include <array>

class Board;
class Character;

// 盤面のマスごとの真偽値（[y][x]）
using CellFlags = std::array<std::array<bool, Rule::GRID_SIZE>, Rule::GRID_SIZE>;

// 駒の行動判定
//   「方向キーを押したら何が起きるか」と「どこまで行けるか」を求める、
//   ゲーム進行の状態を持たない純粋な計算。
//
//   画面のハイライトと実際の操作は必ずここの ResolveMove を通すこと。
//   同じ判定を共有することで、表示と挙動がズレないようにしている。

namespace MoveRules {

// 方向キーを押した時に起きること
enum class MoveOutcome {
    None,   // 何も起きない（盤外・MP 不足など）
    Move,   // 空きマスへ移動する
    Attack, // 敵を攻撃する（駒は移動しない）
    Heal,   // 味方を回復する（医者。駒は移動しない）
};

struct MoveResolution {
    MoveOutcome outcome{MoveOutcome::None};
    sf::Vector2i targetCell{-1, -1}; // Move なら移動先、Attack/Heal なら対象のマス
};

// 今いるマスから行動できる相手
struct ActionTargets {
    CellFlags attack{}; // 攻撃できる敵のマス
    CellFlags heal{};   // 回復できる味方のマス（医者のみ）
};

// その駒が「移動」としてそのマスへ入れるか。
// 駒がいるマスは攻撃・回復になるため移動先にはならない。
// 弓兵は敵に隣接するマスへ踏み込むと自動攻撃に変わるため、そこへも入れない。
bool CanEnterCell(const Board& a_board, const Character& a_character, int a_x, int a_y);

// a_from にいる a_character が a_dir 方向のキーを押した時に何が起きるかを返す
MoveResolution ResolveMove(const Board& a_board, const Character& a_character, sf::Vector2i a_from,
                           sf::Vector2i a_dir, int a_availableMp);

// 4 方向すべてについて ResolveMove を試し、攻撃・回復の対象になるマスを集める
ActionTargets FindActionTargets(const Board& a_board, const Character& a_character, sf::Vector2i a_from,
                                int a_availableMp);

// 掴んだ駒がどこまで行けるか
struct MoveRange {
    CellFlags reachable{}; // 残り MP で到達できるマス（freeMove も含む）
    CellFlags freeMove{};  // MP を 1 も使わずに到達できるマス（魔術師の縦・ヴァンの横）
};

// a_from にいる a_character が、残り MP を a_availableMp として到達できるマスを返す。
// 出発マス自身は含めない。
MoveRange FindMoveRange(const Board& a_board, const Character& a_character, sf::Vector2i a_from,
                        int a_availableMp);

// 指定チームの騎士が受け止める範囲（各騎士自身とその上下左右）を集める。
// この範囲に入ったダメージは騎士が引き受ける（騎士自身のマスも、隣に別の騎士が
// いなければ騎士本人が受けるので同じこと）。騎士を中心とした十字になるので、
// 常時表示すると「騎士の能力の及ぶ範囲」として読める。
CellFlags GuardAuraCells(const Board& a_board, int a_team);

} // namespace MoveRules
