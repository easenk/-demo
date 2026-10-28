using System;
using System.Collections.Generic;

namespace AutoChess.Game
{
    /// <summary>
    /// 六族羁绊的战斗钩子实现（#22① 钩子型效果；由 SynergyTable.Default 的工厂逐档产出）。
    /// 共同纪律：
    ///  · 成员判定 = "开战瞬间场上带该标签的己方单位"快照（HashSet 引用集）；
    ///  · 载荷里的单位可能已死——动手前自查存活；
    ///  · 不引入新随机源；
    ///  · 构造器收 SynergyId（enum，P3-5），SourceId 属性跨界处 (int) 强转；
    ///  · 叠层类修饰器用独立号段（100+族 id，见 SynergySystem.BattleDynamicSourceBase）——
    ///    与族面板修饰器（族 id 本身）互不干扰，重算时两段一并清账。
    /// </summary>

    /// <summary>蛮兽 6 档：出手命中后叠攻速 3%、上限 10 层——一条随层数更新的攻击间隔百分比修饰器。</summary>
    public sealed class BeastStackHook : IBattleHook
    {
        private const int MaxStacks = 10;
        private const float IntervalPercentPerStack = -3f;   // 攻速 +3% = 攻击间隔 -3%（间隔维修饰器语义）

        private readonly HashSet<Unit> _members;
        private readonly int _sourceId;
        private readonly Dictionary<Unit, int> _stacks = new Dictionary<Unit, int>();
        private readonly Dictionary<Unit, StatModifier> _applied = new Dictionary<Unit, StatModifier>();

        public int SourceId => _sourceId;

        public BeastStackHook(IReadOnlyList<Unit> members, SynergyId synergyId)
        {
            _members = new HashSet<Unit>(members);
            _sourceId = (int)synergyId;
        }

        public void OnBattleStart(BattleContext ctx) { }

        public void OnBeforeAttack(BattleContext ctx, ref AttackDraft draft) { }

        public void OnAfterAttack(BattleContext ctx, in AttackResult result)
        {
            Unit attacker = result.Attacker;
            if (attacker == null || !attacker.IsAlive || !_members.Contains(attacker)) return;
            if (result.DamageApplied <= 0) return;   // 没造成伤害不算"命中"

            _stacks.TryGetValue(attacker, out int stacks);
            if (stacks >= MaxStacks) return;          // 封顶：层数与修饰器都不再变

            stacks++;
            _stacks[attacker] = stacks;

            // 更新层：先摘旧的那条（按值），再挂新的——独立号段保证只摘叠层、不碰档位修饰器
            if (_applied.TryGetValue(attacker, out var old))
                attacker.RemoveModifier(old);
            var fresh = new StatModifier(StatKind.AttackInterval, StatOp.Percent, IntervalPercentPerStack * stacks, _sourceId);
            attacker.AddModifier(fresh);
            _applied[attacker] = fresh;
        }

        public void OnDamaged(BattleContext ctx, in DamageResult result) { }

        public void OnDeath(BattleContext ctx, in DeathInfo death) { }

        public void OnTickEnd(BattleContext ctx) { }
    }

    /// <summary>铁壁 6 档：本族受伤 -12%，走守方减伤乘区（钳 90% 上限由 DamageFormula 管，#16③）。</summary>
    public sealed class IronwallReductionHook : IBattleHook
    {
        private const float ReductionPercent = 12f;

        private readonly HashSet<Unit> _members;
        private readonly int _sourceId;

        public int SourceId => _sourceId;

        public IronwallReductionHook(IReadOnlyList<Unit> members, SynergyId synergyId)
        {
            _members = new HashSet<Unit>(members);
            _sourceId = (int)synergyId;
        }

        public void OnBattleStart(BattleContext ctx) { }

        public void OnBeforeAttack(BattleContext ctx, ref AttackDraft draft)
        {
            if (draft.Target != null && _members.Contains(draft.Target))
                draft.DamageReductionPercent += ReductionPercent;
        }

        public void OnAfterAttack(BattleContext ctx, in AttackResult result) { }

