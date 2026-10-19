namespace AutoChess.Game
{
    // ─────────────────────────────────────────────────────
    // 阶段1 战斗事件（10 条）+ 蓝条事件（批次5.5，1 条）+ 阶段2 事件（批次1 起）。
    // 纪律（设计文档 2.6）：
    //  · 全部 struct —— 广播零装箱（EventBus 的 where T : struct 强制）
    //  · 只带"事实快照"与 int id，不带对象引用 —— 表现层拿 id 查自己的视图表
    //  · 名字一律过去时 —— 事件是"已发生的事实"，不是命令
    //  · 未开工系统的事件不预定义 —— 阶段2 各系统开工时按批追加，阶段3/5 同理
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

    // ─────────────────────────────────────────────────────
    // 阶段2 事件（批次1：经济 4 条）
    // ─────────────────────────────────────────────────────

    /// <summary>Economy 金币变动：Delta 为正 = 收入、为负 = 支出。钱包 UI 直接读 Gold。</summary>
    public struct GoldChangedEvent
    {
        public int Gold;
        public int Delta;

        public GoldChangedEvent(int gold, int delta)
        {
            Gold = gold;
            Delta = delta;
        }
    }

    /// <summary>
    /// Economy 经验变动：XpProgress = 当前级内已攒（累计 - 本级门槛）、XpToNextLevel = 距下一级还差。
    /// 事件序契约：升级时 LevelUpEvent 逐级先发，最后一次性发本事件携带最终进度。
    /// </summary>
    public struct XpChangedEvent
    {
        public int Level;
        public int XpProgress;
        public int XpToNextLevel;

        public XpChangedEvent(int level, int xpProgress, int xpToNextLevel)
        {
            Level = level;
            XpProgress = xpProgress;
            XpToNextLevel = xpToNextLevel;
        }
    }

    /// <summary>Economy 升级：Level = 新等级（一次加经验连升多级时逐级各发一条）。</summary>
    public struct LevelUpEvent
    {
        public int Level;

        public LevelUpEvent(int level)
        {
            Level = level;
        }
    }

    /// <summary>
    /// 一场战斗的结算收入三笔分账（决策 #21①：结算收入 = 固定段位 + 利息 + 连胜，分开记账——
    /// 结算 UI 分行展示、测试逐笔断言）。Bill 内带记账后的当前连胜。
    /// </summary>
    public struct IncomeSettledEvent
    {
        public IncomeBill Bill;

        public IncomeSettledEvent(IncomeBill bill)
        {
            Bill = bill;
        }
    }

    // ─────────────────────────────────────────────────────
    // 阶段2 事件（批次2：商店 2 条）
    // ─────────────────────────────────────────────────────

    /// <summary>ShopSystem 上架完成：5 格快照（0 = 空格）。表现层按 CharId 查 CharacterTable 取显示数据。</summary>
    public struct ShopRolledEvent
    {
        public int[] CharIds;

        public ShopRolledEvent(int[] charIds)
        {
            CharIds = charIds;
        }
    }

    /// <summary>ShopSystem 买走一格：SlotIndex = 格号、CharId = 买到的角色、GoldLeft = 买后余额。</summary>
    public struct ShopBoughtEvent
    {
        public int SlotIndex;
        public int CharId;
        public int GoldLeft;

        public ShopBoughtEvent(int slotIndex, int charId, int goldLeft)
        {
            SlotIndex = slotIndex;
            CharId = charId;
            GoldLeft = goldLeft;
        }
    }

    // ─────────────────────────────────────────────────────
    // 阶段2 事件（批次4：羁绊 1 条）
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// SynergySystem 全量重算后广播：全部族的快照（逐族 Count/TierIndex）。
    /// 无变化的重算保持沉默（只陈述真发生的事实）；快照数组是拷贝，外部改不动账本。
    /// </summary>
    public struct SynergiesChangedEvent
    {
        public SynergyInfo[] Snapshot;

        public SynergiesChangedEvent(SynergyInfo[] snapshot)
        {
            Snapshot = snapshot;
        }
    }
}
