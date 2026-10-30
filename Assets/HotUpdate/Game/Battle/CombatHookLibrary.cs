using System.Collections.Generic;

namespace AutoChess.Game
{
    /// <summary>
    /// 战斗钩子内容的通用实现库（批次5，阶段3-接口设计 第七章）：
    /// 把"改伤四乘区"（穿透/增伤/减伤/击碎削抗）与"吸血/击杀回复"做成可复用的钩子类，
    /// 内容侧（装备/策略/技能/Buff）按需实例化注册——CombatSystem 保持规则中立、零改动。
    ///
    /// 纪律（同 IBattleHook 头注释）：
    ///  · 不得引入新随机源（本库全部无随机）；
    ///  · SourceId 是注销句柄（构造传入，通常用 ModSourceId 对应号段）；
    ///  · 载荷里的单位可能已死——存活检查各钩子自担。
    /// </summary>
    public static class CombatHookLibrary
    {
        // ─────────────────────── 出手前：改伤四乘区 ───────────────────────

        /// <summary>
        /// 固定穿透：每次出手无视守方 X 点抗性（决策 #16②：穿透是"这一次伤害"的参数，不进面板）。
        /// 钩子写 draft.FlatPen——与普攻/技能共享唯一改伤挂点（批次2 口径）。
        /// </summary>
        public sealed class FlatPenHook : IBattleHook
        {
            private readonly int _flatPen;
            public int SourceId { get; }

            public FlatPenHook(int flatPen, int sourceId)
            {
                _flatPen = flatPen;
                SourceId = sourceId;
            }

            public void OnBattleStart(BattleContext ctx) { }
            public void OnBeforeAttack(BattleContext ctx, ref AttackDraft draft)
            {
                if (draft.Attacker == null || !draft.Attacker.IsAlive) return;
                draft.FlatPen += _flatPen;
            }
            public void OnAfterAttack(BattleContext ctx, in AttackResult result) { }
            public void OnDamaged(BattleContext ctx, in DamageResult result) { }
            public void OnDeath(BattleContext ctx, in DeathInfo death) { }
            public void OnTickEnd(BattleContext ctx) { }
        }

        /// <summary>
        /// 百分比穿透：每次出手无视守方 X% 抗性（决策 #16②；钳 0~100 由 DamageFormula 承担）。
        /// </summary>
        public sealed class PercentPenHook : IBattleHook
        {
            private readonly int _percentPen;
            public int SourceId { get; }

            public PercentPenHook(int percentPen, int sourceId)
            {
                _percentPen = percentPen;
                SourceId = sourceId;
            }

            public void OnBattleStart(BattleContext ctx) { }
            public void OnBeforeAttack(BattleContext ctx, ref AttackDraft draft)
            {
                if (draft.Attacker == null || !draft.Attacker.IsAlive) return;
                draft.PercentPen += _percentPen;
            }
            public void OnAfterAttack(BattleContext ctx, in AttackResult result) { }
            public void OnDamaged(BattleContext ctx, in DamageResult result) { }
            public void OnDeath(BattleContext ctx, in DeathInfo death) { }
            public void OnTickEnd(BattleContext ctx) { }
        }

        /// <summary>
        /// 增伤：攻方造成的伤害提高 X%（决策 #16③：独立乘区，恒累加）。
        /// </summary>
        public sealed class DamageAmpHook : IBattleHook
        {
            private readonly float _ampPercent;
            public int SourceId { get; }

            public DamageAmpHook(float ampPercent, int sourceId)
            {
                _ampPercent = ampPercent;
                SourceId = sourceId;
            }

            public void OnBattleStart(BattleContext ctx) { }
            public void OnBeforeAttack(BattleContext ctx, ref AttackDraft draft)
            {
                if (draft.Attacker == null || !draft.Attacker.IsAlive) return;
                draft.DamageAmpPercent += _ampPercent;
            }
            public void OnAfterAttack(BattleContext ctx, in AttackResult result) { }
            public void OnDamaged(BattleContext ctx, in DamageResult result) { }
            public void OnDeath(BattleContext ctx, in DeathInfo death) { }
            public void OnTickEnd(BattleContext ctx) { }
        }

