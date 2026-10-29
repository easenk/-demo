using System;
using System.Collections.Generic;

namespace AutoChess.Game
{
    /// <summary>
    /// 逻辑单位：有身份与可变状态 → class（引用语义）。Id 是它在事件里的唯一指代。
    /// 位置的唯一写者是 BoardData（internal set 把纪律焊进程序集边界）；
    /// 血量只有本类能改。修饰器账本 + EffectiveStats 现算（4.3/4.4）。
    /// </summary>
    public sealed class Unit
    {
        // 蓝条扩容后=13。注意：N对N 反射测试只盯"枚举↔字段"两侧，这个常量靠注释盯——
        // 将来新增 StatKind 时记得同步改这里，否则 EffectiveStats 草稿纸越界。
        private const int KindCount = (int)StatKind.ManaRegenPerSecond + 1;

        private readonly EventBus _bus;
        private readonly UnitStats _baseStats;
        private readonly List<StatModifier> _modifiers = new List<StatModifier>();
        // 现算草稿纸（实例级复用，不逐次分配）；EffectiveStats 内部独占，无重入
        private readonly float[] _flatScratch = new float[KindCount];
        private readonly float[] _pctScratch = new float[KindCount];

        private int _maxHp; // 当前生效上限（与 EffectiveStats.MaxHp 同步）
        private int _manaCredit; // 回蓝整数点数账本（批次5.6）：每步攒"回蓝值"点，攒满 TicksPerSecond 点换 1 蓝——避免浮点每步取整吞掉小数回蓝

        // ── 身份（出生定死）──
        public int Id { get; }
        public int CharId { get; }             // 配置表引用，表现层靠它找 sprite/动画
        public bool IsPlayerSide { get; }
        public int Star { get; }
        public int SkillId { get; }            // 技能表引用（批次2；0 = 无技能——"3 费+才有技能"是数据纪律，不写 if(cost)）
        public UnitStats Stats => _baseStats;  // 出厂面板（已按星级缩放），永不修改

        // ── 位置：唯一写者是 BoardData ──
        public Pos Pos { get; internal set; }
        public bool IsOnBoard { get; internal set; }

        // ── 战斗状态：只有本类能改 ──
        public int  CurrentHp { get; private set; }
        public bool IsAlive => CurrentHp > 0;

        // ── 蓝条状态（批次5.5）：出生 0（起手蓝决策）；MaxMana=0 的单位连账本都不建 ──
        public int CurrentMana { get; private set; }

        // ── 装备槽（批次3，决策 #12④）：每单位 3 槽，散件/大件同占槽；槽内实例的移动权在 GameSession ──
        public const int EquipmentSlotCount = 3;

        private readonly EquipmentInstance[] _equipmentSlots = new EquipmentInstance[EquipmentSlotCount];

        /// <summary>装备槽只读视图（null = 空槽）。穿戴/卸下/升级经 GameSession 门禁方法。</summary>
        public IReadOnlyList<EquipmentInstance> EquipmentSlots => _equipmentSlots;

        /// <summary>槽位数组内部写入口（internal = 同程序集 GameSession 专用；测试走 Session 门禁方法）。</summary>
        internal EquipmentInstance[] EquipmentSlotsInternal => _equipmentSlots;

        public Unit(int id, int charId, bool isPlayerSide, UnitStats stats, EventBus bus, int star = 1, int skillId = 0)
        {
            if (stats.MaxHp < 1)
                throw new ArgumentOutOfRangeException(nameof(stats), "MaxHp 必须 ≥ 1");
            if (stats.AttackInterval <= 0f || stats.MoveInterval <= 0f)
                throw new ArgumentOutOfRangeException(nameof(stats), "间隔是 CombatSystem 的除数，必须 > 0");

            Id = id;
            CharId = charId;
            IsPlayerSide = isPlayerSide;
            Star = star;
            SkillId = skillId;
            _baseStats = stats;
            _bus = bus ?? throw new ArgumentNullException(nameof(bus));
            _maxHp = stats.MaxHp;
            CurrentHp = stats.MaxHp;
        }

