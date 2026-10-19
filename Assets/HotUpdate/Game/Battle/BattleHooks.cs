using System;

namespace AutoChess.Game
{
    /// <summary>
    /// 战斗钩子：羁绊（#22）与将来的装备/Buff/策略共用的战斗期效果载体（批次3 骨架）。
    /// 一个接口六个挂点：按来源注册注销只需要一套账本；用不到的挂点写空方法（一行）。
    /// 不加 C# 默认接口实现——热更程序集里不趟 DIM 的兼容风险。
    ///
    /// 纪律：
    ///  · SourceId 自带来源（号段纪律：羁绊 1~999 / 策略 2000~2999），是注销句柄；
    ///  · 钩子不得引入新随机源——要随机只能用 ctx.Random（同一条种子流，否则种子失去意义）；
    ///  · 载荷里的单位可能已死——存活检查由钩子自担；
    ///  · 触发序 = 注册序；分发对钩子列表做快照遍历，迭代中的注册/注销不影响当轮（EventBus 同款纪律）。
    /// </summary>
    public interface IBattleHook
    {
        /// <summary>钩子归属来源（注销句柄；0=无来源，不可作为可卸载来源）。</summary>
        int SourceId { get; }

        /// <summary>战斗开始：首个 tick 的最前（拍照/行动之前），一场只触发一次。空场战斗（构造即终局）从不 Tick，永不触发。</summary>
        void OnBattleStart(BattleContext ctx);

        /// <summary>出手前——唯一可改伤挂点：穿透/增伤/减伤写进 draft（暴击已掷定不改，D2-3）。</summary>
        void OnBeforeAttack(BattleContext ctx, ref AttackDraft draft);

        /// <summary>出手后（攻方视角）：携带本次实际伤害（#22④——吸血/叠层类效果的数据源）。</summary>
        void OnAfterAttack(BattleContext ctx, in AttackResult result);

        /// <summary>受伤后（守方视角）：承受了这次伤害（致死也发——伤害确实发生了）。</summary>
        void OnDamaged(BattleContext ctx, in DamageResult result);

        /// <summary>死亡时（守方视角）：尸体尚未清出、格位还在；携带击杀者（#22④——暗影击杀触发的数据源）。</summary>
        void OnDeath(BattleContext ctx, in DeathInfo death);

        /// <summary>每 tick 结束：恒在胜负结算之前（BattleEndedEvent 依旧是本 tick 最后一条事件）。</summary>
        void OnTickEnd(BattleContext ctx);
    }

    /// <summary>
    /// 钩子视角的战场句柄：枚举场上单位、要随机、推进攻击条。
    /// 只暴露这三样——不给 EventBus（钩子发自定义事件会打乱既有事件序契约）、不给战斗内部账本。
    /// </summary>
    public sealed class BattleContext
    {
        /// <summary>枚举场上单位用（CollectUnitsOnBoard / CountOnBoard）。</summary>
        public BoardData Board { get; }

        /// <summary>钩子要随机只能用它（同一条种子流，禁止新随机源）。</summary>
        public GameRandom Random { get; }

        private readonly CombatSystem _owner;

        internal BattleContext(CombatSystem owner, BoardData board, GameRandom random)
        {
            _owner = owner;
            Board = board;
            Random = random;
        }

        /// <summary>
        /// 攻击条确定性推进（#22④ 暗影 6 档"击杀后攻击条立即推进 50%"的骨架口）：
        /// 只加进度不立即行动——被推条的单位在下个自己的行动位出手；
        /// 封顶蓄满（bar ≤ 1）不产生攻击欠条；不引入随机。
        /// </summary>
        public void PushAttackProgress(Unit unit, float fraction) => _owner.PushAttackProgress(unit, fraction);
    }

    /// <summary>出手前挂点的可改载荷（ref 传入）——钩子改的四个字段全部填进 DamageInput 对应乘区。</summary>
    public struct AttackDraft
    {
        public Unit Attacker;
        public Unit Target;
        public bool IsCrit;                  // 已掷定，钩子不改（D2-3——骨架只透传，CombatSystem 用自己的本地变量）
        public int FlatPen;                  // 固定穿透 → DamageInput.FlatPen
        public int PercentPen;               // 百分比穿透 → DamageInput.PercentPen
        public float DamageAmpPercent;       // 攻方增伤乘区 → DamageInput.DamageAmpPercent
        public float DamageReductionPercent; // 守方减伤乘区 → DamageInput.DamageReductionPercent（"受击减伤"类效果，如铁壁 6 档 -12%）
    }

    /// <summary>Unit.Attack 的出手附加参数（钩子骨架开通的四个乘区；两参重载 = 全 0，阶段1 行为不变）。</summary>
    public struct AttackExtras
    {
        public int FlatPen;
        public int PercentPen;
        public float DamageAmpPercent;
        public float DamageReductionPercent;
    }

    /// <summary>出手后挂点的只读载荷（攻方视角）。</summary>
    public struct AttackResult
    {
        public Unit Attacker;
        public Unit Target;
        public bool IsCrit;
        public int DamageApplied;   // 实际造成的伤害（溢出截断；吸血/叠层的账目基准——#22④）
        public bool TargetDied;
    }

    /// <summary>受伤后挂点的只读载荷（守方视角）。</summary>
    public struct DamageResult
    {
        public Unit Target;
        public Unit Source;
        public int Damage;         // 实际扣除的血量
        public int CurrentHp;
        public bool Died;
    }

    /// <summary>死亡时挂点的只读载荷。</summary>
    public struct DeathInfo
    {
        public Unit Dead;
        public Unit Killer;   // #22④：击杀者（暗影击杀触发用）
        public Pos Cell;      // 死者最后一格（尸体清出前）
    }
}
