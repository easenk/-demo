using System;
using System.Collections.Generic;

namespace AutoChess.Game
{
    /// <summary>
    /// 羁绊表（实例 + 可注入双源模式）：Default = 决策 #22 六族 v1（档位 2/4/6、效果高档含低档、
    /// 计数只看场上不同棋子）。构造即校验（fail fast）：Id 不能为 None、唯一、enum 成员值必须落在
    /// 1~999 羁绊号段内（跨界强转 int 校验——防将来加成员时手滑配出撞装备/策略号段的值）、
    /// 档位 Count 严格升序、每档至少一种效果——建表事故构造期炸。
    /// </summary>
    public sealed class SynergyTable
    {
        public static SynergyTable Default { get; } = new SynergyTable(BuildDefault());

        private readonly List<SynergyDef> _all;
        private readonly Dictionary<SynergyId, SynergyDef> _byId;

        public SynergyTable(IReadOnlyList<SynergyDef> defs)
        {
            if (defs == null) throw new ArgumentNullException(nameof(defs));

            _all = new List<SynergyDef>(defs.Count);
            _byId = new Dictionary<SynergyId, SynergyDef>(defs.Count);
            var seen = new HashSet<SynergyId>();
            foreach (var def in defs)
            {
                if (def.Id == SynergyId.None)
                    throw new ArgumentException("羁绊 Id 不能为 None——None 是哨兵不是一条羁绊");
                int rawId = (int)def.Id;
                if (rawId < ModSourceId.SystemBase || rawId > ModSourceId.SystemMax)
                    throw new ArgumentException(
                        $"羁绊 Id 必须在 {ModSourceId.SystemBase}~{ModSourceId.SystemMax} 号段内（当前 {rawId}）——enum 成员值越段会撞其他号段");
                if (!seen.Add(def.Id))
                    throw new ArgumentException($"羁绊 Id 重复：{def.Id}");
                if (def.Tiers == null || def.Tiers.Length == 0)
                    throw new ArgumentException($"羁绊「{def.DisplayName}」至少要有一个档位");

                for (int i = 0; i < def.Tiers.Length; i++)
                {
                    if (i > 0 && def.Tiers[i].Count <= def.Tiers[i - 1].Count)
                        throw new ArgumentException($"羁绊「{def.DisplayName}」档位 Count 必须严格升序");
                    if (!def.Tiers[i].HasAnyEffect)
                        throw new ArgumentException($"羁绊「{def.DisplayName}」第 {i} 档没有任何效果（建表事故）");
                }
                _all.Add(def);
                _byId.Add(def.Id, def);
            }
        }

        /// <summary>全部族（保持传入顺序 = 快照顺序）。</summary>
        public IReadOnlyList<SynergyDef> All => _all;

        /// <summary>查表：未知 id（含 None）返回 default（Id=None 哨兵）。</summary>
        public SynergyDef Get(SynergyId id)
        {
            return _byId.TryGetValue(id, out var def) ? def : default;
        }

        // ── 决策 #22 六族 v1（覆盖制编表：高档条目自带完整效果集——低档效果在高档内重述；
        // 效果数值 = 第一版锚点，阶段3 压测期精调）──