        // ── 修饰器账本（4.4）──

        public IReadOnlyList<StatModifier> Modifiers => _modifiers;

        /// <summary>派生面板：每次现算不缓存（没有缓存失效问题）。结算顺序见 4.3。</summary>
        public UnitStats EffectiveStats
        {
            get
            {
                // ① 单趟扫描分桶：百分比之间相加不叠乘（+50% 与 +50% ⇒ ×2，不是 ×2.25）
                Array.Clear(_flatScratch, 0, KindCount);
                Array.Clear(_pctScratch, 0, KindCount);
                for (int i = 0; i < _modifiers.Count; i++)
                {
                    StatModifier m = _modifiers[i];
                    int k = (int)m.Kind;
                    if (m.Op == StatOp.Flat) _flatScratch[k] += m.Value;
                    else _pctScratch[k] += m.Value;
                }

                // ②(base+flat)×(1+pct/100) → ③取整一次（D2）→ ④钳制
                UnitStats s = _baseStats;
                s.MaxHp = ScaledInt(StatKind.MaxHp, _baseStats.MaxHp, 1);            // 上限至少 1
                s.AttackPower = ScaledInt(StatKind.AttackPower, _baseStats.AttackPower, 0);
                s.Armor = ScaledInt(StatKind.Armor, _baseStats.Armor, int.MinValue); // 抗性可为负：不钳下限
                s.MagicResist = ScaledInt(StatKind.MagicResist, _baseStats.MagicResist, int.MinValue);
                s.AbilityPower = ScaledInt(StatKind.AbilityPower, _baseStats.AbilityPower, 0);            // 法强 ≥ 0
                s.AttackRange = ScaledInt(StatKind.AttackRange, _baseStats.AttackRange, 0);
                s.AttackInterval = ScaledInterval(StatKind.AttackInterval, _baseStats.AttackInterval);      // ≥ 0.1
                s.MoveInterval = ScaledInterval(StatKind.MoveInterval, _baseStats.MoveInterval);
                s.CritChance = GameMath.Min(100, ScaledInt(StatKind.CritChance, _baseStats.CritChance, 0)); // ∈ [0,100]
                s.CritDamage = ScaledInt(StatKind.CritDamage, _baseStats.CritDamage, 0);
                s.MaxMana = ScaledInt(StatKind.MaxMana, _baseStats.MaxMana, 0);            // 蓝条 ≥ 0（0 = 无蓝条单位）
                s.ManaPerHit = ScaledInt(StatKind.ManaPerHit, _baseStats.ManaPerHit, 0);   // 回蓝 ≥ 0
                s.ManaRegenPerSecond = ScaledInt(StatKind.ManaRegenPerSecond, _baseStats.ManaRegenPerSecond, 0);
                return s;
            }
        }

        public void AddModifier(StatModifier m)
        {
            _modifiers.Add(m);
            ApplyMaxHpDelta();
        }

        /// <summary>按值删第一条；账本对称（加得上的才摘得掉）→ 没有则 false。</summary>
        public bool RemoveModifier(StatModifier m)
        {
            if (_modifiers.Remove(m))
            {
                ApplyMaxHpDelta();
                return true;
            }
            return false;
        }

        /// <summary>整批卸载某来源的全部修饰器，返回删除条数。0=无来源不是"一个来源"，禁用。</summary>
        public int RemoveModifiersBySource(int sourceId)
        {
            if (sourceId == ModSourceId.None)
                throw new ArgumentException("SourceId=0 表示无来源，不是可卸载的来源段", nameof(sourceId));

            int removed = 0;
            for (int i = _modifiers.Count - 1; i >= 0; i--) // 倒序移除安全
            {
                if (_modifiers[i].SourceId == sourceId)
                {
                    _modifiers.RemoveAt(i);
                    removed++;
                }
            }
            if (removed > 0) ApplyMaxHpDelta();
            return removed;
        }

