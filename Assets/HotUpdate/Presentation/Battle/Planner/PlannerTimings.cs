using System;
using System.Collections.Generic;

namespace AutoChess.Presentation
{
    /// <summary>
    /// 单位的规划时刻数据（#23⑥ 动画数据契约的最小可用子集，批次B1）：
    /// 排程只关心"每个动作占多长、命中落在动画的百分之几处"——帧率/帧数/出弹帧等
    /// 归 4C 的 AnimationDef 全量契约，本表是它喂给规划器的那一面。
    /// 占位数值（2026-11-03 拍板）：攻 0.45s/命中 50%、施法 0.60s/50%、移动 0.30s、倒地 0.40s，
    /// 4C 录真数据时逐单位覆盖；录入时必须逐项校验守约纪律"动作时长 ≤ 对应动作间隔"。
    /// </summary>
    public struct UnitTiming
    {
        /// <summary>普攻出手动画时长（秒，前摇+收招整体算）。0 = 瞬发。</summary>
        public float AttackDuration;
        /// <summary>普攻命中帧在动画中的位置（0~1，0.5 = 动画正中见血）。</summary>
        public float AttackHitPercent;
        /// <summary>施法动画时长（秒）。0 = 瞬发。</summary>
        public float CastDuration;
        /// <summary>施法命中帧位置（0~1）。</summary>
        public float CastHitPercent;
        /// <summary>一格移动补间时长（秒；战斗走位恒 1 格，CombatSystem.TryStep 焊死）。</summary>
        public float MoveDuration;
        /// <summary>倒地动画时长（秒）——该格的让渡时刻 = 倒地开始 + 本值。</summary>
        public float DeathDuration;
    }

    /// <summary>
    /// 规划时刻表：单位 id → UnitTiming。为什么按"单位"而不是"棋子"键：
    /// 流条目与时间轴节点只携带单位 id（批次A 快照没存 CharId），且同一棋子不同星级的
    /// 动画时长本就可能不同——按单位给表由 4B2 战台组合根从名册展开，精度与职责都对。
    /// 构造与 Set 全量校验（时长 ≥0、帧百分比 ∈[0,1]，违规即抛——fail fast）；
    /// Get 对缺失单位回退 Fallback（规划必须全函数：宁可占位慢，不可半路炸；
    /// 生产侧组合根应逐单位填满表，回退值只做兜底）。
    /// </summary>
    public sealed class PlannerTimingTable
    {
        private readonly Dictionary<int, UnitTiming> _perUnit = new Dictionary<int, UnitTiming>();

        /// <summary>缺失单位的回退时刻（构造时校验）。</summary>
        public UnitTiming Fallback { get; }

        public PlannerTimingTable(UnitTiming fallback)
        {
            Fallback = Validate(fallback);
        }

        /// <summary>逐单位覆盖（重复 Set = 后值覆盖前值）。</summary>
        public void Set(int unitId, UnitTiming timing)
        {
            _perUnit[unitId] = Validate(timing);
        }

        /// <summary>查表：有覆盖用覆盖，无覆盖用 Fallback。</summary>
        public UnitTiming Get(int unitId)
        {
            return _perUnit.TryGetValue(unitId, out UnitTiming t) ? t : Fallback;
        }

        private static UnitTiming Validate(UnitTiming t)
        {
            if (t.AttackDuration < 0f || t.CastDuration < 0f || t.MoveDuration < 0f || t.DeathDuration < 0f)
                throw new ArgumentException("动画时长不可为负");
            if (t.AttackHitPercent < 0f || t.AttackHitPercent > 1f)
                throw new ArgumentException("普攻命中帧百分比必须在 [0,1]");
            if (t.CastHitPercent < 0f || t.CastHitPercent > 1f)
                throw new ArgumentException("施法命中帧百分比必须在 [0,1]");
            return t;
        }
    }
}
