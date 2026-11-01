using System;
using System.Collections.Generic;

namespace AutoChess.Game
{
    /// <summary>
    /// 装备表（实例 + 可注入双源模式，同 CharacterTable/PolicyTable 纪律）：
    /// Default = 决策 #12 的 3 散件 + 8 大件（共 11 条；大件数值与效果 = 7c-1 用户定稿 2026-11-01）。
    ///
    /// 构造即校验（fail fast，两遍扫描）：
    ///   第一遍（登记）：id 唯一且 ≥1、修饰器非空（装备必须有面板效果——空效果装备是建表事故）；
    ///   第二遍（引用完整性）：散件必须带升级分支、每个分支 id 在表内且是大件；
    ///                       大件必须 UpgradeCost > 0 且不带分支（它是终点，#12③ 一次定型）。
    /// </summary>
    public sealed class EquipmentTable
    {
        public static EquipmentTable Default { get; } = BuildDefault();

        private readonly Dictionary<int, EquipmentDef> _byId = new Dictionary<int, EquipmentDef>();
        private readonly List<EquipmentDef> _all = new List<EquipmentDef>();

        public EquipmentTable(IReadOnlyList<EquipmentDef> defs)
        {
            if (defs == null) throw new ArgumentNullException(nameof(defs));

            foreach (var def in defs)
            {
                if (def.Id < 1)
                    throw new ArgumentException($"装备 id 必须 ≥ 1（当前 {def.Id}）");
                if (_byId.ContainsKey(def.Id))
                    throw new ArgumentException($"装备 id 重复：{def.Id}");
                if (def.Modifiers == null || def.Modifiers.Length == 0)
                    throw new ArgumentException($"装备 {def.Id}「{def.DisplayName}」必须至少带一条面板修饰器");
                _byId[def.Id] = def;
                _all.Add(def);
            }

            foreach (var def in _all)
            {
                if (def.Kind == EquipmentKind.Component)
                {
                    if (def.UpgradeOptions.Length == 0)
                        throw new ArgumentException($"散件 {def.Id}「{def.DisplayName}」必须带升级分支（#12②：分支选择权在玩家）");
                    foreach (int option in def.UpgradeOptions)
                    {
                        if (!_byId.TryGetValue(option, out var target) || target.Kind != EquipmentKind.Finished)
                            throw new ArgumentException($"散件 {def.Id}「{def.DisplayName}」的升级分支 {option} 不在表内或不是大件");
                    }
                }
                else
                {
                    if (def.UpgradeCost <= 0)
                        throw new ArgumentException($"大件 {def.Id}「{def.DisplayName}」必须带升级费（7c-1：物理/法术系 7 金、防御系 5 金）");
                    if (def.UpgradeOptions.Length > 0)
                        throw new ArgumentException($"大件 {def.Id}「{def.DisplayName}」是升级终点，不带分支（一次定型，#12③）");
                }
            }
        }

        /// <summary>全部装备定义（保持注册顺序）。</summary>
        public IReadOnlyList<EquipmentDef> All => _all;

        /// <summary>按 id 查定义；查无返回 default（Id=0）。调用方先查 Id 再用。</summary>
        public EquipmentDef Get(int id) => _byId.TryGetValue(id, out var def) ? def : default;