        // ── 行为 ──

        /// <summary>null / 自己已死 / 目标已死 / 超出切比雪夫射程 → false。</summary>
        public bool CanAttack(Unit target)
        {
            if (target == null || !IsAlive || !target.IsAlive) return false;
            return Pos.ChebyshevTo(target.Pos) <= EffectiveStats.AttackRange;
        }

        /// <summary>普攻＝物理伤害（金铲铲口径）；暴击由 CombatSystem 掷骰后传入（随机源单点化）。</summary>
        public int Attack(Unit target, bool isCrit) => Attack(target, isCrit, default);

        /// <summary>
        /// 普攻三参重载（批次3）：extras 由 CombatSystem 的出手前钩子（OnBeforeAttack）填好后传入——
        /// 穿透/增伤/减伤四个乘区从阶段1 的恒 0 开通为钩子可填。
        /// 两参版本委托本重载且 extras 全 0——阶段1 行为（含全部测试）逐字节不变。
        /// </summary>
        public int Attack(Unit target, bool isCrit, in AttackExtras extras)
        {
            DamageInput input = new DamageInput
            {
                Type = DamageType.Physical,        // 普攻恒物理
                BaseDamage = EffectiveStats.AttackPower,
                CanCrit = true,                    // 普攻恒可暴击
                Attacker = EffectiveStats,
                Defender = target.EffectiveStats,
                IsCrit = isCrit,
                FlatPen = extras.FlatPen,
                PercentPen = extras.PercentPen,
                DamageAmpPercent = extras.DamageAmpPercent,
                DamageReductionPercent = extras.DamageReductionPercent,
            };
            int final = DamageFormula.Compute(input);
            return target.TakeDamage(final, this);
        }

        /// <summary>
        /// 只管扣血/广播/死亡：收到的必须是"已结算的最终伤害"（公式在 DamageFormula，D7）。
        /// ≤0 或已死 → 返回 0 且不广播（只陈述真发生的事实）；
        /// 溢出不记实际伤害（吸血阶段不吃溢出）。
        /// </summary>
        public int TakeDamage(int finalDamage, Unit attacker)
        {
            if (finalDamage <= 0 || !IsAlive) return 0;

            int applied = finalDamage < CurrentHp ? finalDamage : CurrentHp;
            CurrentHp -= applied;
            _bus.Publish(new UnitDamagedEvent(Id, attacker == null ? 0 : attacker.Id, applied, CurrentHp));
            if (CurrentHp == 0)
                _bus.Publish(new UnitDiedEvent(Id, Pos)); // 只陈述死亡，不涉及销毁
            return applied;
        }

        /// <summary>战斗内增量回复：满血回复 0 且不广播；不复活死人。</summary>
        public int Heal(int amount)
        {
            if (amount <= 0 || !IsAlive) return 0;
            int room = _maxHp - CurrentHp;
            if (room <= 0) return 0;
            int applied = amount < room ? amount : room;
            CurrentHp += applied;
            _bus.Publish(new UnitHealedEvent(Id, applied, CurrentHp));
            return applied;
        }

        /// <summary>备战期满血归位（阶段2 调用；战斗切换时顺带清蓝——蓝是战斗内状态，不跨战斗携带）。</summary>
        public void RestoreFullHp()
        {
            CurrentHp = _maxHp;
            CurrentMana = 0;
            _bus.Publish(new UnitRestoredEvent(Id, CurrentHp));
        }

