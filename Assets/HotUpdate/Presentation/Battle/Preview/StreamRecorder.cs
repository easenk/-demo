using System;
using AutoChess.Game;

namespace AutoChess.Presentation
{
    /// <summary>
    /// 捕获器（#23①②，批次A）：预演总线的订阅者——白名单 11 类事件逐条摊平进事件流，
    /// 带 tick 戳 + 序号。tick 读数经注入的 Func&lt;int&gt; 在捕获瞬间取
    /// （CombatSystem 在 tick 开始即递增计数、事件发生在递增之后——读即正确，
    /// 不依赖"调用方记得 +1"这类脆弱假设）。
    ///
    /// 零分配捕获：条目全部落 BattleEventStream 的预分配数组（扩容除外，见其注释）。
    /// 本类不做任何游戏语义——只做"事件 → 条目"的翻译，翻译表见 StreamItem 注释（唯一真源）。
    ///
    /// 订阅者口径（2026-11-02 拍板）：预演总线的订阅者 = 本捕获器 + GameSession 的晶矿记账
    /// （OnUnitDied——逻辑内部职责，不属表现层）；"捕获器是唯一订阅者"按"表现层视角唯一"理解。
    /// </summary>
    public sealed class StreamRecorder
    {
        private readonly BattleEventStream _stream;
        private Func<int> _tickSource;

        /// <summary>本 tick 内的死亡事件数——结构性时刻判据（#23②）：
        /// 预演循环每 tick 前经 BeginTick 清零、tick 后读它决定是否拍快照。</summary>
        public int DeathsThisTick { get; private set; }

        public StreamRecorder(BattleEventStream stream = null)
        {
            _stream = stream ?? new BattleEventStream();
        }

        /// <summary>捕获产物（只读视图由调用方持有）。</summary>
        public BattleEventStream Stream => _stream;

        /// <summary>tick 读数源注入（预演器在 StartBattle 成功后接 () => Combat.TickCount）。
        /// 必须在开始 Tick 之前设好。捕获路径每个事件调一次——纯读委托，无线程状态。</summary>
        public void SetTickSource(Func<int> tickSource)
        {
            _tickSource = tickSource ?? throw new ArgumentNullException(nameof(tickSource));
        }

        /// <summary>白名单 11 类逐一订阅。同一总线重复调用会双份订阅——
        /// 一个预演器一场战斗恰一次；返回的流条目 Seq 由捕获顺序自然递增。</summary>
        public void SubscribeAll(EventBus bus)
        {
            if (bus == null) throw new ArgumentNullException(nameof(bus));
            bus.Subscribe<UnitAttackedEvent>(OnAttacked);
            bus.Subscribe<SkillCastEvent>(OnSkillCast);
            bus.Subscribe<UnitDamagedEvent>(OnDamaged);
            bus.Subscribe<UnitDiedEvent>(OnDied);
            bus.Subscribe<UnitHealedEvent>(OnHealed);
            bus.Subscribe<ManaChangedEvent>(OnManaChanged);
            bus.Subscribe<UnitMovedEvent>(OnMoved);
            bus.Subscribe<UnitRemovedEvent>(OnRemoved);
            bus.Subscribe<UnitMaxHpChangedEvent>(OnMaxHpChanged);
            bus.Subscribe<BattleEndedEvent>(OnBattleEnded);
            bus.Subscribe<OreDroppedEvent>(OnOreDropped);
        }

        /// <summary>每 tick 前清死亡计数（结构性判据按 tick 作用域）。</summary>
        public void BeginTick()
        {
            DeathsThisTick = 0;
        }

        private int NowTick() => _tickSource != null ? _tickSource() : 0;

        private void OnAttacked(UnitAttackedEvent e)
        {
            _stream.Add(new StreamItem
            {
                Kind = StreamEventKind.UnitAttacked,
                Tick = NowTick(),
                Seq = _stream.Count,
                IdA = e.AttackerId,
                IdB = e.TargetId,
                V0 = e.IsCrit ? 1 : 0,
            });
        }