        private static SynergyDef[] BuildDefault()
        {
            return new SynergyDef[]
            {
                new SynergyDef(SynergyId.Brute, "蛮兽", new SynergyTierDef[]
                {
                    new SynergyTierDef(2, "2 蛮兽：本族攻速 +12%",
                        traitModifiers: new[] { M(StatKind.AttackInterval, StatOp.Percent, -12f) }),
                    new SynergyTierDef(4, "4 蛮兽：本族攻速 +12%、攻击 +15%",
                        traitModifiers: new[]
                        {
                            M(StatKind.AttackInterval, StatOp.Percent, -12f),
                            M(StatKind.AttackPower, StatOp.Percent, 15f),
                        }),
                    new SynergyTierDef(6, "6 蛮兽：本族攻速 +12%、攻击 +15%、命中叠攻速 3%（上限 10 层）",
                        traitModifiers: new[]
                        {
                            M(StatKind.AttackInterval, StatOp.Percent, -12f),
                            M(StatKind.AttackPower, StatOp.Percent, 15f),
                        },
                        hookFactories: new[] { (Func<IReadOnlyList<Unit>, IBattleHook>)(members => new BeastStackHook(members, SynergyId.Brute)) }),
                }),
                new SynergyDef(SynergyId.Bulwark, "铁壁", new SynergyTierDef[]
                {
                    new SynergyTierDef(2, "2 铁壁：本族护甲 +20",
                        traitModifiers: new[] { M(StatKind.Armor, StatOp.Flat, 20f) }),
                    new SynergyTierDef(4, "4 铁壁：本族护甲 +20、法抗 +20",
                        traitModifiers: new[]
                        {
                            M(StatKind.Armor, StatOp.Flat, 20f),
                            M(StatKind.MagicResist, StatOp.Flat, 20f),
                        }),
                    new SynergyTierDef(6, "6 铁壁：本族护甲 +20、法抗 +20、受伤 -12%（减伤乘区）",
                        traitModifiers: new[]
                        {
                            M(StatKind.Armor, StatOp.Flat, 20f),
                            M(StatKind.MagicResist, StatOp.Flat, 20f),
                        },
                        hookFactories: new[] { (Func<IReadOnlyList<Unit>, IBattleHook>)(members => new IronwallReductionHook(members, SynergyId.Bulwark)) }),
                }),
                new SynergyDef(SynergyId.Arcane, "秘法", new SynergyTierDef[]
                {
                    new SynergyTierDef(2, "2 秘法：本族法强 +20",
                        traitModifiers: new[] { M(StatKind.AbilityPower, StatOp.Flat, 20f) }),
                    new SynergyTierDef(4, "4 秘法：本族法强 +20、命中回蓝 +2",
                        traitModifiers: new[]
                        {
                            M(StatKind.AbilityPower, StatOp.Flat, 20f),
                            M(StatKind.ManaPerHit, StatOp.Flat, 2f),
                        }),
                    // 混范围档实例：同一档里全队与本族各走各的数组
                    new SynergyTierDef(6, "6 秘法：全队法强 +25、本族法强 +20、命中回蓝 +2、每秒回蓝 +2",
                        traitModifiers: new[]
                        {
                            M(StatKind.AbilityPower, StatOp.Flat, 20f),
                            M(StatKind.ManaPerHit, StatOp.Flat, 2f),
                            M(StatKind.ManaRegenPerSecond, StatOp.Flat, 2f),
                        },
                        teamModifiers: new[] { M(StatKind.AbilityPower, StatOp.Flat, 25f) }),
                }),
                new SynergyDef(SynergyId.Ranger, "游侠", new SynergyTierDef[]
                {
                    new SynergyTierDef(2, "2 游侠：本族暴击率 +8%",
                        traitModifiers: new[] { M(StatKind.CritChance, StatOp.Flat, 8f) }),
                    new SynergyTierDef(4, "4 游侠：本族暴击率 +8%、暴击伤害 +20%",
                        traitModifiers: new[]
                        {
                            M(StatKind.CritChance, StatOp.Flat, 8f),
                            M(StatKind.CritDamage, StatOp.Flat, 20f),
                        }),
                    // 暴伤重述为累计值 +60%（4 档 +20% 累计至此）；全队暴击率 +8% 对本族同样生效
                    new SynergyTierDef(6, "6 游侠：全队暴击率 +8%、本族暴击伤害 +60%",
                        traitModifiers: new[]
                        {
                            M(StatKind.CritChance, StatOp.Flat, 8f),
                            M(StatKind.CritDamage, StatOp.Flat, 60f),
                        },
                        teamModifiers: new[] { M(StatKind.CritChance, StatOp.Flat, 8f) }),
                }),
                new SynergyDef(SynergyId.Shadow, "暗影", new SynergyTierDef[]
                {
                    new SynergyTierDef(2, "2 暗影：本族普攻吸血 12%",
                        hookFactories: new Func<IReadOnlyList<Unit>, IBattleHook>[]
                        {
                            members => new ShadowLifestealHook(members, SynergyId.Shadow),
                        }),
                    new SynergyTierDef(4, "4 暗影：普攻吸血 12%、击杀回自身 10% 最大生命",
                        hookFactories: new Func<IReadOnlyList<Unit>, IBattleHook>[]
                        {
                            members => new ShadowLifestealHook(members, SynergyId.Shadow),
                            members => new ShadowKillHealHook(members, SynergyId.Shadow),
                        }),
                    new SynergyTierDef(6, "6 暗影：普攻吸血 12%、击杀回 10% 最大生命、击杀后攻击条推进 50%",
                        hookFactories: new Func<IReadOnlyList<Unit>, IBattleHook>[]
                        {
                            members => new ShadowLifestealHook(members, SynergyId.Shadow),
                            members => new ShadowKillHealHook(members, SynergyId.Shadow),
                            members => new ShadowKillPushHook(members, SynergyId.Shadow),
                        }),
                }),
                new SynergyDef(SynergyId.Crown, "王庭", new SynergyTierDef[]
                {
                    new SynergyTierDef(2, "2 王庭：全队生命 +10%",
                        teamModifiers: new[] { M(StatKind.MaxHp, StatOp.Percent, 10f) }),
                    new SynergyTierDef(4, "4 王庭：全队生命 +10%、攻击 +10%",
                        teamModifiers: new[]
                        {
                            M(StatKind.MaxHp, StatOp.Percent, 10f),
                            M(StatKind.AttackPower, StatOp.Percent, 10f),
                        }),
                    new SynergyTierDef(6, "6 王庭：全队生命 +10%、攻击 +10%、双抗 +15",
                        teamModifiers: new[]
                        {
                            M(StatKind.MaxHp, StatOp.Percent, 10f),
                            M(StatKind.AttackPower, StatOp.Percent, 10f),
                            M(StatKind.Armor, StatOp.Flat, 15f),
                            M(StatKind.MagicResist, StatOp.Flat, 15f),
                        }),
                }),
            };

            // 表内模板速记：SourceId 恒 0，挂载时由 SynergySystem 盖族章
            static StatModifier M(StatKind kind, StatOp op, float value)
                => new StatModifier(kind, op, value, 0);
        }
    }
}