        /// <summary>
        /// 回蓝唯一入口（批次5.5：命中回蓝/每秒回蓝都走它；阶段3 的受击回蓝/施法消耗也归这里）。
        /// Heal 的镜像契约：无蓝条(MaxMana=0)静默、增量 ≤0 静默、已满静默——只陈述真发生的事实。
        /// </summary>
        public void AddMana(int amount)
        {
            if (amount <= 0) return;              // 负/零增量：静默（面板钳制 ≥0 的运行时镜像）
            int max = EffectiveStats.MaxMana;
            if (max <= 0) return;                 // 无蓝条单位：连账本都不建
            int room = max - CurrentMana;
            if (room <= 0) return;               // 已满：不重复陈述
            int applied = amount < room ? amount : room;
            CurrentMana += applied;
            _bus.Publish(new ManaChangedEvent(Id, CurrentMana, max));
        }

        /// <summary>
        /// 每步被动回蓝入口（CombatSystem 每步调用一次，传入当前有效回蓝值）。
        /// 为什么用整数点数账本而不是浮点累加：步长 1/30 秒时"每秒回2蓝"=每步 0.0666 蓝，
        /// 浮点四舍五入会把它静默吞成 0；整数点数账本（每步 +回蓝值点、攒满 30 点换 1 蓝）
        /// 换算精确、确定性无忧。蓝已满时点数照常消耗（蓝满不涨，与金铲铲口径一致）。
        /// </summary>
        public void RegenMana(int regenPerSecond)
        {
            if (regenPerSecond <= 0) return;
            _manaCredit += regenPerSecond;
            while (_manaCredit >= GameConst.TicksPerSecond)
            {
                _manaCredit -= GameConst.TicksPerSecond;
                AddMana(1);
            }
        }

        /// <summary>
        /// 施法后清蓝（批次2）：施法消耗 = 蓝条上限（MaxMana 即施法阈值，2026-10-16 拍板），施法后清零。
        /// 清零也是"真发生的事实"→ 广播（供蓝条 UI 归零）；无蓝条 / 已是 0 静默。
        /// 保留型技能（SkillDef.KeepManaAfterCast = true）不调它——v1 表全 false。
        /// </summary>
        public void ClearMana()
        {
            int max = EffectiveStats.MaxMana;
            if (max <= 0) return;
            if (CurrentMana == 0) return;
            CurrentMana = 0;
            _bus.Publish(new ManaChangedEvent(Id, 0, max));
        }

        // ── 内部 ──

        /// <summary>整数维：(base+flat)×(1+pct/100) → D2 取整 → 钳下限（min 传 int.MinValue 表示不钳）。</summary>
        private int ScaledInt(StatKind kind, int baseValue, int min)
        {
            int k = (int)kind;
            float v = (baseValue + _flatScratch[k]) * (1f + _pctScratch[k] / 100f);
            return GameMath.Max(min, GameMath.RoundToInt(v));
        }

        /// <summary>间隔维：同公式不取整，只钳 0.1 下限（它是 CombatSystem 的除数，永不为 0）。</summary>
        private float ScaledInterval(StatKind kind, float baseValue)
        {
            int k = (int)kind;
            float v = (baseValue + _flatScratch[k]) * (1f + _pctScratch[k] / 100f);
            return GameMath.Max(0.1f, v);
        }

        /// <summary>MaxHp 上限变化联动（4.4）：变大加血 / 收缩钳制 / 死亡不回血 / 真变才广播。</summary>
        private void ApplyMaxHpDelta()
        {
            int newMax = EffectiveStats.MaxHp;
            if (newMax == _maxHp) return;

            if (CurrentHp > 0)
            {
                if (newMax > _maxHp)
                    CurrentHp += newMax - _maxHp;   // 变大：等量增加（满血买血立即受益）
                else if (CurrentHp > newMax)
                    CurrentHp = newMax;             // 收缩：钳进新上限（不是按差值减！）
                if (CurrentHp < 1) CurrentHp = 1;   // 修饰器不杀人
            }
            // 已死亡（CurrentHp=0）保持 0：修饰器不复活
            _maxHp = newMax;
            _bus.Publish(new UnitMaxHpChangedEvent(Id, newMax, CurrentHp));
        }
    }
}