        private void OnSkillCast(SkillCastEvent e)
        {
            _stream.Add(new StreamItem
            {
                Kind = StreamEventKind.SkillCast,
                Tick = NowTick(),
                Seq = _stream.Count,
                IdA = e.CasterId,
                IdB = e.SkillId,
                IdC = e.TargetId,
                V0 = e.IsCrit ? 1 : 0,
            });
        }

        private void OnDamaged(UnitDamagedEvent e)
        {
            _stream.Add(new StreamItem
            {
                Kind = StreamEventKind.UnitDamaged,
                Tick = NowTick(),
                Seq = _stream.Count,
                IdA = e.UnitId,
                IdB = e.SourceUnitId,
                V0 = e.FinalDamage,
                V1 = e.CurrentHp,
            });
        }

        private void OnDied(UnitDiedEvent e)
        {
            DeathsThisTick++;
            _stream.Add(new StreamItem
            {
                Kind = StreamEventKind.UnitDied,
                Tick = NowTick(),
                Seq = _stream.Count,
                IdA = e.UnitId,
                CellX = (byte)e.Cell.X,
                CellY = (byte)e.Cell.Y,
            });
        }

        private void OnHealed(UnitHealedEvent e)
        {
            _stream.Add(new StreamItem
            {
                Kind = StreamEventKind.UnitHealed,
                Tick = NowTick(),
                Seq = _stream.Count,
                IdA = e.UnitId,
                V0 = e.Amount,
                V1 = e.CurrentHp,
            });
        }

        private void OnManaChanged(ManaChangedEvent e)
        {
            _stream.Add(new StreamItem
            {
                Kind = StreamEventKind.ManaChanged,
                Tick = NowTick(),
                Seq = _stream.Count,
                IdA = e.UnitId,
                V0 = e.CurrentMana,
                V1 = e.MaxMana,
            });
        }

        private void OnMoved(UnitMovedEvent e)
        {
            _stream.Add(new StreamItem
            {
                Kind = StreamEventKind.UnitMoved,
                Tick = NowTick(),
                Seq = _stream.Count,
                IdA = e.UnitId,
                CellX = (byte)e.FromCell.X,
                CellY = (byte)e.FromCell.Y,
                ToX = (byte)e.ToCell.X,
                ToY = (byte)e.ToCell.Y,
            });
        }

        private void OnRemoved(UnitRemovedEvent e)
        {
            _stream.Add(new StreamItem
            {
                Kind = StreamEventKind.UnitRemoved,
                Tick = NowTick(),
                Seq = _stream.Count,
                IdA = e.UnitId,
                CellX = (byte)e.Cell.X,
                CellY = (byte)e.Cell.Y,
            });
        }

        private void OnMaxHpChanged(UnitMaxHpChangedEvent e)
        {
            _stream.Add(new StreamItem
            {
                Kind = StreamEventKind.UnitMaxHpChanged,
                Tick = NowTick(),
                Seq = _stream.Count,
                IdA = e.UnitId,
                V0 = e.MaxHp,
                V1 = e.CurrentHp,
            });
        }

        private void OnBattleEnded(BattleEndedEvent e)
        {
            _stream.Add(new StreamItem
            {
                Kind = StreamEventKind.BattleEnded,
                Tick = NowTick(),
                Seq = _stream.Count,
                V0 = e.PlayerWon ? 1 : 0,
                V1 = e.TotalTicks,
            });
        }

        private void OnOreDropped(OreDroppedEvent e)
        {
            _stream.Add(new StreamItem
            {
                Kind = StreamEventKind.OreDropped,
                Tick = NowTick(),
                Seq = _stream.Count,
                IdA = e.OreId,
                V0 = e.Tier,
                CellX = (byte)e.Cell.X,
                CellY = (byte)e.Cell.Y,
            });
        }
    }
}
