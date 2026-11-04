using System;
using System.Collections.Generic;

namespace AutoChess.Game
{
    /// <summary>
    /// 固定 tick 战斗模拟器。没有真实时间概念：Tick() 被调一次 = 模拟时间前进
    /// GameConst.TickSeconds（1/30 秒）。表现层按真实时间慢慢调（看戏），
    /// 测试用 for 循环狂调（一秒几百局）——同一份代码两种人生。
    /// 一个 tick 内部的执行顺序恒定（5.4），测试焊死。
    ///
    /// 批次2（阶段3）：施法循环（阶段3-接口设计 4.3）——单位行动位最前判定
    /// "蓝满 + 有技能 + 目标在施法距离内" → 本行动位施法（替代普攻/移动、不充条）；
    /// 施法失败（无目标/超距）不扣蓝、走原普攻/移动逻辑。技能伤害走 DamageFormula 同一条乘区管线，
    /// 且吃出手前钩子的四个改伤乘区（与普攻共享）；OnBeforeAttack/OnDamaged/OnDeath 对施法照常触发，
    /// OnAfterAttack（普攻收尾：吸血/叠层）不对施法触发——暗影吸血/蛮兽叠层按词条口径是普攻专属。
    /// 回归红线：不传技能表（skills = null）或单位无技能时，与批次1 行为逐字节一致。
    ///
    /// 批次3：战斗钩子骨架（阶段2-接口设计 5.2）——六挂点 + 按来源注册注销。
    /// 回归红线：无钩子注册时与阶段1 行为逐字节一致（掷骰序列/事件流/TickCount/RollCount 不变）——
    /// 所有分发先查 _hooks.Count，空表零副作用；CombatRules 本批不动（转钩子实现是阶段3 的事）。
    ///
    /// 7c-2（技能系统 v2）：施法循环支持 8 种目标规则 + 形状遍历 + 纯 buff 分支 + 附带 Buff；
    /// BuffSystem 内建为战斗协作者（构造器可选参 BuffTable；null = 无 Buff 结算，直连测试的回归红线），
    /// 每 tick 第 6.5 步驱动 TickAll（顺序焊死：回蓝 6 → Buff 6.5 → 钩子 7 → 胜负 8）；
    /// 多目标逐个行主序结算、一次掷骰全目标共享暴击；施法附带 Buff 走内建 Buffs.Apply
    /// （来源章 = ModSourceId.SkillBuffApplierBase + 技能 id；批次4 起测试钩子自建 BuffSystem 实例
    /// 与内建账本是两本独立账，互不结算——既有测试形态零扰动）。
    /// </summary>
    public sealed class CombatSystem
    {
        private readonly BoardData _board;
        private readonly EventBus _bus;
        private readonly GameRandom _random;
        private readonly CombatRules _rules;
        private readonly SkillTable _skills;   // 批次2：null = 无技能表——蓝满也永不施法（测试/未注入配置的兜底）
        private readonly BuffSystem _buffs;     // 7c-2：战斗内 Buff 账本（null = 无 Buff 结算）；随本实例每场新生

        private readonly Dictionary<Unit, Unit> _lockedTarget = new Dictionary<Unit, Unit>(); // 目标锁定到死
        private readonly Dictionary<Unit, float> _attackBar = new Dictionary<Unit, float>();  // 攻击进度条
        private readonly Dictionary<Unit, float> _moveBar = new Dictionary<Unit, float>();     // 移动进度条
        // 拍照名单与索敌缓冲（M2：实例级复用，热路径零分配）
        private readonly List<Unit> _playerRoster = new List<Unit>();
        private readonly List<Unit> _enemyRoster = new List<Unit>();
        private readonly List<Unit> _targetScan = new List<Unit>();
        private readonly List<Unit> _castTargets = new List<Unit>();  // 7c-2：施法作用名单（行主序，零分配复用）

        // ── 批次3：钩子账本 ──
        private readonly List<IBattleHook> _hooks = new List<IBattleHook>();
        private readonly List<IBattleHook> _hookSnapshot = new List<IBattleHook>(); // 快照遍历缓冲（EventBus 同款纪律）
        private readonly BattleContext _context;   // 钩子视角的战场句柄（棋盘/随机/攻击条推进）

