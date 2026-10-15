using System;
using System.Collections.Generic;

namespace AutoChess.Game
{
    /// <summary>
    /// 固定 tick 战斗模拟器。没有真实时间概念：Tick() 被调一次 = 模拟时间前进
    /// GameConst.TickSeconds（0.5 秒）。表现层按真实时间慢慢调（看戏），
    /// 测试用 for 循环狂调（一秒几百局）——同一份代码两种人生。
    /// 一个 tick 内部的执行顺序恒定（5.4），测试焊死。
    /// </summary>
    public sealed class CombatSystem
    {
        private readonly BoardData _board;
        private readonly EventBus _bus;
        private readonly GameRandom _random;
        private readonly CombatRules _rules;

        private readonly Dictionary<Unit, Unit> _lockedTarget = new Dictionary<Unit, Unit>(); // 目标锁定到死
        private readonly Dictionary<Unit, float> _attackBar = new Dictionary<Unit, float>();  // 攻击进度条
        private readonly Dictionary<Unit, float> _moveBar = new Dictionary<Unit, float>();     // 移动进度条
        // 拍照名单与索敌缓冲（M2：实例级复用，热路径零分配）
        private readonly List<Unit> _playerRoster = new List<Unit>();
        private readonly List<Unit> _enemyRoster = new List<Unit>();
        private readonly List<Unit> _targetScan = new List<Unit>();

        private int _tickCount;
        private int _rollCount;
        private bool _ended; // BattleEndedEvent 已发的执行印记（全程只发一次）

        public CombatSystem(BoardData board, EventBus bus, uint seed = 0u, CombatRules rules = null)
        {
            _board = board ?? throw new ArgumentNullException(nameof(board));
            _bus = bus ?? throw new ArgumentNullException(nameof(bus));
            _random = new GameRandom(seed);   // 唯一随机源；种子注入（铁律4）
            _rules = rules ?? new CombatRules();
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

        /// <summary>推进一个模拟时间步（GameConst.TickSeconds = 0.5 秒）。</summary>
        public void Tick()
        {
            // 1. 收尾守卫：已收尾 → TickCount 不增、不再广播
            if (_ended) return;

            // 2.
            _tickCount++;

            // 3. 拍照：先玩家后敌方 = 全盘行主序快照（防止"先行动的单位挪进未扫区被扫两次"）
            _board.CollectUnitsOnBoard(true, _playerRoster);
            _board.CollectUnitsOnBoard(false, _enemyRoster);

            // 4. 逐个行动（名单里本 tick 早些时候阵亡的跳过）
            ActRoster(_playerRoster);
            ActRoster(_enemyRoster);

            // 5. 胜负结算（查棋盘）
            ResolveOutcome();
        }

        // ─────────────────────── 内部执行序 ───────────────────────

        private void ActRoster(List<Unit> roster)
        {
            for (int i = 0; i < roster.Count; i++)
            {
                Unit u = roster[i];
                if (!u.IsAlive || !u.IsOnBoard) continue;   // 本 tick 早些时候阵亡/已清场 → 跳过

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

            // 时序契约：先出手（Attacked），后见血（Damaged/Died 在 TakeDamage 内）
            _bus.Publish(new UnitAttackedEvent(attacker.Id, target.Id, isCrit));

            int applied = attacker.Attack(target, isCrit);

            // 吸血：对"实际造成的伤害"按规则百分比回复（仅玩家侧享受）
            if (_rules.PlayerLifestealPercent > 0f && attacker.IsPlayerSide && attacker.IsAlive)
            {
                int heal = GameMath.RoundToInt(applied * _rules.PlayerLifestealPercent / 100f);
                if (heal > 0) attacker.Heal(heal);
            }

            if (!target.IsAlive)
            {
                // 死者立即清出棋盘（尸体让格），然后触发击杀回复
                _board.Remove(target);

                if (_rules.PlayerKillHealPercent > 0f && attacker.IsPlayerSide && attacker.IsAlive)
                {
                    int heal = GameMath.RoundToInt(attacker.EffectiveStats.MaxHp * _rules.PlayerKillHealPercent / 100f);
                    if (heal > 0) attacker.Heal(heal);
                }
            }

            // 条 -= 1：保留余量，防高攻速下进度漂移
            _attackBar[attacker] -= 1f;
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
    }
}