        public void OnDamaged(BattleContext ctx, in DamageResult result) { }

        public void OnDeath(BattleContext ctx, in DeathInfo death) { }

        public void OnTickEnd(BattleContext ctx) { }
    }

    /// <summary>暗影 2 档：本族普攻吸血 12%——按实际伤害回复攻方（Heal 契约自带满血/死亡静默）。</summary>
    public sealed class ShadowLifestealHook : IBattleHook
    {
        private const float LifestealPercent = 12f;

        private readonly HashSet<Unit> _members;
        private readonly int _sourceId;

        public int SourceId => _sourceId;

        public ShadowLifestealHook(IReadOnlyList<Unit> members, SynergyId synergyId)
        {
            _members = new HashSet<Unit>(members);
            _sourceId = (int)synergyId;
        }

        public void OnBattleStart(BattleContext ctx) { }

        public void OnBeforeAttack(BattleContext ctx, ref AttackDraft draft) { }

        public void OnAfterAttack(BattleContext ctx, in AttackResult result)
        {
            if (result.DamageApplied <= 0) return;
            Unit attacker = result.Attacker;
            if (attacker == null || !attacker.IsAlive || !_members.Contains(attacker)) return;
            attacker.Heal(GameMath.RoundToInt(result.DamageApplied * LifestealPercent / 100f));
        }

        public void OnDamaged(BattleContext ctx, in DamageResult result) { }

        public void OnDeath(BattleContext ctx, in DeathInfo death) { }

        public void OnTickEnd(BattleContext ctx) { }
    }

    /// <summary>暗影 4 档：击杀回自身 10% 最大生命（#22④：死亡钩子携带击杀者）。</summary>
    public sealed class ShadowKillHealHook : IBattleHook
    {
        private const float KillHealPercent = 10f;

        private readonly HashSet<Unit> _members;
        private readonly int _sourceId;

        public int SourceId => _sourceId;

        public ShadowKillHealHook(IReadOnlyList<Unit> members, SynergyId synergyId)
        {
            _members = new HashSet<Unit>(members);
            _sourceId = (int)synergyId;
        }

        public void OnBattleStart(BattleContext ctx) { }

        public void OnBeforeAttack(BattleContext ctx, ref AttackDraft draft) { }

        public void OnAfterAttack(BattleContext ctx, in AttackResult result) { }

        public void OnDamaged(BattleContext ctx, in DamageResult result) { }

        public void OnDeath(BattleContext ctx, in DeathInfo death)
        {
            Unit killer = death.Killer;
            if (killer == null || !killer.IsAlive || !_members.Contains(killer)) return;
            killer.Heal(GameMath.RoundToInt(killer.EffectiveStats.MaxHp * KillHealPercent / 100f));
        }

        public void OnTickEnd(BattleContext ctx) { }
    }

    /// <summary>暗影 6 档：击杀后攻击条立即推进 50%（确定性推条，封顶蓄满在 CombatSystem 内）。</summary>
    public sealed class ShadowKillPushHook : IBattleHook
    {
        private const float PushFraction = 0.5f;

        private readonly HashSet<Unit> _members;
        private readonly int _sourceId;

        public int SourceId => _sourceId;

        public ShadowKillPushHook(IReadOnlyList<Unit> members, SynergyId synergyId)
        {
            _members = new HashSet<Unit>(members);
            _sourceId = (int)synergyId;
        }

        public void OnBattleStart(BattleContext ctx) { }

        public void OnBeforeAttack(BattleContext ctx, ref AttackDraft draft) { }

        public void OnAfterAttack(BattleContext ctx, in AttackResult result) { }

        public void OnDamaged(BattleContext ctx, in DamageResult result) { }

        public void OnDeath(BattleContext ctx, in DeathInfo death)
        {
            Unit killer = death.Killer;
            if (killer == null || !killer.IsAlive || !_members.Contains(killer)) return;
            ctx.PushAttackProgress(killer, PushFraction);
        }

        public void OnTickEnd(BattleContext ctx) { }
    }
}