        /// <summary>
        /// 减伤：成员受到的伤害降低 X%（决策 #16③：独立乘区，钳 90% 上限由 DamageFormula 承担）。
        /// 铁壁 6 档的 IronwallReductionHook 是它的同型实现（批次3 已有，语义一致）。
        /// </summary>
        public sealed class DamageReductionHook : IBattleHook
        {
            private readonly float _reductionPercent;
            private readonly HashSet<Unit> _members;   // null = 对所有己方单位生效（全队形态）

            public int SourceId { get; }

            /// <summary>成员制：只对名单内单位减伤（羁绊"本族"形态）。</summary>
            public DamageReductionHook(IReadOnlyList<Unit> members, float reductionPercent, int sourceId)
            {
                _members = members == null ? null : new HashSet<Unit>(members);
                _reductionPercent = reductionPercent;
                SourceId = sourceId;
            }

            /// <summary>无差别制：对所有被作用的单位减伤（全队形态——判定交给调用方的成员名单）。</summary>
            public DamageReductionHook(float reductionPercent, int sourceId)
            {
                _members = null;
                _reductionPercent = reductionPercent;
                SourceId = sourceId;
            }

            public void OnBattleStart(BattleContext ctx) { }
            public void OnBeforeAttack(BattleContext ctx, ref AttackDraft draft)
            {
                if (draft.Target == null) return;
                if (_members != null && !_members.Contains(draft.Target)) return;
                draft.DamageReductionPercent += _reductionPercent;
            }
            public void OnAfterAttack(BattleContext ctx, in AttackResult result) { }
            public void OnDamaged(BattleContext ctx, in DamageResult result) { }
            public void OnDeath(BattleContext ctx, in DeathInfo death) { }
            public void OnTickEnd(BattleContext ctx) { }
        }

        // ─────────────────────── 击碎/削抗（守方负向抗性修饰器，决策 #16②） ───────────────────────

        /// <summary>
        /// 击碎（Shred）：守方被命中时挂上负向抗性修饰器（护甲/法抗 -X，持续到战斗结束——
        /// 抗性可被压成负数，负值走 2-100/(100-抗性) 趋近 200% 承伤）。
        /// 挂点 = OnDamaged（守方视角"承受了这次伤害"）：普攻与技能命中都触发（与铁壁减伤同款语义）。
        /// 清理 = 备战期 RecalculateBoardEffects 兜底重算按源整批摘（既有路径，无需新机制）。
        /// </summary>
        public sealed class ShredHook : IBattleHook
        {
            private readonly StatKind _resistKind;    // Armor 或 MagicResist
            private readonly float _amount;            // 每次被命中削多少（负向 flat）
            private readonly int _maxStacks;           // 叠层上限（0 = 不限）
            private readonly Dictionary<Unit, int> _applied = new Dictionary<Unit, int>();

            public int SourceId { get; }

            public ShredHook(StatKind resistKind, float amount, int maxStacks, int sourceId)
            {
                if (resistKind != StatKind.Armor && resistKind != StatKind.MagicResist)
                    throw new System.ArgumentException($"击碎只作用于抗性维（Armor/MagicResist），当前 {resistKind}", nameof(resistKind));
                _resistKind = resistKind;
                _amount = amount;
                _maxStacks = maxStacks;
                SourceId = sourceId;
            }

            public void OnBattleStart(BattleContext ctx) { }
            public void OnBeforeAttack(BattleContext ctx, ref AttackDraft draft) { }
            public void OnAfterAttack(BattleContext ctx, in AttackResult result) { }