        private int _tickCount;
        private int _rollCount;
        private bool _ended;    // BattleEndedEvent 已发的执行印记（全程只发一次）
        private bool _started;  // OnBattleStart 已触发（首 tick 最前，一场只一次）

        public CombatSystem(BoardData board, EventBus bus, uint seed = 0u, CombatRules rules = null, SkillTable skills = null, BuffTable buffs = null)
        {
            _board = board ?? throw new ArgumentNullException(nameof(board));
            _bus = bus ?? throw new ArgumentNullException(nameof(bus));
            _random = new GameRandom(seed);   // 唯一随机源；种子注入（铁律4）
            _rules = rules ?? new CombatRules();
            _skills = skills;                 // null = 无技能表：蓝满也永不施法
            _buffs = buffs != null ? new BuffSystem(buffs, _bus) : null;   // null = 无 Buff 结算：技能附带 Buff 静默跳过
            _context = new BattleContext(this, _board, _random);
        }

        /// <summary>战斗是否已见分晓：任一方清空或到达 MaxTicks。查棋盘现算，无隐藏计数器（D5）。</summary>
        public bool IsOver => _ended
            || _board.CountOnBoard(true) == 0
            || _board.CountOnBoard(false) == 0
            || _tickCount >= GameConst.MaxTicks;

        /// <summary>敌方清空且我方还有人 ⇒ true；超时与玩家全灭均为 false（超时判负）。</summary>
        public bool PlayerWon => _board.CountOnBoard(false) == 0 && _board.CountOnBoard(true) > 0;

        public int TickCount => _tickCount;

        /// <summary>掷骰总次数。暴击率 0 的单位一次都不掷——随机序列消耗点与实际行为一一对应（D9）。</summary>
        public int RollCount => _rollCount;

        /// <summary>战斗内 Buff 账本（7c-2；构造时未注入 Buff 表则为 null）。只读出口——测试/战绩查层数用。</summary>
        public BuffSystem Buffs => _buffs;

        // ─────────────────────── 钩子注册与注销（批次3） ───────────────────────

        /// <summary>注册战斗钩子（触发序 = 注册序）。hook 为 null 抛异常。</summary>
        public void AddHook(IBattleHook hook)
        {
            if (hook == null) throw new ArgumentNullException(nameof(hook));
            _hooks.Add(hook);
        }

        /// <summary>按来源整批注销，返回移除数；sourceId=0 抛（同修饰器纪律：无来源不是"一个来源"）。</summary>
        public int RemoveHooksBySource(int sourceId)
        {
            if (sourceId == ModSourceId.None)
                throw new ArgumentException("SourceId=0 表示无来源，不是可卸载的来源段", nameof(sourceId));
            int removed = 0;
            for (int i = _hooks.Count - 1; i >= 0; i--)
            {
                if (_hooks[i].SourceId == sourceId)
                {
                    _hooks.RemoveAt(i);
                    removed++;
                }
            }
            return removed;
        }

        /// <summary>
        /// 攻击条确定性推进（#22④ 骨架口，经 BattleContext.PushAttackProgress 调用）：
        /// 只加进度不立即行动、封顶蓄满（bar ≤ 1）不产生攻击欠条、不引入随机。
        /// </summary>
        internal void PushAttackProgress(Unit unit, float fraction)
        {
            if (unit == null || !unit.IsAlive || fraction <= 0f) return;
            _attackBar.TryGetValue(unit, out float bar);
            bar = GameMath.Min(bar + fraction, 1f);
            _attackBar[unit] = bar;
        }

