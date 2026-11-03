using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AutoChess.Game;

namespace AutoChess.Presentation
{
    /// <summary>
    /// 一场预演的交接包：胜负/步数/掷骰数 + 事件流与快照句柄。
    /// 秒胜链（构造即终局）：流空、快照空、TickCount=0、无 BattleEnded 条目——
    /// 播放侧按 Phase 已是 Settlement 直切结算（战斗根本没发生）。
    /// Release 之后本包内的流/快照视图作废（#23②"播完即释放"）。
    /// </summary>
    public sealed class BattlePreviewResult
    {
        public bool PlayerWon;
        public int TickCount;
        /// <summary>掷骰总次数——确定性断言的辅助读数（同种子两场必然相等）。</summary>
        public int RollCount;
        public BattleEventStream Stream;
        public BattleSnapshotSequence Snapshots;
    }

    /// <summary>
    /// 预演器（#23①，批次A）：整场预演的编排者——建独立战斗总线、捕获器订阅、开战、
    /// 逐 tick 驱动 CombatSystem 至 IsOver（MaxTicks=4500 步上限天然有界，无死循环风险）、
    /// 按结构性时刻拍关键帧。
    ///
    /// 用法（一个实例服务一场战斗，要多场就每场 new——便宜对象；Start 只许调一次，被门禁拒也算用过）：
    ///   主线程：Start(session)——会话状态变更（遭遇生成/种子抽取/总线重定向）发生在主线程；
    ///           返回 false = 开战被门禁拒（矿没捡完/策略没选完），预演不成立；
    ///   预演线程：RunToEnd() / RunToEndInBackground()——循环期间主线程不得触碰 GameSession
    ///           （Phase=Battle 下备战类公开方法本就纯拒绝，无竞态面——#23① 纪律）；
    ///   排空后（4E 闸门）：session.SettleBattle() 结算（归还总线）。
    ///
    /// 后台线程（#23① 手机主线程不卡帧）：RunToEndInBackground = Task.Run 包裹整场循环；
    /// "完成后回主线程交接"（Unity 侧续接上下文、开场演出同播）属 4B2 战台组合根的职责，
    /// 本批只交 Task。预演循环触碰的只有 CombatSystem/捕获器/快照器——纯 C#，无线程亲和 API。
    /// </summary>
    public sealed class BattlePreviewer
    {
        // 快照扫描缓冲（实例级复用，两份分开——CollectUnitsOnBoard 会先清空缓冲）
        private readonly List<Unit> _scanPlayers = new List<Unit>(24);
        private readonly List<Unit> _scanEnemies = new List<Unit>(24);
        private GameSession _session;
        private bool _startAttempted;
        private bool _completed;

        public EventBus BattleBus { get; } = new EventBus();
        public StreamRecorder Recorder { get; }
        public BattleSnapshotSequence Snapshots { get; } = new BattleSnapshotSequence();

        /// <param name="streamInitialCapacity">事件流初始条目容量（默认 8192 条 ≈ 288KB，覆盖绝大多数整场；
        /// 病态长战斗倍增扩容。测试用小容量验证扩容路径。）</param>
        public BattlePreviewer(int streamInitialCapacity = 8192)
        {
            Recorder = new StreamRecorder(new BattleEventStream(streamInitialCapacity));
        }

        /// <summary>
        /// 主线程开战入口：捕获器接 tick 源并订阅战斗总线 → session.StartBattle(BattleBus)。
        /// 订阅先于开战：StartBattle 本身不 tick（秒胜链也不发事件），任何战斗事件发生时
        /// 捕获器必已就位、顺序恒为"捕获器在前、晶矿记账在后"——与旧路径
        /// "测试收集器先于会话构造订阅"得到的 Died→OreDropped 相对序一致（对照验收的前提）。
        /// </summary>
        public bool Start(GameSession session)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (_startAttempted) throw new InvalidOperationException("一个 BattlePreviewer 只服务一场战斗（Start 只许一次，被门禁拒也算用过）");
            _startAttempted = true;

            GameSession s = session;
            Recorder.SetTickSource(() => s.Combat.TickCount);
            Recorder.SubscribeAll(BattleBus);

            if (!session.StartBattle(BattleBus)) return false;   // 门禁拒：预演不成立（实例作废，换新实例重试）
            _session = session;
            return true;
        }

        /// <summary>预演线程：整场驱动至 IsOver + 拍关键帧。
        /// 秒胜链（开战即终局）零循环、零条目、零帧。重复调用 = 编程错误，直接抛。</summary>
        public BattlePreviewResult RunToEnd()
        {
            if (_session == null) throw new InvalidOperationException("Start 成功之前不可预演");
            if (_completed) throw new InvalidOperationException("预演已完成——一个 BattlePreviewer 只服务一场战斗");
            _completed = true;

            CombatSystem combat = _session.Combat;
            if (combat.IsOver) return BuildResult();   // 秒胜链：战斗根本没发生（无事件无快照）

            CaptureKeyframe(0);                         // 开战时刻帧（结构性时刻）
            Recorder.BeginTick();
            while (!combat.IsOver)
            {
                combat.Tick();
                int t = combat.TickCount;
                if (Recorder.DeathsThisTick > 0 || (t % GameConst.TicksPerSecond) == 0)
                    CaptureKeyframe(t);                // 死亡 tick / 每 30 tick（1 模拟秒）
                Recorder.BeginTick();
            }
            CaptureKeyframe(combat.TickCount);          // 终局帧（同 tick 去重——死亡 tick 与终局重合时只留一帧）
            return BuildResult();
        }

        /// <summary>后台线程整场预演（#23①）。生产接线（4B2 战台组合根）在开场演出期间调它并续接主线程。</summary>
        public Task<BattlePreviewResult> RunToEndInBackground()
            => Task.Run(() => RunToEnd());

        /// <summary>释放（#23②"播完即释放"）：清事件流与快照（容量保留）；Result 里的旧视图即作废。</summary>
        public void Release()
        {
            Recorder.Stream.Clear();
            Snapshots.Clear();
        }

        // ── 内部 ──

        private void CaptureKeyframe(int tick)
        {
            if (Snapshots.Count > 0 && Snapshots[Snapshots.Count - 1].Tick == tick) return;   // 同 tick 去重

            _session.Board.CollectUnitsOnBoard(true, _scanPlayers);    // 先玩家后敌方 = 确定性序
            _session.Board.CollectUnitsOnBoard(false, _scanEnemies);
            var units = new SnapshotUnit[_scanPlayers.Count + _scanEnemies.Count];
            int n = 0;
            for (int i = 0; i < _scanPlayers.Count; i++)
            {
                Unit u = _scanPlayers[i];
                units[n++] = new SnapshotUnit(u.Id, (byte)u.Pos.X, (byte)u.Pos.Y, u.CurrentHp, u.CurrentMana, u.IsAlive, u.IsPlayerSide);
            }
            for (int i = 0; i < _scanEnemies.Count; i++)
            {
                Unit u = _scanEnemies[i];
                units[n++] = new SnapshotUnit(u.Id, (byte)u.Pos.X, (byte)u.Pos.Y, u.CurrentHp, u.CurrentMana, u.IsAlive, u.IsPlayerSide);
            }
            Snapshots.Add(new BattleSnapshot(tick, units));
        }

        private BattlePreviewResult BuildResult()
        {
            CombatSystem combat = _session.Combat;
            return new BattlePreviewResult
            {
                PlayerWon = combat.PlayerWon,
                TickCount = combat.TickCount,
                RollCount = combat.RollCount,
                Stream = Recorder.Stream,
                Snapshots = Snapshots,
            };
        }
    }
}
