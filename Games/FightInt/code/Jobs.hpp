#pragma once

#include "Character.hpp"
#include "Config.hpp"

// ジョブ別派生クラス
//   いずれも Character の仮想関数を数行上書きするだけなので、
//   .cpp を分けず inline 定義のままヘッダに置く。

// 剣士: MP に余裕があれば 2 消費して 2 ダメージの強攻撃を行う
class Sword : public Character {
public:
    Sword(const sf::Texture& a_texture, int a_team) : Character{a_texture, Rule::SWORD_HP, a_team, "剣士"} {}

    int AttackCost(int a_currentMp) const override
    {
        return (a_currentMp >= Rule::SWORD_POWER_COST) ? Rule::SWORD_POWER_COST : Rule::DEF_ATTACK_COST;
    }

    int DamageValue(int a_currentMp) const override
    {
        return (a_currentMp >= Rule::SWORD_POWER_COST) ? Rule::SWORD_POWER_DAMAGE : Rule::DEF_DAMAGE;
    }
};

// 騎士: 高 HP。隣接する味方が狙われた時、代わりにダメージを受ける
class Knight : public Character {
public:
    Knight(const sf::Texture& a_texture, int a_team) : Character{a_texture, Rule::KNIGHT_HP, a_team, "騎士"} {}

    bool IsGuardian() const override { return true; }
};

// 弓兵: 移動先の上下左右に敵がいれば、移動せずその場から攻撃する
class Archer : public Character {
public:
    Archer(const sf::Texture& a_texture, int a_team) : Character{a_texture, Rule::ARCHER_HP, a_team, "弓兵"} {}

    bool AttacksOnApproach() const override { return true; }
};

// 魔術師: 縦移動を 1 ターンに 2 回まで MP 消費なしで行える
class Magician : public Character {
public:
    Magician(const sf::Texture& a_texture, int a_team) : Character{a_texture, Rule::MAGICIAN_HP, a_team, "魔術師"} {}

    int MoveCost(int a_dx, int a_dy, int a_skillUsedCount) const override
    {
        const bool isVerticalMove = (a_dx == 0 && a_dy != 0);
        if (isVerticalMove && a_skillUsedCount < Rule::FREE_MOVE_LIMIT) return 0;
        return Rule::DEF_MOVE_COST;
    }
};

// 医者: 通常の攻撃に加えて、隣接する味方の HP を回復できる
class Doctor : public Character {
public:
    Doctor(const sf::Texture& a_texture, int a_team) : Character{a_texture, Rule::DOCTOR_HP, a_team, "医者"} {}

    bool CanHeal() const override { return true; }
};

// ヴァン: 横移動を 1 ターンに 2 回まで MP 消費なしで行える
class Van : public Character {
public:
    Van(const sf::Texture& a_texture, int a_team) : Character{a_texture, Rule::VAN_HP, a_team, "ヴァン"} {}

    int MoveCost(int a_dx, int a_dy, int a_skillUsedCount) const override
    {
        const bool isHorizontalMove = (a_dy == 0 && a_dx != 0);
        if (isHorizontalMove && a_skillUsedCount < Rule::FREE_MOVE_LIMIT) return 0;
        return Rule::DEF_MOVE_COST;
    }
};
