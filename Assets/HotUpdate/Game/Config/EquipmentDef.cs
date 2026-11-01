using System;

namespace AutoChess.Game
{
    /// <summary>装备形态（封闭集合 → enum，P3-5 划线原则）：散件 = 可升级的基础件；大件 = 升级终点、一次定型。</summary>
    public enum EquipmentKind
    {
        /// <summary>散件：3 种基础件（物理/法术/防御，#12①），持升级分支指针。</summary>
        Component = 1,

        /// <summary>大件：8 种升级终点（主维 ×2~2.5 + 第二维 + 可选被动，#12②），一次定型不可换分支。</summary>
        Finished = 2,
    }

    /// <summary>
    /// 装备定义（表的每一行）。readonly struct——定义一旦入库永不修改。
    ///
    /// 升级分支制（决策 #12，2026-10-16 拍板取代二合一配方制）：
    ///   散件持 UpgradeOptions（可升成的大件 id 数组——分支选择权永远在玩家手里，#12⑧）；
    ///   大件持 UpgradeCost（升成本件的费用；7c-1 定稿：物理/法术系 7 金、防御系 5 金）。
    /// 表内模板 SourceId 恒 0，挂载时由 GameSession 盖装备章（装备号段 1000 + 槽位号）。
    ///
    /// 7c-1 大件 v2：面板之外可携带**战斗钩子**（吸血/叠层/光环/类型乘区——HookFactory）：
    /// 穿戴者制（参数 = 穿戴它的 Unit），每场 StartBattle 新造一只注册、只挂场上玩家单位（P3-2 同构）。
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

        /// <summary>战斗钩子工厂（大件 v2；null = 无被动）。参数 = 穿戴者——装备效果只属于穿它的人；
        /// 每场新造（钩子不吃跨场状态，与羁绊/策略钩子同款生命周期纪律）。</summary>
        public readonly Func<Unit, IBattleHook> HookFactory;

        public EquipmentDef(int id, string displayName, string desc, EquipmentKind kind,
            StatModifier[] modifiers, int[] upgradeOptions, int upgradeCost,
            Func<Unit, IBattleHook> hookFactory = null)
        {
            Id = id;
            DisplayName = displayName ?? "";
            Desc = desc ?? "";
            Kind = kind;
            Modifiers = modifiers == null ? Array.Empty<StatModifier>() : (StatModifier[])modifiers.Clone();
            UpgradeOptions = upgradeOptions == null ? Array.Empty<int>() : (int[])upgradeOptions.Clone();
            UpgradeCost = upgradeCost;
            HookFactory = hookFactory;
        }
    }
}
