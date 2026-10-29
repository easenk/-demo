using System;

namespace AutoChess.Game
{
    /// <summary>装备形态（封闭集合 → enum，P3-5 划线原则）：散件 = 可升级的基础件；大件 = 升级终点、一次定型。</summary>
    public enum EquipmentKind
    {
        /// <summary>散件：3 种基础件（物理/法术/防御，#12①），持升级分支指针。</summary>
        Component = 1,

        /// <summary>大件：8 种升级终点（主维 ×2~2.5 + 第二维，#12②），一次定型不可换分支。</summary>
        Finished = 2,
    }

    /// <summary>
    /// 装备定义（表的每一行）。readonly struct——定义一旦入库永不修改。
    ///
    /// 升级分支制（决策 #12，2026-10-16 拍板取代二合一配方制）：
    ///   散件持 UpgradeOptions（可升成的大件 id 数组——分支选择权永远在玩家手里，#12⑧）；
    ///   大件持 UpgradeCost（升成本件的费用，占位 5~8 金，平衡期随千场压测重定）。
    /// 表内模板 SourceId 恒 0，挂载时由 GameSession 盖装备章（装备号段 1000 + 槽位号）。
    /// </summary>
    public readonly struct EquipmentDef
    {
        /// <summary>装备 id（>0；0 = 无/查无哨兵。开放内容表 → int，P3-5）。</summary>
        public readonly int Id;

        /// <summary>仅显示用（禁止当键）。</summary>
        public readonly string DisplayName;

        /// <summary>仅显示用（效果一句话描述）。</summary>
        public readonly string Desc;

        /// <summary>散件 / 大件。</summary>
        public readonly EquipmentKind Kind;

        /// <summary>面板加成模板（构造期防御性拷贝；SourceId 恒 0，挂载时盖章）。</summary>
        public readonly StatModifier[] Modifiers;

        /// <summary>散件才有：可升成的大件 id 数组（大件恒空——它是终点）。</summary>
        public readonly int[] UpgradeOptions;

        /// <summary>大件才有：升成本件的费用（散件恒 0）。</summary>
        public readonly int UpgradeCost;

        public EquipmentDef(int id, string displayName, string desc, EquipmentKind kind,
            StatModifier[] modifiers, int[] upgradeOptions, int upgradeCost)
        {
            Id = id;
            DisplayName = displayName ?? "";
            Desc = desc ?? "";
            Kind = kind;
            Modifiers = modifiers == null ? Array.Empty<StatModifier>() : (StatModifier[])modifiers.Clone();
            UpgradeOptions = upgradeOptions == null ? Array.Empty<int>() : (int[])upgradeOptions.Clone();
            UpgradeCost = upgradeCost;
        }
    }
}
