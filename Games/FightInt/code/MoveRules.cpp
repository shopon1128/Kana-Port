#include "MoveRules.hpp"

#include "Board.hpp"
#include "Character.hpp"

#include <algorithm>
#include <cstddef>
#include <deque>
#include <limits>

namespace {

// 到達範囲探索の状態。無料移動スキルの残り回数で先のコストが変わるため、
// 座標だけでなくスキル使用回数も状態に含める
struct SearchNode {
    int x{};
    int y{};
    int skillUsed{};
};

// 弓兵の自動攻撃の対象を探す。移動先の上下左右にいる最初の敵を返す。
// 探索順（上・左・右・下）がそのまま「どの敵を撃つか」になる。
const Character* FindApproachTarget(const Board& a_board, const Character& a_character, sf::Vector2i a_dest,
                                    sf::Vector2i& a_outCell)
{
    for (const sf::Vector2i& dir : Rule::ORTHOGONAL_DIRS) {
        const int ax = a_dest.x + dir.x;
        const int ay = a_dest.y + dir.y;
        if (!Board::IsInside(ax, ay)) continue;

        const Character* enemy = a_board.At(ax, ay);
        if (enemy == nullptr || enemy->Team() == a_character.Team()) continue;

        a_outCell = {ax, ay};
        return enemy;
    }
    return nullptr;
}

} // namespace