        /// <summary>推进一个模拟时间步（GameConst.TickSeconds = 1/30 秒）。</summary>
        public void Tick()
        {
            // 1. 收尾守卫：已收尾 → TickCount 不增、不再广播
            if (_ended) return;

            // 2.
            _tickCount++;

            // 3. ★战斗开始钩子：首 tick 最前（拍照/行动之前），一场只一次；
            //    空场战斗（构造即终局）从不 Tick，永不触发，一致于"战斗根本没发生"
            if (!_started)
            {
                _started = true;
                ApplyInitialMana();   // 7c-1 起手蓝：面板事实先于钩子入账（回蓝大件 +15——钳 MaxMana、无蓝条静默）
                if (_hooks.Count > 0) DispatchOnBattleStart();
            }

            // 4. 拍照：先玩家后敌方 = 全盘行主序快照（防止"先行动的单位挪进未扫区被扫两次"）
            _board.CollectUnitsOnBoard(true, _playerRoster);
            _board.CollectUnitsOnBoard(false, _enemyRoster);

            // 5. 逐个行动（名单里本 tick 早些时候阵亡的跳过）
            ActRoster(_playerRoster);
            ActRoster(_enemyRoster);

            // 6. 每 tick 回蓝记账（批次5.5；术士被动）
            TickManaRegen();

            // 6.5 ★战斗内 Buff 结算（7c-2 生产接线）：DoT → Hot → 计时递减 → 到期摘层——
            //     与回蓝同为"每 tick 常规内容"，恒在钩子与胜负结算之前；无 Buff 表时零行为
            _buffs?.TickAll();

            // 7. ★每 tick 结束钩子：属于"tick 的常规内容"——
            //    恒在胜负结算之前，BattleEndedEvent 依旧是本 tick 最后一条事件
            if (_hooks.Count > 0) DispatchOnTickEnd();

            // 8. 胜负结算（查棋盘）
            ResolveOutcome();
        }

        // ─────────────────────── 内部执行序 ───────────────────────

        /// <summary>7c-1 起手蓝：开战瞬间把面板 InitialMana 入账（AddMana 钳上限、无蓝条/0 静默）。
        /// 修饰器来源 = 回蓝大件（+15）等；一场只应用一次（_started 守卫内调用）。</summary>
        private void ApplyInitialMana()
        {
            _board.CollectUnitsOnBoard(true, _playerRoster);
            _board.CollectUnitsOnBoard(false, _enemyRoster);
            for (int i = 0; i < _playerRoster.Count; i++)
                _playerRoster[i].AddMana(GameMath.RoundToInt(_playerRoster[i].EffectiveStats.InitialMana));
            for (int i = 0; i < _enemyRoster.Count; i++)
                _enemyRoster[i].AddMana(GameMath.RoundToInt(_enemyRoster[i].EffectiveStats.InitialMana));
        }

        private void ActRoster(List<Unit> roster)
        {
            for (int i = 0; i < roster.Count; i++)
            {
                Unit u = roster[i];
                if (!u.IsAlive || !u.IsOnBoard) continue;   // 本 tick 早些时候阵亡/已清场 → 跳过

                // ★施法判定（批次2）：蓝满 + 有技能 + 目标在施法距离内 → 本行动位施法——
                //   替代本次普攻/移动（不充条）；失败（无目标/超距）不扣蓝，落回原逻辑
                if (TryPerformCast(u)) continue;

                Unit target = GetLockedTarget(u);
                if (target == null || !target.IsAlive)       // 目标锁定到死：死了才重新索敌
                    target = AcquireNearestEnemy(u);
                _lockedTarget[u] = target;

                if (target == null) continue;                // 无敌可索：本 tick 白过，不充条（也就不掷骰）

                if (u.CanAttack(target))
                {
                    // 够得着 → 充攻击条（除数是有效攻击间隔，面板已保证 > 0）。
                    // 注意读法：Dictionary 索引器对"缺失键的读"会抛 KeyNotFoundException，
                    // 只有 TryGetValue 才给默认值 0；写回（索引器赋值）才会自动补键。
                    _attackBar.TryGetValue(u, out float bar);
                    bar += GameConst.TickSeconds / u.EffectiveStats.AttackInterval;
                    _attackBar[u] = bar;
                    if (bar >= 1f)
                        PerformAttack(u, target);
                }
                else
                {
                    // 够不着 → 充移动条（读法同理）
                    _moveBar.TryGetValue(u, out float bar);
                    bar += GameConst.TickSeconds / u.EffectiveStats.MoveInterval;
                    _moveBar[u] = bar;
                    if (bar >= 1f)
                        PerformMove(u, target);
                }
            }
        }

        /// <summary>每 tick 给存活单位按面板回蓝（无蓝条/0回蓝的单位在 AddMana 内静默）。</summary>
        private void TickManaRegen()
        {
            TickRegenFor(_playerRoster);
            TickRegenFor(_enemyRoster);
        }