            public void OnDamaged(BattleContext ctx, in DamageResult result)
            {
                Unit target = result.Target;
                if (target == null || !target.IsAlive) return;          // 死了不叠
                if (result.Damage <= 0) return;                          // 没受到伤害不算命中
                _applied.TryGetValue(target, out int stacks);
                if (_maxStacks > 0 && stacks >= _maxStacks) return;     // 封顶
                _applied[target] = stacks + 1;
                target.AddModifier(new StatModifier(_resistKind, StatOp.Flat, -_amount, SourceId));
            }

            public void OnDeath(BattleContext ctx, in DeathInfo death) { }
            public void OnTickEnd(BattleContext ctx) { }
        }

        // ─────────────────────── 出手后：吸血/击杀回复（CombatRules 转钩子，批次5） ───────────────────────

        /// <summary>
        /// 普攻吸血：攻方每次普攻命中后，按实际伤害的 X% 回血（决策 #12⑥ 蛮兽/暗影同型）。
        /// 这是 CombatRules.PlayerLifestealPercent 的钩子形态——
        /// 施法不触发（OnAfterAttack 不对施法触发，批次2 口径）；
        /// 成员制（members = null 时对所有攻方生效——含敌方）。
        /// </summary>
        public sealed class LifestealHook : IBattleHook
        {
            private readonly float _percent;
            private readonly HashSet<Unit> _members;   // null = 任何攻方
            public int SourceId { get; }

            public LifestealHook(float percent, int sourceId)
            {
                _percent = percent;
                _members = null;
                SourceId = sourceId;
            }

            public LifestealHook(IReadOnlyList<Unit> members, float percent, int sourceId)
            {
                _percent = percent;
                _members = members == null ? null : new HashSet<Unit>(members);
                SourceId = sourceId;
            }

            public void OnBattleStart(BattleContext ctx) { }
            public void OnBeforeAttack(BattleContext ctx, ref AttackDraft draft) { }
            public void OnAfterAttack(BattleContext ctx, in AttackResult result)
            {
                if (result.DamageApplied <= 0) return;
                Unit attacker = result.Attacker;
                if (attacker == null || !attacker.IsAlive) return;
                if (_members != null && !_members.Contains(attacker)) return;
                int heal = GameMath.RoundToInt(result.DamageApplied * _percent / 100f);
                if (heal > 0) attacker.Heal(heal);
            }
            public void OnDamaged(BattleContext ctx, in DamageResult result) { }
            public void OnDeath(BattleContext ctx, in DeathInfo death) { }
            public void OnTickEnd(BattleContext ctx) { }
        }

        /// <summary>
        /// 击杀回复：击杀瞬间回击杀者有效上限的 X%（决策 #22④ 暗影 4 档同型；CombatRules.PlayerKillHealPercent 的钩子形态）。
        /// 对普攻击杀与技能击杀都触发（HandleKill 是共用收尾，批次2 已定）。
        /// </summary>
        public sealed class KillHealHook : IBattleHook
        {
            private readonly float _percent;
            private readonly HashSet<Unit> _members;   // null = 任何击杀者

            public int SourceId { get; }

            public KillHealHook(float percent, int sourceId)
            {
                _percent = percent;
                _members = null;
                SourceId = sourceId;
            }

            public KillHealHook(IReadOnlyList<Unit> members, float percent, int sourceId)
            {
                _percent = percent;
                _members = members == null ? null : new HashSet<Unit>(members);
                SourceId = sourceId;
            }

            public void OnBattleStart(BattleContext ctx) { }
            public void OnBeforeAttack(BattleContext ctx, ref AttackDraft draft) { }
            public void OnAfterAttack(BattleContext ctx, in AttackResult result) { }
            public void OnDamaged(BattleContext ctx, in DamageResult result) { }
            public void OnDeath(BattleContext ctx, in DeathInfo death)
            {
                Unit killer = death.Killer;
                if (killer == null || !killer.IsAlive) return;
                if (_members != null && !_members.Contains(killer)) return;
                int heal = GameMath.RoundToInt(killer.EffectiveStats.MaxHp * _percent / 100f);
                if (heal > 0) killer.Heal(heal);
            }
            public void OnTickEnd(BattleContext ctx) { }
        }
    }
}