        /// <summary>
        /// 大件 v2（7c-1 用户定稿 2026-11-01；散件维持占位）：
        /// 3 散件（物理/法术/防御）+ 8 大件。id 布局：散件 1/2/3，大件 11~13（物理）、21~22（法术）、31~33（防御）。
        /// 升级费：物理/法术系 7 金、防御系 5 金。大件除面板外可带战斗钩子（穿戴者制）。
        /// </summary>
        private static EquipmentTable BuildDefault()
        {
            return new EquipmentTable(new List<EquipmentDef>
            {
                // ── 散件（#12①：基础件，持分支指针；数值维持占位）──
                new EquipmentDef(1, "物理散件", "攻击 +8", EquipmentKind.Component,
                    new[] { M(StatKind.AttackPower, StatOp.Flat, 8f) },
                    new[] { 11, 12, 13 }, 0),
                new EquipmentDef(2, "法术散件", "法强 +8", EquipmentKind.Component,
                    new[] { M(StatKind.AbilityPower, StatOp.Flat, 8f) },
                    new[] { 21, 22 }, 0),
                new EquipmentDef(3, "防御散件", "生命 +40、双抗 +5", EquipmentKind.Component,
                    new[]
                    {
                        M(StatKind.MaxHp, StatOp.Flat, 40f),
                        M(StatKind.Armor, StatOp.Flat, 5f),
                        M(StatKind.MagicResist, StatOp.Flat, 5f),
                    },
                    new[] { 31, 32, 33 }, 0),

                // ── 大件：物理三分支（#12②；升级费 7 金）──
                new EquipmentDef(11, "输出", "攻击 +20、攻击额外 +15%，普攻吸血 15%", EquipmentKind.Finished,
                    new[]
                    {
                        M(StatKind.AttackPower, StatOp.Flat, 20f),
                        M(StatKind.AttackPower, StatOp.Percent, 15f),
                    }, null, 7,
                    hookFactory: u => new CombatHookLibrary.LifestealHook(new[] { u }, 15f, ModSourceId.EquipmentBase + 11)),
                new EquipmentDef(12, "暴击", "暴击率 +20%、暴伤 +100%，击杀叠 10% 穿透（封顶 50%）", EquipmentKind.Finished,
                    new[]
                    {
                        M(StatKind.CritChance, StatOp.Flat, 20f),
                        M(StatKind.CritDamage, StatOp.Flat, 100f),
                    }, null, 7,
                    hookFactory: u => new CombatHookLibrary.KillPenStackHook(u, 10, 50, ModSourceId.EquipmentBase + 12)),
                new EquipmentDef(13, "攻速", "攻击 +20、攻速 +15%，普攻叠 5% 增伤（封顶 50%）", EquipmentKind.Finished,
                    new[]
                    {
                        M(StatKind.AttackPower, StatOp.Flat, 20f),
                        M(StatKind.AttackInterval, StatOp.Percent, -15f),
                    }, null, 7,
                    hookFactory: u => new CombatHookLibrary.AttackAmpStackHook(u, 5, 50, ModSourceId.EquipmentBase + 13)),

                // ── 大件：法术两分支（升级费 7 金）──
                new EquipmentDef(21, "回蓝", "命中回蓝 +5、每秒回蓝 +2、起手蓝 +15", EquipmentKind.Finished,
                    new[]
                    {
                        M(StatKind.ManaPerHit, StatOp.Flat, 5f),
                        M(StatKind.ManaRegenPerSecond, StatOp.Flat, 2f),
                        M(StatKind.InitialMana, StatOp.Flat, 15f),
                    }, null, 7),
                new EquipmentDef(22, "法强", "法强 +40、法强额外 +25%", EquipmentKind.Finished,
                    new[]
                    {
                        M(StatKind.AbilityPower, StatOp.Flat, 40f),
                        M(StatKind.AbilityPower, StatOp.Percent, 25f),
                    }, null, 7),

                // ── 大件：防御三分支（升级费 5 金）──
                new EquipmentDef(31, "生命", "生命 +300、生命额外 +10%，每秒回复已损生命 5%", EquipmentKind.Finished,
                    new[]
                    {
                        M(StatKind.MaxHp, StatOp.Flat, 300f),
                        M(StatKind.MaxHp, StatOp.Percent, 10f),
                    }, null, 5,
                    hookFactory: u => new CombatHookLibrary.LostHpRegenHook(u, 5, ModSourceId.EquipmentBase + 31)),
                new EquipmentDef(32, "物防", "物防 +35、生命 +150，每 2 秒灼伤周围敌人其最大生命的 2.5%", EquipmentKind.Finished,
                    new[]
                    {
                        M(StatKind.Armor, StatOp.Flat, 35f),
                        M(StatKind.MaxHp, StatOp.Flat, 150f),
                    }, null, 5,
                    hookFactory: u => new CombatHookLibrary.BurnAuraHook(u, 2.5f, 2, ModSourceId.EquipmentBase + 32)),
                new EquipmentDef(33, "魔抗", "法抗 +40、受法术伤害降低 30%，每 2 秒回复自身最大生命的 2.5%", EquipmentKind.Finished,
                    new[]
                    {
                        M(StatKind.MagicResist, StatOp.Flat, 40f),
                    }, null, 5,
                    hookFactory: u => new CombatHookLibrary.CompositeHook(
                        new CombatHookLibrary.TypeDamageReductionHook(u, DamageType.Magic, 30f, ModSourceId.EquipmentBase + 33),
                        new CombatHookLibrary.MaxHpRegenHook(u, 2.5f, 2, ModSourceId.EquipmentBase + 133))),
            });

            // 表内模板速记：SourceId 恒 0，挂载时由 GameSession 盖装备章（1000 + 槽位号）
            static StatModifier M(StatKind kind, StatOp op, float value)
                => new StatModifier(kind, op, value, 0);
        }
    }
}