        private void TickRegenFor(List<Unit> roster)
        {
            for (int i = 0; i < roster.Count; i++)
            {
                Unit u = roster[i];
                if (!u.IsAlive || !u.IsOnBoard) continue;
                // 决策⑦（随步长 1/30 修订）：每步把"每秒回蓝"交给单位自己的整数点数账本，
                // 攒满 TicksPerSecond(30) 点 = 1 蓝——精确无浮点，回蓝节奏与步长解耦
                u.RegenMana(u.EffectiveStats.ManaRegenPerSecond);
            }
        }

        private Unit GetLockedTarget(Unit u)
        {
            return _lockedTarget.TryGetValue(u, out var t) ? t : null;
        }

        /// <summary>全盘行主序扫描，取切比雪夫距离最小者；并列取先扫到的（严格小于才替换）。</summary>
        private Unit AcquireNearestEnemy(Unit u)
        {
            Unit best = null;
            int bestDist = int.MaxValue;
            _board.CollectUnitsOnBoard(!u.IsPlayerSide, _targetScan);
            for (int i = 0; i < _targetScan.Count; i++)
            {
                int d = u.Pos.ChebyshevTo(_targetScan[i].Pos);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = _targetScan[i];
                }
            }
            return best;
        }

        private void PerformAttack(Unit attacker, Unit target)
        {
            // 掷骰契约（D9）：只发生在真正出手时。暴击率 0 不掷——不消耗随机序列。
            bool isCrit = false;
            float chance = attacker.EffectiveStats.CritChance;
            if (chance > 0f)
            {
                _rollCount++;
                isCrit = _random.RollPercent(chance);
            }

            // ★出手前钩子：唯一可改伤挂点——穿透/增伤/减伤写进 draft（暴击已掷定不改，D2-3）
            var draft = new AttackDraft { Attacker = attacker, Target = target, Type = DamageType.Physical, IsCrit = isCrit };
            if (_hooks.Count > 0) DispatchOnBeforeAttack(ref draft);

            // 时序契约：先出手（Attacked），后见血（Damaged/Died 在 TakeDamage 内）
            _bus.Publish(new UnitAttackedEvent(attacker.Id, target.Id, isCrit));

            int applied = attacker.Attack(target, isCrit, ToExtras(in draft));

            // 攻击条 -=1 提前结算（原在方法末尾）：OnDeath 钩子的攻击条推进（#22④ 暗影 6 档）
            // 必须读到"已扣减"的余量，否则推的进度会被满格 clamp 吃掉。
            // 条在本方法后续流程（回蓝/吸血/死亡处理）中不被读取——位置前移不改变阶段1 可观察行为。
            _attackBar[attacker] -= 1f;

            // ★出手后钩子（攻方视角：动作收尾）
            if (_hooks.Count > 0)
                DispatchOnAfterAttack(new AttackResult
                {
                    Attacker = attacker, Target = target, IsCrit = isCrit,
                    DamageApplied = applied, TargetDied = !target.IsAlive,
                });

            // ★受伤后钩子（守方视角：承受了这次伤害——致死也发，伤害确实发生了）
            if (_hooks.Count > 0)
                DispatchOnDamaged(new DamageResult
                {
                    Target = target, Source = attacker,
                    Damage = applied, CurrentHp = target.CurrentHp, Died = !target.IsAlive,
                });

            // 普攻命中回蓝（批次5.5，职业口径；无蓝条/0回蓝在 AddMana 内静默）
            // 记账顺序锁定：伤害落地 → 出手后/受伤后钩子 → 命中回蓝 → 吸血回血
            attacker.AddMana(attacker.EffectiveStats.ManaPerHit);

            // 吸血：对"实际造成的伤害"按规则百分比回复（仅玩家侧享受）
            if (_rules.PlayerLifestealPercent > 0f && attacker.IsPlayerSide && attacker.IsAlive)
            {
                int heal = GameMath.RoundToInt(applied * _rules.PlayerLifestealPercent / 100f);
                if (heal > 0) attacker.Heal(heal);
            }

            if (!target.IsAlive)
                HandleKill(attacker, target);
        }

        // ─────────────────────── 施法（批次2，阶段3-接口设计 4.3） ───────────────────────

