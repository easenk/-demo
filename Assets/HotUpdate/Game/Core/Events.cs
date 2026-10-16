namespace AutoChess.Game
{
    // ─────────────────────────────────────────────────────
    // 阶段1 战斗事件 + 蓝条事件（批次5.5 扩容后共 11 条）。纪律（设计文档 2.6）：
    //  · 全部 struct —— 广播零装箱（EventBus 的 where T : struct 强制）
    //  · 只带"事实快照"与 int id，不带对象引用 —— 表现层拿 id 查自己的视图表
    //  · 名字一律过去时 —— 事件是"已发生的事实"，不是命令
    //  · 阶段2/3/5 的事件（商店/经济/羁绊/技能…）一律不预定义
    // ─────────────────────────────────────────────────────

    /// <summary>BoardData.TryPlace 成功后广播：某单位被放上了棋盘。</summary>
    public struct UnitPlacedEvent
    {
        public int UnitId;
        public Pos Cell;
        public bool IsPlayerSide;

        public UnitPlacedEvent(int unitId, Pos cell, bool isPlayerSide)
        {
            UnitId = unitId;
            Cell = cell;
            IsPlayerSide = isPlayerSide;
        }
    }

    /// <summary>BoardData.TryMove 成功后广播：某单位从一格挪到了另一格。</summary>
    public struct UnitMovedEvent
    {
        public int UnitId;
        public Pos FromCell;
        public Pos ToCell;

        public UnitMovedEvent(int unitId, Pos fromCell, Pos toCell)
        {
            UnitId = unitId;
            FromCell = fromCell;
            ToCell = toCell;
        }
    }

    /// <summary>BoardData.Remove 成功后广播：某单位被移出棋盘（撤离地点一并带走）。</summary>
    public struct UnitRemovedEvent
    {
        public int UnitId;
        public Pos Cell;

        public UnitRemovedEvent(int unitId, Pos cell)
        {
            UnitId = unitId;
            Cell = cell;
        }
    }

    /// <summary>CombatSystem 出手时广播——必须先于目标的受伤事件（先出手、后见血）。IsCrit 供表现层播暴击演出。</summary>
    public struct UnitAttackedEvent
    {
        public int AttackerId;
        public int TargetId;
        public bool IsCrit;

        public UnitAttackedEvent(int attackerId, int targetId, bool isCrit)
        {
            AttackerId = attackerId;
            TargetId = targetId;
            IsCrit = isCrit;
        }
    }

    /// <summary>Unit.TakeDamage 内广播：某单位实际掉了多少血。血条直接读 CurrentHp。</summary>
    public struct UnitDamagedEvent
    {
        public int UnitId;
        public int SourceUnitId;
        public int FinalDamage;
        public int CurrentHp;

        public UnitDamagedEvent(int unitId, int sourceUnitId, int finalDamage, int currentHp)
        {
            UnitId = unitId;
            SourceUnitId = sourceUnitId;
            FinalDamage = finalDamage;
            CurrentHp = currentHp;
        }
    }

    /// <summary>Unit 血量归零时广播：只陈述死亡，不涉及销毁（尸体清理由 CombatSystem Remove）。</summary>
    public struct UnitDiedEvent
    {
        public int UnitId;
        public Pos Cell;

        public UnitDiedEvent(int unitId, Pos cell)
        {
            UnitId = unitId;
            Cell = cell;
        }
    }

    /// <summary>Unit.Heal 内广播：某单位实际回了多少血。满血不发（只陈述真发生的事实）。</summary>
    public struct UnitHealedEvent
    {
        public int UnitId;
        public int Amount;
        public int CurrentHp;

        public UnitHealedEvent(int unitId, int amount, int currentHp)
        {
            UnitId = unitId;
            Amount = amount;
            CurrentHp = currentHp;
        }
    }

    /// <summary>修饰器挂摘导致 MaxHp 变化时广播：血条"格子变长"。</summary>
    public struct UnitMaxHpChangedEvent
    {
        public int UnitId;
        public int MaxHp;
        public int CurrentHp;

        public UnitMaxHpChangedEvent(int unitId, int maxHp, int currentHp)
        {
            UnitId = unitId;
            MaxHp = maxHp;
            CurrentHp = currentHp;
        }
    }

    /// <summary>备战期满血归位（阶段2 才会被调用；属 Unit 公开 API 的一部分，故先定义）。</summary>
    public struct UnitRestoredEvent
    {
        public int UnitId;
        public int CurrentHp;

        public UnitRestoredEvent(int unitId, int currentHp)
        {
            UnitId = unitId;
            CurrentHp = currentHp;
        }
    }

    /// <summary>CombatSystem 分出胜负的那一个 tick 广播，全程只发一次。</summary>
    public struct BattleEndedEvent
    {
        public bool PlayerWon;
        public int TotalTicks;

        public BattleEndedEvent(bool playerWon, int totalTicks)
        {
            PlayerWon = playerWon;
            TotalTicks = totalTicks;
        }
    }

    /// <summary>单位蓝量变化时广播（批次5.5：命中回蓝/每秒回蓝；阶段3 施法消耗也走它）。蓝条 UI 直接读。</summary>
    public struct ManaChangedEvent
    {
        public int UnitId;
        public int CurrentMana;
        public int MaxMana;

        public ManaChangedEvent(int unitId, int currentMana, int maxMana)
        {
            UnitId = unitId;
            CurrentMana = currentMana;
            MaxMana = maxMana;
        }
    }
}
