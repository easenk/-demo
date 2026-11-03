using System;
using System.Collections.Generic;

namespace AutoChess.Presentation
{
    /// <summary>
    /// 时间轴节点种类（#23③，批次B1）——与白名单事件流一一对应：每条 StreamItem 恰产出一个节点。
    /// Duration > 0 = 动作节点（占单位的时间轴：Move/Attack/Cast/Death）；
    /// Duration = 0 = 交付节点（瞬间兑现：数值直设/格位让渡/终局标记）。
    /// </summary>
    public enum PlannedNodeKind
    {
        /// <summary>移动补间（UnitMoved）：CellX/Y=起点格，ToX/Y=终点格。</summary>
        Move = 1,
        /// <summary>普攻出手（UnitAttacked）。</summary>
        Attack,
        /// <summary>施法出手（SkillCast）。</summary>
        Cast,
        /// <summary>伤害交付（UnitDamaged）：Start=命中时刻。</summary>
        Damage,
        /// <summary>回血交付（UnitHealed）。</summary>
        Heal,
        /// <summary>蓝量交付（ManaChanged）。</summary>
        Mana,
        /// <summary>上限交付（UnitMaxHpChanged）。</summary>
        MaxHp,
        /// <summary>倒地（UnitDied）：Start=致死伤害的交付时刻。</summary>
        Death,
        /// <summary>让格（UnitRemoved）：Start=该格的释放时刻（=倒地完成）。</summary>
        Remove,
        /// <summary>掉矿（OreDropped）：Start 链到死亡格的倒地开始之后。</summary>
        OreDrop,
        /// <summary>终局（BattleEnded）：Start=逻辑终局时刻。</summary>
        BattleEnd,
    }

    /// <summary>
    /// 时间轴节点（#23③）：动作节点 + 交付时刻。字段语义随 Kind 变化，下表是唯一真源
    /// （形态照抄 StreamItem 的映射表；消费侧 = 4B2 通用播放器按表解码，规划器与播放器
    /// 读同一份时刻语义——#23⑥"同一份数据、两边各算各的"的规划侧落点）：
    ///
    ///   Kind        UnitId        TargetId     SkillId    V0              V1           格(CellX/Y)   终格(ToX/ToY)   Duration
    ///   Move        移动者        0            0          0               0            起点格        终点格         移动补间时长
    ///   Attack      攻击者        目标         0          是否暴击        0            0             0              出手动画时长
    ///   Cast        施法者        主目标       技能id     是否暴击        0            0             0              施法动画时长
    ///   Damage      受击者        伤害来源     0          实际伤害        受击后血      0             0              0
    ///   Heal        回血者        0            0          回复量          回血后血      0             0              0
    ///   Mana        蓝变者        0            0          当前蓝          蓝上限        0             0              0
    ///   MaxHp       单位          0            0          新上限          当前血        0             0              0
    ///   Death       死者          0            0          0               0            死亡格        0              倒地时长
    ///   Remove      被清者        0            0          0               0            撤离格        0              0（Start=释放时刻）
    ///   OreDrop     矿实例id      0            0          挡位            0            死亡格        0              0
    ///   BattleEnd   0             0            0          是否获胜        总tick数      0             0              0
    ///
    /// Seq = 源流条目序号（时间轴条数恒等于流条数，逐条可溯源——排错与验收的锚点）；
    /// Tick = 源逻辑 tick（调试对照）。Start = 表现秒（动作节点=开拍时刻，交付节点=兑现时刻）。
    /// 手写 Equals（不走 ValueType 反射比较）：确定性验收逐条比对数千节点，反射慢一个数量级。
    /// </summary>
    public struct PlannedNode : IEquatable<PlannedNode>
    {
        public PlannedNodeKind Kind;
        public int UnitId;
        public int TargetId;
        public int SkillId;
        public int V0;
        public int V1;
        public int Seq;
        public int Tick;
        /// <summary>交付/起始时刻（表现秒）。</summary>
        public float Start;
        /// <summary>动作时长（秒）；交付节点恒 0。</summary>
        public float Duration;
        public byte CellX;
        public byte CellY;
        public byte ToX;
        public byte ToY;

        public bool Equals(PlannedNode other) =>
            Kind == other.Kind && UnitId == other.UnitId && TargetId == other.TargetId
            && SkillId == other.SkillId && V0 == other.V0 && V1 == other.V1
            && Seq == other.Seq && Tick == other.Tick
            && Start == other.Start && Duration == other.Duration
            && CellX == other.CellX && CellY == other.CellY && ToX == other.ToX && ToY == other.ToY;
    }

    /// <summary>
    /// 全局交付时间轴（#23③）：节点按（Start, Seq）排序的序列 + Makespan。
    /// 条数恒等于源流条数；节点全值类型，构造后只读。排序键全序（Seq 唯一）→ 确定。
    /// 4B2 通用播放器消费它（时间轴+快照+速率旋钮 → 画面，不认识游戏规则）；
    /// 4E 闸门读 Makespan 做排空参考。空时间轴（秒胜链）Count=0、Makespan=0。
    /// </summary>
    public sealed class PlannedTimeline
    {
        private readonly List<PlannedNode> _nodes;

        /// <summary>时间轴总长（秒）= 最晚节点的结束时刻；空时间轴为 0。</summary>
        public float Makespan { get; }

        internal PlannedTimeline(List<PlannedNode> sortedNodes)
        {
            _nodes = sortedNodes;
            float max = 0f;
            for (int i = 0; i < sortedNodes.Count; i++)
            {
                float end = sortedNodes[i].Start + sortedNodes[i].Duration;
                if (end > max) max = end;
            }
            Makespan = max;
        }

        public int Count => _nodes.Count;

        public PlannedNode this[int index] => _nodes[index];
    }
}