        /// <summary>
        /// 行动位施法判定（7c-2 v2）：蓝满（CurrentMana == MaxMana，AddMana 已钳上限）+ 技能表里查得到技能
        /// + 按技能表 TargetRule 产生非空"作用名单"（门禁语义见 SkillTargetRule 注释）→ 施法并返回 true
        /// （本行动位被施法占用）。任一条件不满足 → false（不扣蓝，落回普攻/移动逻辑）。
        /// 目标选择独立扫描（与索敌同套行主序规则），不读不写普攻的目标锁定账本；
        /// 无随机（暴击掷骰在 PerformCast 内、有伤害段才掷）。
        /// </summary>
        private bool TryPerformCast(Unit u)
        {
            if (_skills == null) return false;
            int maxMana = u.EffectiveStats.MaxMana;
            if (maxMana <= 0 || u.CurrentMana < maxMana) return false;

            SkillDef skill = _skills.Get(u.SkillId);
            if (skill.Id == 0) return false;

            if (!CollectCastTargets(u, skill)) return false;   // 门禁不过：不施法不扣蓝（名单为空即失败）

            PerformCast(u, _castTargets, skill);
            return true;
        }

        /// <summary>
        /// 按目标规则产生施法作用名单（填 _castTargets，行主序；返回 false = 门禁不过，不改任何状态）。
        /// 门禁两类 + 全局前置的完整口径见 SkillTargetRule 注释（7c-2 拍板）。
        /// </summary>
        private bool CollectCastTargets(Unit caster, in SkillDef skill)
        {
            _castTargets.Clear();
            if (_board.CountOnBoard(!caster.IsPlayerSide) == 0) return false;   // 全局前置：敌方有人（清场后的余势行动位不施法）

            switch (skill.TargetRule)
            {
                case SkillTargetRule.NearestEnemy:
                {
                    Unit t = AcquireNearestEnemy(caster);
                    if (t == null || caster.Pos.ChebyshevTo(t.Pos) > skill.CastRange) return false;   // 超距：不施法不扣蓝
                    _castTargets.Add(t);
                    return true;
                }
                case SkillTargetRule.LowestHpEnemy:
                {
                    // 施法距离内筛生命最低（严格小于才替换——并列取行主序先扫到）
                    Unit best = null;
                    int bestHp = int.MaxValue;
                    _board.CollectUnitsOnBoard(!caster.IsPlayerSide, _targetScan);
                    for (int i = 0; i < _targetScan.Count; i++)
                    {
                        Unit t = _targetScan[i];
                        if (caster.Pos.ChebyshevTo(t.Pos) > skill.CastRange) continue;
                        if (t.CurrentHp < bestHp) { bestHp = t.CurrentHp; best = t; }
                    }
                    if (best == null) return false;
                    _castTargets.Add(best);
                    return true;
                }
                case SkillTargetRule.NearestEnemyShape:
                {
                    Unit anchor = AcquireNearestEnemy(caster);
                    if (anchor == null || caster.Pos.ChebyshevTo(anchor.Pos) > skill.CastRange) return false;   // 锚点超距
                    CollectUnitsInShape(!caster.IsPlayerSide, skill, anchor.Pos, caster.IsPlayerSide);
                    return _castTargets.Count > 0;   // 锚点必在形状中心 → 恒真；防御性保留
                }
                case SkillTargetRule.SelfShape:
                {
                    // 形状即门禁：形状内无敌人 = 不施法不扣蓝（CastRange 不消费）
                    CollectUnitsInShape(!caster.IsPlayerSide, skill, caster.Pos, caster.IsPlayerSide);
                    return _castTargets.Count > 0;
                }
                case SkillTargetRule.Self:
                    _castTargets.Add(caster);
                    return true;
                case SkillTargetRule.AllyHighestAttack:
                {
                    // 含自身（自己攻最高就 buff 自己）；严格大于才替换——并列取行主序先扫到
                    Unit best = null;
                    int bestAtk = int.MinValue;
                    _board.CollectUnitsOnBoard(caster.IsPlayerSide, _targetScan);
                    for (int i = 0; i < _targetScan.Count; i++)
                    {
                        int atk = _targetScan[i].EffectiveStats.AttackPower;
                        if (atk > bestAtk) { bestAtk = atk; best = _targetScan[i]; }
                    }
                    if (best == null) return false;   // 理论不可达（自己在场上），防御性保留
                    _castTargets.Add(best);
                    return true;
                }
                case SkillTargetRule.AllyLowestHpRatio:
                {
                    // 生命"比例"最低：交叉相乘比较（整数精确，不做浮点除法）；严格小于才替换——并列取先扫到
                    Unit best = null;
                    int bestHp = 0, bestMax = 1;
                    _board.CollectUnitsOnBoard(caster.IsPlayerSide, _targetScan);
                    for (int i = 0; i < _targetScan.Count; i++)
                    {
                        Unit t = _targetScan[i];
                        int max = GameMath.Max(1, t.EffectiveStats.MaxHp);
                        if (best == null) { best = t; bestHp = t.CurrentHp; bestMax = max; continue; }
                        if ((long)t.CurrentHp * bestMax < (long)bestHp * max)
                        { best = t; bestHp = t.CurrentHp; bestMax = max; }
                    }
                    if (best == null) return false;   // 理论不可达（自己在场上），防御性保留
                    _castTargets.Add(best);
                    return true;
                }
                case SkillTargetRule.AllAllies:
                {
                    _board.CollectUnitsOnBoard(caster.IsPlayerSide, _castTargets);   // 含自身，行主序
                    return _castTargets.Count > 0;   // 理论不可达 false，防御性保留
                }
                default:
                    return false;   // 未知规则 = 不施法（防御）
            }
        }