namespace MoveRules {

bool CanEnterCell(const Board& a_board, const Character& a_character, int a_x, int a_y)
{
    if (!a_board.IsEmpty(a_x, a_y)) return false; // 駒がいるマスへ進むと攻撃・回復になる

    if (!a_character.AttacksOnApproach()) return true;

    // 弓兵は移動先の上下左右に敵がいると、移動ではなく自動攻撃になる
    sf::Vector2i ignored{};
    return FindApproachTarget(a_board, a_character, {a_x, a_y}, ignored) == nullptr;
}

MoveResolution ResolveMove(const Board& a_board, const Character& a_character, sf::Vector2i a_from,
                           sf::Vector2i a_dir, int a_availableMp)
{
    MoveResolution resolution{};
    if (a_availableMp <= 0) return resolution;

    const sf::Vector2i dest{a_from.x + a_dir.x, a_from.y + a_dir.y};
    if (!Board::IsInside(dest.x, dest.y)) return resolution;

    // 弓兵は移動先の隣に敵がいれば、移動せずその敵を撃つ
    if (a_character.AttacksOnApproach()) {
        sf::Vector2i enemyCell{};
        if (FindApproachTarget(a_board, a_character, dest, enemyCell) != nullptr) {
            resolution.outcome = MoveOutcome::Attack;
            resolution.targetCell = enemyCell;
            return resolution;
        }
    }

    const Character* occupant = a_board.At(dest.x, dest.y);

    // 医者は移動先に味方がいれば回復する。MP が足りなくても移動には変わらない
    if (a_character.CanHeal() && occupant != nullptr && occupant->Team() == a_character.Team()) {
        if (a_availableMp >= Rule::HEAL_COST) {
            resolution.outcome = MoveOutcome::Heal;
            resolution.targetCell = dest;
        }
        return resolution;
    }

    if (occupant == nullptr) {
        const int cost = a_character.MoveCost(a_dir.x, a_dir.y, a_character.SkillUsedCount());
        if (a_availableMp >= cost) {
            resolution.outcome = MoveOutcome::Move;
            resolution.targetCell = dest;
        }
        return resolution;
    }

    // 敵がいるマスへ進もうとすると攻撃になる
    if (occupant->Team() != a_character.Team() && a_character.CanAttack() &&
        a_availableMp >= a_character.AttackCost(a_availableMp)) {
        resolution.outcome = MoveOutcome::Attack;
        resolution.targetCell = dest;
    }
    return resolution;
}

ActionTargets FindActionTargets(const Board& a_board, const Character& a_character, sf::Vector2i a_from,
                                int a_availableMp)
{
    ActionTargets targets{};
    for (const sf::Vector2i& dir : Rule::ORTHOGONAL_DIRS) {
        const MoveResolution resolution = ResolveMove(a_board, a_character, a_from, dir, a_availableMp);
        if (resolution.outcome != MoveOutcome::Attack && resolution.outcome != MoveOutcome::Heal) continue;

        const auto x = static_cast<std::size_t>(resolution.targetCell.x);
        const auto y = static_cast<std::size_t>(resolution.targetCell.y);
        if (resolution.outcome == MoveOutcome::Attack) targets.attack[y][x] = true;
        else targets.heal[y][x] = true;
    }
    return targets;
}

MoveRange FindMoveRange(const Board& a_board, const Character& a_character, sf::Vector2i a_from,
                        int a_availableMp)
{
    MoveRange range{};

    // 無料移動スキルは「このターンに何回使ったか」で発動可否が変わるので、
    // (マス, スキル使用回数) を 1 つの状態として最小消費 MP を求める。
    constexpr int SKILL_STATES = Rule::FREE_MOVE_LIMIT + 1;
    constexpr int UNREACHED = std::numeric_limits<int>::max();
    using CostTable = std::array<std::array<std::array<int, SKILL_STATES>, Board::SIZE>, Board::SIZE>;

    CostTable cost{};
    for (auto& row : cost) {
        for (auto& cell : row) cell.fill(UNREACHED);
    }
    const auto costAt = [&cost](int a_x, int a_y, int a_skill) -> int& {
        return cost[static_cast<std::size_t>(a_y)][static_cast<std::size_t>(a_x)][static_cast<std::size_t>(a_skill)];
    };

    const int startSkill = std::min(a_character.SkillUsedCount(), Rule::FREE_MOVE_LIMIT);
    costAt(a_from.x, a_from.y, startSkill) = 0;

    // 1 歩のコストは 0 か 1 しかないので、両端キューによる 0-1 BFS で最小コストが求まる
    std::deque<SearchNode> queue;
    queue.push_back({a_from.x, a_from.y, startSkill});

    while (!queue.empty()) {
        const SearchNode node = queue.front();
        queue.pop_front();
        const int currentCost = costAt(node.x, node.y, node.skillUsed);

        for (const sf::Vector2i& dir : Rule::ORTHOGONAL_DIRS) {
            const int nx = node.x + dir.x;
            const int ny = node.y + dir.y;
            if (!Board::IsInside(nx, ny)) continue;
            if (!CanEnterCell(a_board, a_character, nx, ny)) continue;

            const int stepCost = a_character.MoveCost(dir.x, dir.y, node.skillUsed);
            const int nextCost = currentCost + stepCost;
            if (nextCost > a_availableMp) continue; // 残り MP では届かない

            const int nextSkill = (stepCost == 0) ? node.skillUsed + 1 : node.skillUsed;
            if (nextCost >= costAt(nx, ny, nextSkill)) continue;

            costAt(nx, ny, nextSkill) = nextCost;
            if (stepCost == 0) {
                queue.push_front({nx, ny, nextSkill});
            } else {
                queue.push_back({nx, ny, nextSkill});
            }
        }
    }

    for (int y = 0; y < Board::SIZE; ++y) {
        for (int x = 0; x < Board::SIZE; ++x) {
            if (x == a_from.x && y == a_from.y) continue; // 出発マスは塗らない

            const auto& costsForCell = cost[static_cast<std::size_t>(y)][static_cast<std::size_t>(x)];
            const auto ux = static_cast<std::size_t>(x);
            const auto uy = static_cast<std::size_t>(y);

            range.reachable[uy][ux] = std::any_of(costsForCell.begin(), costsForCell.end(),
                                                  [a_availableMp](int a_cost) { return a_cost <= a_availableMp; });
            // 消費 0 で行き着ける経路があるかどうか
            range.freeMove[uy][ux] =
                std::any_of(costsForCell.begin(), costsForCell.end(), [](int a_cost) { return a_cost == 0; });
        }
    }
    return range;
}

CellFlags GuardAuraCells(const Board& a_board, int a_team)
{
    CellFlags aura{};
    const auto mark = [&aura](int a_x, int a_y) {
        if (Board::IsInside(a_x, a_y)) aura[static_cast<std::size_t>(a_y)][static_cast<std::size_t>(a_x)] = true;
    };

    for (int y = 0; y < Board::SIZE; ++y) {
        for (int x = 0; x < Board::SIZE; ++x) {
            const Character* knight = a_board.At(x, y);
            if (knight == nullptr || !knight->IsAlive()) continue;
            if (!knight->IsGuardian() || knight->Team() != a_team) continue;

            // 騎士自身と上下左右。十字に塗ることで騎士の能力だと読み取れるようにする
            mark(x, y);
            for (const sf::Vector2i& dir : Rule::ORTHOGONAL_DIRS) mark(x + dir.x, y + dir.y);
        }
    }
    return aura;
}

} // namespace MoveRules
