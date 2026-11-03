using System;

namespace AutoChess.Presentation
{
    /// <summary>
    /// 事件流条目类型（#23①②，批次A）——白名单 11 条 = 战斗期实际广播的事件全集
    /// （批内逐事件 grep 落死，"只多不少"保真）。
    /// 维护纪律：逻辑层将来新增"战斗期广播"的事件时，本枚举与 StreamRecorder 的订阅、
    /// StreamItem 的字段映射表必须同步扩——否则预演会静默漏掉该事件（4B1 规划器看不见）。
    /// 排除项及理由：UnitPlaced/UnitRestored 只发生在战前布置与备战期（遭遇生成在总线重定向之前，
    /// 与玩家侧摆位同构走会话总线）；经济/商店/羁绊/装备/策略事件全部属备战/结算期，
    /// 本来就不在战斗 tick 里发生。
    /// </summary>
    public enum StreamEventKind
    {
        /// <summary>UnitAttackedEvent：普攻出手（CombatSystem 广播）。</summary>
        UnitAttacked = 1,
        /// <summary>SkillCastEvent：施法出手（CombatSystem 广播；一场施法恰一条、TargetId=主目标）。</summary>
        SkillCast,
        /// <summary>UnitDamagedEvent：实际掉血（Unit 广播；普攻/技能/DoT 同走一条伤害管线）。</summary>
        UnitDamaged,
        /// <summary>UnitDiedEvent：血量归零（Unit 广播）。
        /// 注意：DoT 致死不伴随 UnitRemoved（逻辑层尸体不清格的现状，如实镜像——见批次A 交付报告挂账）。</summary>
        UnitDied,
        /// <summary>UnitHealedEvent：实际回血（Unit 广播；满血回复静默）。</summary>
        UnitHealed,
        /// <summary>ManaChangedEvent：蓝量变化（Unit 广播：命中回蓝/每秒回蓝/施法清零）。</summary>
        ManaChanged,
        /// <summary>UnitMovedEvent：战斗走位（BoardData 广播；可越境）。</summary>
        UnitMoved,
        /// <summary>UnitRemovedEvent：清出棋盘/尸体让格（BoardData 广播；攻击/技能击杀时发）。</summary>
        UnitRemoved,
        /// <summary>UnitMaxHpChangedEvent：上限变化（Unit 广播；战斗期由 Buff/钩子的 MaxHp 修饰触发）。</summary>
        UnitMaxHpChanged,
        /// <summary>BattleEndedEvent：分晓（CombatSystem 广播，全程恰一条；秒胜链不广播——战斗根本没发生）。</summary>
        BattleEnded,
        /// <summary>OreDroppedEvent：带矿敌人死亡掉矿（GameSession 记账广播——战斗期事件，
        /// 4U3"掉落弹出动画"的数据源，随播放出现在死亡格）。</summary>
        OreDropped,
    }

    /// <summary>
    /// 事件流条目（#23②）：白名单事件摊平成统一结构 + tick 戳 + 序号。
    /// 为什么摊平：白名单是 11 种异构 struct——零装箱落流的唯一实用形态就是统一条目结构
    /// （装箱/存 object 都违背零分配纪律）。字段语义随 Kind 变化，下表是唯一真源，
    /// 消费侧（4B1 规划器 / 4B2 播放器）按表解码：
    ///
    ///   Kind              IdA            IdB          IdC         V0          V1        格(X/Y)     终格(ToX/ToY)
    ///   UnitAttacked      攻击者         目标         -           是否暴击    -         -           -
    ///   SkillCast         施法者         技能id       主目标      是否暴击    -         -           -
    ///   UnitDamaged       受击者         伤害来源     -           实际伤害    受击后血  -           -
    ///   UnitHealed        回血者         -            -           回复量      回血后血  -           -
    ///   ManaChanged       蓝变者         -            -           当前蓝      蓝上限    -           -
    ///   UnitDied          死者           -            -           -           -         死亡格      -
    ///   UnitMoved         移动者         -            -           -           -         起点格      终点格
    ///   UnitRemoved       被清者         -            -           -           -         撤离格      -
    ///   UnitMaxHpChanged  单位           -            -           新上限      当前血    -           -
    ///   BattleEnded       -              -            -           是否获胜    总tick数  -           -
    ///   OreDropped       矿实例id       -            -           挡位        -         死亡格      -
    ///
    /// 布尔一律 0/1（V0 槽）；格子坐标用 byte——棋盘 8x6，X∈[0,7]、Y∈[0,5] 天然装得下
    /// （BoardData.Remove 后 Pos 保留最后位置，撤离格=死亡格）。条目约 40 字节：
    /// 典型局（数千条）约 100~300KB，连同快照落在 #23②"单场峰值 0.3MB"预算内
    /// （实测为准——批次A 基准报告给出分布；病态满员超时局会超预算，属已知边界）。
    ///
    /// Tick = 事件发生时的逻辑步数（CombatSystem 在 tick 开始即递增、事件发生在递增之后——1 基）。
    /// Seq = 全流序号（0 基，捕获先后）——与数组下标恒等，显式留字段防"流被重排"类 bug 静默。
    /// 手写 Equals（不走 ValueType 反射比较）：确定性验收逐条比对数千条，反射慢一个数量级。
    /// </summary>
    public struct StreamItem : IEquatable<StreamItem>
    {
        public StreamEventKind Kind;
        public int Tick;
        public int Seq;
        public int IdA;
        public int IdB;
        public int IdC;
        public int V0;
        public int V1;
        public byte CellX;
        public byte CellY;
        public byte ToX;
        public byte ToY;

        public bool Equals(StreamItem other) =>
            Kind == other.Kind && Tick == other.Tick && Seq == other.Seq
            && IdA == other.IdA && IdB == other.IdB && IdC == other.IdC
            && V0 == other.V0 && V1 == other.V1
            && CellX == other.CellX && CellY == other.CellY
            && ToX == other.ToX && ToY == other.ToY;
    }

    /// <summary>
    /// 事件流容器（#23②）：白名单条目的预分配数组 + 计数。
    /// 初始容量 8192 条（约 320KB）——写满倍增，捕获路径除扩容瞬间外零分配
    /// （扩容是罕见长战斗的一次性成本，GrowthCount 供基准报告观察）。
    /// Clear 只清计数保留容量 = "播完即释放"的语义（条目全是值类型，计数归零即引用断根；
    /// 容量留给复用）。事件流不落盘——长期留存仅战前快照（#19/阶段5，本批不做）。
    /// </summary>
    public sealed class BattleEventStream
    {
        private StreamItem[] _items;
        private int _count;
        private int _growCount;

        public BattleEventStream(int initialCapacity = 8192)
        {
            if (initialCapacity < 16) initialCapacity = 16;
            _items = new StreamItem[initialCapacity];
        }

        /// <summary>已捕获条数。</summary>
        public int Count => _count;

        /// <summary>当前容量（预分配 + 扩容结果）。</summary>
        public int Capacity => _items.Length;

        /// <summary>扩容次数（零分配纪律的观察口：典型局应为 0~1）。</summary>
        public int GrowCount => _growCount;

        public StreamItem this[int index] => _items[index];

        internal void Add(in StreamItem item)
        {
            if (_count == _items.Length)
            {
                Array.Resize(ref _items, _items.Length * 2);
                _growCount++;
            }
            _items[_count] = item;
            _count++;
        }

        /// <summary>清空计数（保留容量）——预演结束、播放排空后的释放语义；此后 Result 里的旧视图作废。</summary>
        public void Clear()
        {
            _count = 0;
        }
    }
}