        /// <summary>收集形状内全部存活"敌方"单位（行主序；友方永不因形状被误伤——构造上只收对面）。</summary>
        private void CollectUnitsInShape(bool enemySide, in SkillDef skill, Pos anchor, bool casterIsPlayer)
        {
            _board.CollectUnitsOnBoard(enemySide, _targetScan);
            for (int i = 0; i < _targetScan.Count; i++)
                if (IsInShape(skill, anchor, _targetScan[i].Pos, casterIsPlayer))
                    _castTargets.Add(_targetScan[i]);
        }

        /// <summary>形状判定（纯函数；锚点必在自身形状内；HalfBoard = 施法者视角的敌方半场）。</summary>
        private static bool IsInShape(in SkillDef skill, Pos anchor, Pos p, bool casterIsPlayer)
        {
            switch (skill.Shape)
            {
                case SkillShape.Box:
                {
                    // 奇数维完全对称；偶数维向右/向下多占一格（确定性约定，见 SkillShape 注释）；
                    // 越界区间自然截断（棋盘外的格不存在的单位）
                    int minX = anchor.X - (skill.ShapeWidth - 1) / 2;
                    int maxX = minX + skill.ShapeWidth - 1;
                    int minY = anchor.Y - (skill.ShapeHeight - 1) / 2;
                    int maxY = minY + skill.ShapeHeight - 1;
                    return p.X >= minX && p.X <= maxX && p.Y >= minY && p.Y <= maxY;
                }
                case SkillShape.FullRow:
                    return p.Y == anchor.Y;
                case SkillShape.FullColumn:
                    return p.X == anchor.X;
                case SkillShape.FullCross:
                    return p.Y == anchor.Y || p.X == anchor.X;
                case SkillShape.HalfBoard:
                    return casterIsPlayer
                        ? p.X >= BoardData.Width - BoardData.PlayerHalfColumns
                        : p.X < BoardData.PlayerHalfColumns;
                default:
                    return false;   // Single 不会走到这里（形状规则校验已挡）
            }
        }

