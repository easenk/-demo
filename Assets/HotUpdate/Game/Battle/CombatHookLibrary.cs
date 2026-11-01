using System;
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

        // ─────────────────────── 7c-1 扩充：装备大件 v2 / 策略 v2 的效果钩子 ───────────────────────
        // 以下钩子全部"穿戴者制"（构造传 Unit wearer——装备效果只属于穿它的人），
        // 唯独 TypeDamageAmpHook 是成员制（策略 4/5 的全队增伤用）。
        // 周期型钩子的节拍：内部 tick 计数器，每场随钩子新造归零——与羁绊钩子同款生命周期纪律。

        /// <summary>
        /// 击杀叠穿透（暴击大件）：穿戴者每次击杀 +X% 百分比穿透，封顶 maxPercent。
        /// 层数记在钩子内部（穿透是"这一次伤害"的参数不进面板，#16②）——普攻与技能出手都吃到。
        /// </summary>
        public sealed class KillPenStackHook : IBattleHook
        {
            private readonly Unit _wearer;
            private readonly int _percentPerKill;
            private readonly int _maxPercent;
            private readonly Dictionary<Unit, int> _stacks = new Dictionary<Unit, int>();

            public int SourceId { get; }

            public KillPenStackHook(Unit wearer, int percentPerKill, int maxPercent, int sourceId)
            {
                _wearer = wearer;
                _percentPerKill = percentPerKill;
                _maxPercent = maxPercent;
                SourceId = sourceId;
            }

            public void OnBattleStart(BattleContext ctx) { }
            public void OnBeforeAttack(BattleContext ctx, ref AttackDraft draft)
            {
                if (draft.Attacker != _wearer || !draft.Attacker.IsAlive) return;
                _stacks.TryGetValue(_wearer, out int s);
                draft.PercentPen += s * _percentPerKill;
            }
            public void OnAfterAttack(BattleContext ctx, in AttackResult result) { }
            public void OnDamaged(BattleContext ctx, in DamageResult result) { }
            public void OnDeath(BattleContext ctx, in DeathInfo death)
            {
                if (death.Killer != _wearer) return;
                _stacks.TryGetValue(_wearer, out int s);
                int pen = (s + 1) * _percentPerKill;
                if (pen <= _maxPercent) _stacks[_wearer] = s + 1;   // 封顶后静默不叠
            }
            public void OnTickEnd(BattleContext ctx) { }
        }

        /// <summary>
        /// 攻击叠增伤（攻速大件）：穿戴者每次普攻命中叠 +X% 增伤乘区，封顶 maxPercent。
        /// 叠层由普攻触发（OnAfterAttack = 普攻收尾，批次2 口径），效果作用于普攻与技能的全部出手。
        /// </summary>
        public sealed class AttackAmpStackHook : IBattleHook
        {
            private readonly Unit _wearer;
            private readonly int _percentPerStack;
            private readonly int _maxPercent;
            private int _stacks;

            public int SourceId { get; }

            public AttackAmpStackHook(Unit wearer, int percentPerStack, int maxPercent, int sourceId)
            {
                _wearer = wearer;
                _percentPerStack = percentPerStack;
                _maxPercent = maxPercent;
                SourceId = sourceId;
            }

            public void OnBattleStart(BattleContext ctx) { }
            public void OnBeforeAttack(BattleContext ctx, ref AttackDraft draft)
            {
                if (draft.Attacker != _wearer || !draft.Attacker.IsAlive) return;
                draft.DamageAmpPercent += _stacks * _percentPerStack;
            }
            public void OnAfterAttack(BattleContext ctx, in AttackResult result)
            {
                if (result.Attacker != _wearer || !_wearer.IsAlive || result.DamageApplied <= 0) return;
                if ((_stacks + 1) * _percentPerStack <= _maxPercent) _stacks++;   // 封顶后静默不叠
            }
            public void OnDamaged(BattleContext ctx, in DamageResult result) { }
            public void OnDeath(BattleContext ctx, in DeathInfo death) { }
            public void OnTickEnd(BattleContext ctx) { }
        }

        /// <summary>按已损生命回复（生命大件）：每秒回复穿戴者已损生命的 X%。</summary>
        public sealed class LostHpRegenHook : IBattleHook
        {
            private readonly Unit _wearer;
            private readonly float _percentPerSecond;
            private int _tick;

            public int SourceId { get; }

            public LostHpRegenHook(Unit wearer, float percentPerSecond, int sourceId)
            {
                _wearer = wearer;
                _percentPerSecond = percentPerSecond;
                SourceId = sourceId;
            }

            public void OnBattleStart(BattleContext ctx) { }
            public void OnBeforeAttack(BattleContext ctx, ref AttackDraft draft) { }
            public void OnAfterAttack(BattleContext ctx, in AttackResult result) { }
            public void OnDamaged(BattleContext ctx, in DamageResult result) { }
            public void OnDeath(BattleContext ctx, in DeathInfo death) { }
            public void OnTickEnd(BattleContext ctx)
            {
                _tick++;
                if (_tick % GameConst.TicksPerSecond != 0) return;   // 每秒一跳
                if (!_wearer.IsAlive || !_wearer.IsOnBoard) return;
                int lost = _wearer.EffectiveStats.MaxHp - _wearer.CurrentHp;
                int heal = GameMath.RoundToInt(lost * _percentPerSecond / 100f);
                if (heal > 0) _wearer.Heal(heal);
            }
        }

        /// <summary>按最大生命回复（魔抗大件）：每 periodSeconds 秒回复穿戴者最大生命的 X%。</summary>
        public sealed class MaxHpRegenHook : IBattleHook
        {
            private readonly Unit _wearer;
            private readonly float _percent;
            private readonly int _periodTicks;
            private int _tick;

            public int SourceId { get; }

            public MaxHpRegenHook(Unit wearer, float percent, int periodSeconds, int sourceId)
            {
                _wearer = wearer;
                _percent = percent;
                _periodTicks = periodSeconds * GameConst.TicksPerSecond;
                SourceId = sourceId;
            }

            public void OnBattleStart(BattleContext ctx) { }
            public void OnBeforeAttack(BattleContext ctx, ref AttackDraft draft) { }
            public void OnAfterAttack(BattleContext ctx, in AttackResult result) { }
            public void OnDamaged(BattleContext ctx, in DamageResult result) { }
            public void OnDeath(BattleContext ctx, in DeathInfo death) { }
            public void OnTickEnd(BattleContext ctx)
            {
                _tick++;
                if (_tick % _periodTicks != 0) return;
                if (!_wearer.IsAlive || !_wearer.IsOnBoard) return;
                int heal = GameMath.RoundToInt(_wearer.EffectiveStats.MaxHp * _percent / 100f);
                if (heal > 0) _wearer.Heal(heal);
            }
        }

        /// <summary>灼伤光环（物防大件）：每 periodSeconds 秒对穿戴者周围一圈（切比雪夫 ≤1，8 格）的
        /// 敌方单位造成其最大生命 X% 的伤害——真实伤害语义（TakeDamage 直伤，不打折不暴击）。</summary>
        public sealed class BurnAuraHook : IBattleHook
        {
            private readonly Unit _wearer;
            private readonly float _percent;
            private readonly int _periodTicks;
            private int _tick;
            private readonly List<Unit> _scan = new List<Unit>();

            public int SourceId { get; }

            public BurnAuraHook(Unit wearer, float percent, int periodSeconds, int sourceId)
            {
                _wearer = wearer;
                _percent = percent;
                _periodTicks = periodSeconds * GameConst.TicksPerSecond;
                SourceId = sourceId;
            }

            public void OnBattleStart(BattleContext ctx) { }
            public void OnBeforeAttack(BattleContext ctx, ref AttackDraft draft) { }
            public void OnAfterAttack(BattleContext ctx, in AttackResult result) { }
            public void OnDamaged(BattleContext ctx, in DamageResult result) { }
            public void OnDeath(BattleContext ctx, in DeathInfo death) { }
            public void OnTickEnd(BattleContext ctx)
            {
                _tick++;
                if (_tick % _periodTicks != 0) return;
                if (!_wearer.IsAlive || !_wearer.IsOnBoard) return;

                ctx.Board.CollectUnitsOnBoard(!_wearer.IsPlayerSide, _scan);
                for (int i = 0; i < _scan.Count; i++)
                {
                    Unit enemy = _scan[i];
                    if (!enemy.IsAlive) continue;
                    if (_wearer.Pos.ChebyshevTo(enemy.Pos) > 1) continue;   // 周围一圈 8 格
                    int dmg = GameMath.RoundToInt(enemy.EffectiveStats.MaxHp * _percent / 100f);
                    if (dmg > 0) enemy.TakeDamage(dmg, _wearer);
                }
                _scan.Clear();
            }
        }

        /// <summary>类型条件减伤（魔抗大件）：穿戴者受到指定类型伤害时降低 X%（写减伤乘区，钳 90% 由公式承担）。</summary>
        public sealed class TypeDamageReductionHook : IBattleHook
        {
            private readonly Unit _wearer;
            private readonly DamageType _type;
            private readonly float _percent;

            public int SourceId { get; }

            public TypeDamageReductionHook(Unit wearer, DamageType type, float percent, int sourceId)
            {
                _wearer = wearer;
                _type = type;
                _percent = percent;
                SourceId = sourceId;
            }

            public void OnBattleStart(BattleContext ctx) { }
            public void OnBeforeAttack(BattleContext ctx, ref AttackDraft draft)
            {
                if (draft.Target != _wearer || draft.Type != _type) return;
                draft.DamageReductionPercent += _percent;
            }
            public void OnAfterAttack(BattleContext ctx, in AttackResult result) { }
            public void OnDamaged(BattleContext ctx, in DamageResult result) { }
            public void OnDeath(BattleContext ctx, in DeathInfo death) { }
            public void OnTickEnd(BattleContext ctx) { }
        }

        /// <summary>组合钩子（一件装备带多个被动的组合器，如魔抗件 = 类型减伤 + 生命回复）：
        /// 顺序分发两个钩子；SourceId 取第一个的。</summary>
        public sealed class CompositeHook : IBattleHook
        {
            private readonly IBattleHook _a;
            private readonly IBattleHook _b;

            public int SourceId => _a.SourceId;

            public CompositeHook(IBattleHook first, IBattleHook second)
            {
                _a = first ?? throw new ArgumentNullException(nameof(first));
                _b = second ?? throw new ArgumentNullException(nameof(second));
            }

            public void OnBattleStart(BattleContext ctx) { _a.OnBattleStart(ctx); _b.OnBattleStart(ctx); }
            public void OnBeforeAttack(BattleContext ctx, ref AttackDraft draft) { _a.OnBeforeAttack(ctx, ref draft); _b.OnBeforeAttack(ctx, ref draft); }
            public void OnAfterAttack(BattleContext ctx, in AttackResult result) { _a.OnAfterAttack(ctx, in result); _b.OnAfterAttack(ctx, in result); }
            public void OnDamaged(BattleContext ctx, in DamageResult result) { _a.OnDamaged(ctx, in result); _b.OnDamaged(ctx, in result); }
            public void OnDeath(BattleContext ctx, in DeathInfo death) { _a.OnDeath(ctx, in death); _b.OnDeath(ctx, in death); }
            public void OnTickEnd(BattleContext ctx) { _a.OnTickEnd(ctx); _b.OnTickEnd(ctx); }
        }

        /// <summary>类型条件增伤（策略 4/5）：成员造成的指定类型伤害提高 X%（写增伤乘区）——成员制（全队形态）。</summary>
        public sealed class TypeDamageAmpHook : IBattleHook
        {
            private readonly DamageType _type;
            private readonly float _percent;
            private readonly HashSet<Unit> _members;

            public int SourceId { get; }

            public TypeDamageAmpHook(IReadOnlyList<Unit> members, DamageType type, float percent, int sourceId)
            {
                _members = members == null ? null : new HashSet<Unit>(members);
                _type = type;
                _percent = percent;
                SourceId = sourceId;
            }

            public void OnBattleStart(BattleContext ctx) { }
            public void OnBeforeAttack(BattleContext ctx, ref AttackDraft draft)
            {
                if (draft.Attacker == null || !draft.Attacker.IsAlive) return;
                if (draft.Type != _type) return;
                if (_members != null && !_members.Contains(draft.Attacker)) return;
                draft.DamageAmpPercent += _percent;
            }
            public void OnAfterAttack(BattleContext ctx, in AttackResult result) { }
            public void OnDamaged(BattleContext ctx, in DamageResult result) { }
            public void OnDeath(BattleContext ctx, in DeathInfo death) { }
            public void OnTickEnd(BattleContext ctx) { }
        }
    }
}