        /// <summary>
        /// 施法 v2（多目标 + 附带 Buff + 纯 buff 分支；名单已由 CollectCastTargets 产生，行主序）：
        /// ① 有伤害段且物理且暴击率>0 → 掷骰一次，全部目标共享暴击结果（RollCount 口径：一次施法至多一掷）
        /// ② 广播 SkillCastEvent（恰一条；TargetId = 名单首位 = 主目标——全部受作用者的可见变化
        ///    由各自的 Damaged/Healed/Mana 事件承载，表现层事件契约零扰动）
        /// ③ 逐目标：出手前钩子（每目标独立 draft）→ DamageFormula 同管线结算 → 受伤后钩子 → 击杀收尾
        /// ④ 附带 Buff：对名单中存活者 Buffs.Apply（被击杀者不挂——毒不上尸体；
        ///    无内建 BuffSystem 时静默跳过——伤害照常）
        /// ⑤ 清蓝（保留型 KeepManaAfterCast = true 不清；true = 每个行动位连续施法）。
        /// 纯 buff 技能（无伤害段）跳过 ①③：只 ②④⑤——无暴击掷骰、无伤害事件。
        /// 与普攻的差异（口径，测试焊死）：不回蓝（命中回蓝是普攻权益）、不触发 OnAfterAttack
        /// （普攻收尾钩子：吸血/叠层按词条口径是普攻专属）、不吃 CombatRules 普攻吸血。
        /// </summary>
        private void PerformCast(Unit caster, List<Unit> targets, SkillDef skill)
        {
            bool hasDamage = skill.HasDamageSegment;

            bool isCrit = false;
            if (hasDamage && SkillDamage.CanCrit(skill) && caster.EffectiveStats.CritChance > 0f)
            {
                _rollCount++;
                isCrit = _random.RollPercent(caster.EffectiveStats.CritChance);
            }

            // 时序契约：先出手（SkillCast），后见血（Damaged/Died 在 TakeDamage 内）
            _bus.Publish(new SkillCastEvent(caster.Id, skill.Id, targets[0].Id, isCrit));

            if (hasDamage)
            {
                for (int i = 0; i < targets.Count; i++)
                {
                    Unit target = targets[i];

                    // ★出手前钩子：技能与普攻共享唯一可改伤挂点（穿透/增伤/减伤；暴击已掷定不改，D2-3）
                    var draft = new AttackDraft { Attacker = caster, Target = target, Type = skill.DamageType, IsCrit = isCrit };
                    if (_hooks.Count > 0) DispatchOnBeforeAttack(ref draft);

                    int applied = CastDamage(caster, target, skill, isCrit, ToExtras(in draft));

                    // ★受伤后钩子（守方视角）：技能伤害同样是"承受了这次伤害"（致死也发）
                    if (_hooks.Count > 0)
                        DispatchOnDamaged(new DamageResult
                        {
                            Target = target, Source = caster,
                            Damage = applied, CurrentHp = target.CurrentHp, Died = !target.IsAlive,
                        });

                    if (!target.IsAlive)
                        HandleKill(caster, target);
                }
            }

            // 附带 Buff（7c-2）：名单中存活者挂层；来源章 = 600 + 技能 id（ModSourceId 系统段子段）
            if (skill.BuffId != 0 && _buffs != null)
            {
                for (int i = 0; i < targets.Count; i++)
                    _buffs.Apply(targets[i], new BuffApplier(skill.BuffId, ModSourceId.SkillBuffApplierBase + skill.Id));
            }

            // 施法后清零（保留型 KeepManaAfterCast = true 不清——v1 表全 false；true = 每个行动位连续施法）
            if (!skill.KeepManaAfterCast) caster.ClearMana();
        }

        /// <summary>技能伤害：SkillDamage 算基础值（表数据 + 系数段）→ DamageFormula 走与普攻相同的乘区管线。</summary>
        private int CastDamage(Unit caster, Unit target, SkillDef skill, bool isCrit, in AttackExtras extras)
        {
            DamageInput input = new DamageInput
            {
                Type = skill.DamageType,
                BaseDamage = SkillDamage.Compute(skill, caster.Star, caster.EffectiveStats),
                CanCrit = SkillDamage.CanCrit(skill),
                Attacker = caster.EffectiveStats,
                Defender = target.EffectiveStats,
                IsCrit = isCrit,
                FlatPen = extras.FlatPen,
                PercentPen = extras.PercentPen,
                DamageAmpPercent = extras.DamageAmpPercent,
                DamageReductionPercent = extras.DamageReductionPercent,
            };
            int final = DamageFormula.Compute(input);
            return target.TakeDamage(final, caster);
        }

        /// <summary>击杀收尾（普攻/施法共用）：死亡钩子 → 清出棋盘（尸体让格）→ 击杀回复（CombatRules，默认 0）。</summary>
        private void HandleKill(Unit attacker, Unit target)
        {
            // ★死亡时钩子：死者/击杀者/死亡格——尸体尚未清出、格位还在（钩子还能读到最后一格）
            if (_hooks.Count > 0)
                DispatchOnDeath(new DeathInfo { Dead = target, Killer = attacker, Cell = target.Pos });

            // 死者立即清出棋盘（尸体让格），然后触发击杀回复
            _board.Remove(target);

            if (_rules.PlayerKillHealPercent > 0f && attacker.IsPlayerSide && attacker.IsAlive)
            {
                int heal = GameMath.RoundToInt(attacker.EffectiveStats.MaxHp * _rules.PlayerKillHealPercent / 100f);
                if (heal > 0) attacker.Heal(heal);
            }
        }

        private void PerformMove(Unit u, Unit target)
        {
            int dx = target.Pos.X > u.Pos.X ? 1 : target.Pos.X < u.Pos.X ? -1 : 0;
            int dy = target.Pos.Y > u.Pos.Y ? 1 : target.Pos.Y < u.Pos.Y ? -1 : 0;

            // 候选顺序：斜向 → 横向 → 纵向；战斗走位可越境（mustStayInOwnHalf: false）
            // 全被挡住 → 不扣进度，下 tick 再试
            if (TryStep(u, dx, dy)) { }
            else if (TryStep(u, dx, 0)) { }
            else if (TryStep(u, 0, dy)) { }
            else return;

            _moveBar[u] -= 1f;
        }

        private bool TryStep(Unit u, int dx, int dy)
        {
            if (dx == 0 && dy == 0) return false;
            return _board.TryMove(u, new Pos(u.Pos.X + dx, u.Pos.Y + dy), mustStayInOwnHalf: false);
        }

        private void ResolveOutcome()
        {
            bool enemyWiped = _board.CountOnBoard(false) == 0;
            bool playerWiped = _board.CountOnBoard(true) == 0;
            bool timeout = _tickCount >= GameConst.MaxTicks;

            if (!enemyWiped && !playerWiped && !timeout) return;  // 还没分出胜负

            _ended = true;
            bool playerWon = enemyWiped && !playerWiped;           // 超时/玩家全灭 → 判负
            _bus.Publish(new BattleEndedEvent(playerWon, _tickCount)); // 全程只发一次
        }

        // ─────────────────────── 钩子分发（批次3） ───────────────────────

        /// <summary>快照遍历（EventBus 同款纪律）：迭代中注册/注销不影响当轮。</summary>
        private void BeginHookSnapshot()
        {
            _hookSnapshot.Clear();
            for (int i = 0; i < _hooks.Count; i++) _hookSnapshot.Add(_hooks[i]);
        }

        private void DispatchOnBattleStart()
        {
            BeginHookSnapshot();
            for (int i = 0; i < _hookSnapshot.Count; i++)
                _hookSnapshot[i].OnBattleStart(_context);
        }

        private void DispatchOnTickEnd()
        {
            BeginHookSnapshot();
            for (int i = 0; i < _hookSnapshot.Count; i++)
                _hookSnapshot[i].OnTickEnd(_context);
        }

        private void DispatchOnBeforeAttack(ref AttackDraft draft)
        {
            BeginHookSnapshot();
            for (int i = 0; i < _hookSnapshot.Count; i++)
                _hookSnapshot[i].OnBeforeAttack(_context, ref draft);
        }

        private void DispatchOnAfterAttack(in AttackResult result)
        {
            BeginHookSnapshot();
            for (int i = 0; i < _hookSnapshot.Count; i++)
                _hookSnapshot[i].OnAfterAttack(_context, in result);
        }

        private void DispatchOnDamaged(in DamageResult result)
        {
            BeginHookSnapshot();
            for (int i = 0; i < _hookSnapshot.Count; i++)
                _hookSnapshot[i].OnDamaged(_context, in result);
        }

        private void DispatchOnDeath(in DeathInfo death)
        {
            BeginHookSnapshot();
            for (int i = 0; i < _hookSnapshot.Count; i++)
                _hookSnapshot[i].OnDeath(_context, in death);
        }

        private static AttackExtras ToExtras(in AttackDraft draft) => new AttackExtras
        {
            FlatPen = draft.FlatPen,
            PercentPen = draft.PercentPen,
            DamageAmpPercent = draft.DamageAmpPercent,
            DamageReductionPercent = draft.DamageReductionPercent,
        };
    }
}
