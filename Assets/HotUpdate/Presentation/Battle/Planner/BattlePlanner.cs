using System;
using System.Collections.Generic;
using AutoChess.Game;

namespace AutoChess.Presentation
{
    /// <summary>
    /// 规划器（#23③，批次B1）：把预演事件流排成全局交付时间轴——"逻辑定序、表现定时"。
    /// 顺序约束全部来自逻辑流序（全序、无环），时刻由单遍贪心分配（每节点取各约束最大值）：
    /// 必然终止、确定（同流同表两算逐节点全等）、O(流长) 排程 + 一次排序。
    ///
    /// 三不变式 → 三张账本：
    ///  · I1 同单位串行 → busy 账本：下一动作 Start ≥ 上一动作 Start+Duration
    ///    （唯一例外：死亡打断自身动作——倒地起于致死交付，busy 被倒地覆盖=封口）；
    ///  · I2 同目标交付序 → chain 账本：对同一接收者，Damage/Heal/Mana/MaxHp 按流序
    ///    严格递增（最小间隔 DeliveryGap；同目标禁同刻=严格序，跨目标同刻合法=AOE 同帧）；
    ///    普攻出手再"预扣命中偏移"：Start ≥ chain[目标]+Gap-命中偏移 → 落点构造上
    ///    排在目标链之后、且必落在自己挥击窗内（不同前摇的倒置/打尸体从构造上杜绝）；
    ///  · I3+I4 占位互斥按序 → releaseAt 账本：到达任一格 ≥ 该格释放时刻。释放写者三种：
    ///    移动者走完（起点格保守持到补间走完，2026-11-03 拍板）/ 尸体倒地完成
    ///    （Died+Removed 配对落笔——"倒地前格不让位"的机制本身）/ DoT 尸体永不写
    ///    （镜像逻辑层"不清格"现状——阶段4-接口设计 九节挂账）。
    ///
    /// 恒等式（守约对齐的时间半边）：任何节点 Start ≥ L = Tick × TickSeconds——约束只推后
    /// 不提前，画面永不早于逻辑。守约（动作时长≤对应动作间隔）时同类动作的约束全不绑定：
    /// 出手全落逻辑时刻（零等待零漂移）；违约时等待沿三张账本传播级联，但序恒不破。
    /// （相邻异种动作——攻击后 1 tick 施法、移动后 1 tick 施法——的 busy 串行是固有错开，
    /// 属"序不可矛盾时可错开"，不算违约。）
    ///
    /// 伤害配对三规则：
    ///  · 普攻伤害 = 流的紧邻下一条（PerformAttack 里两条广播之间零代码——源码序焊死）；
    ///  · 施法块：SkillCast 开块，块内 Src=施法者 的 Damage 全配对到同一命中时刻；
    ///    块由"施法者 ManaChanged（ClearMana 恒发）/任一新动作事件/异源伤害"终结，
    ///    块内 Died/Removed/Healed(施法者)=击杀回复 插花不清块；
    ///  · 独立伤害（DoT/灼烧等 tick 末来源）：无配对，落逻辑时刻按链递增。
    /// 兜底钳制：配对交付再过 max(命中时刻, L, chain+Gap)——即使将来出现保留型施法
    /// （KeepManaAfterCast 无清蓝 → 块悬挂误配）也绝不早于逻辑、绝不倒置，只可能变慢。
    ///
    /// 死亡封口的播放侧落法（4B2 落实，本类不改写已排节点）：播放器丢弃"同一单位
    /// Start ≥ 该单位倒地 Start"的动作节点——病态级联下（资产严重违约才可达）死者
    /// 的迟到动作不再演出。合规资产下该情形不可达。
    ///
    /// 2026-11-03 拍板（P4-B1 全按建议）：①输入=事件流+时刻表两参、不消费快照——
    /// 格位信息流条目全有（初始站位逻辑棋盘保证合法、死亡格/走位格/矿格都在条目里），
    /// 快照的主顾是 4B2 播放器，#23③"事件流+快照→规划器"按管线口径理解；
    /// ②攻击预扣与施法钳制的不对称（施法多目标出手时未知，只能交付时钳制——极端级联下
    /// 落点可晚于施法动画，顺序不破的已记录让步）；③占位时刻=攻 0.45s/命中 50%、
    /// 施法 0.60s/50%、移动 0.30s、倒地 0.40s（4C AnimationDef 替换）；④保守占位；
    /// ⑤InternalsVisibleTo("AutoChess.Tests") 供测试直构合成流。
    /// 已知边界（遗留观察）：击杀回复等后果性数值事件按"排水直设"落逻辑时刻，
    /// 长前摇下可早于命中画面（不破序；4C/4F 视觉打磨期议）。
    /// </summary>
    public sealed class BattlePlanner
    {
        /// <summary>同一接收者两次交付的最小间隔（秒）。</summary>
        public const float DeliveryGap = 0.001f;

        // ── 一次 Plan 的状态（每次 Plan 重建；实例可复用，逐次独立） ──
        private List<PlannedNode> _nodes;
        private Dictionary<int, float> _busy;                 // I1：单位忙到几点
        private Dictionary<int, float> _chain;               // I2：对单位的最后一笔交付
        private Dictionary<int, float> _lastDamage;          // 死亡配对：对单位的最后一笔伤害交付
        private Dictionary<int, float> _releaseAt;           // I3/I4：格位释放时刻（下一任最早到达）
        private Dictionary<int, PendingDeath> _pendingDeath; // Died → (格, 倒地完成)，等 Removed 落笔
        private Dictionary<int, float> _deathStartAtCell;    // 矿石链：该格最后一次倒地的开始时刻
        private int _castCtxCaster;                          // 施法块：施法者 id（0 = 无块）
        private float _castCtxHit;                           // 施法块：命中时刻
        private int _atkCtxAttacker;                         // 普攻配对：出手者/目标/命中（单发——只活到紧邻下一条）
        private int _atkCtxTarget;
        private float _atkCtxHit;
        private bool _atkCtxActive;

        /// <summary>把事件流排成全局交付时间轴。可重复调用（每次重建账本，逐次独立）；
        /// 秒胜链（流空）产出空时间轴。</summary>
        public PlannedTimeline Plan(BattleEventStream stream, PlannerTimingTable timings)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (timings == null) throw new ArgumentNullException(nameof(timings));
            Reset(stream.Count);

            for (int i = 0; i < stream.Count; i++)
            {
                StreamItem it = stream[i];
                bool atkActive = _atkCtxActive;
                _atkCtxActive = false;   // 普攻配对单发：先读上一条留下的上下文再复位（紧邻即配对）
                switch (it.Kind)
                {
                    case StreamEventKind.UnitAttacked: OnAttacked(it, timings); break;
                    case StreamEventKind.SkillCast: OnCast(it, timings); break;
                    case StreamEventKind.UnitDamaged: OnDamaged(it, atkActive); break;
                    case StreamEventKind.UnitDied: OnDied(it, timings); break;
                    case StreamEventKind.UnitHealed: OnHealed(it); break;
                    case StreamEventKind.ManaChanged: OnManaChanged(it); break;
                    case StreamEventKind.UnitMaxHpChanged: OnMaxHpChanged(it); break;
                    case StreamEventKind.UnitMoved: OnMoved(it, timings); break;
                    case StreamEventKind.UnitRemoved: OnRemoved(it); break;
                    case StreamEventKind.OreDropped: OnOreDropped(it); break;
                    case StreamEventKind.BattleEnded: OnBattleEnded(it); break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(it.Kind),
                            $"规划器未认识事件 {it.Kind}——白名单扩容时必须同步扩规划器与播放器");
                }
            }

            _nodes.Sort(CompareNodes);   // (Start, Seq) 全序（Seq 唯一）→ 排序确定
            return new PlannedTimeline(_nodes);
        }

        // ─────────────────────── 动作节点 ───────────────────────

        private void OnAttacked(StreamItem it, PlannerTimingTable timings)
        {
            _castCtxCaster = 0;   // 新动作事件：施法块终结
            UnitTiming t = timings.Get(it.IdA);
            float hitOffset = t.AttackHitPercent * t.AttackDuration;
            float start = Math.Max(it.Tick * GameConst.TickSeconds, BusyOf(it.IdA));
            if (_chain.TryGetValue(it.IdB, out float lastToTarget))   // I2 预扣，见类注释
                start = Math.Max(start, lastToTarget + DeliveryGap - hitOffset);
            _nodes.Add(new PlannedNode
            {
                Kind = PlannedNodeKind.Attack, UnitId = it.IdA, TargetId = it.IdB,
                V0 = it.V0, Seq = it.Seq, Tick = it.Tick,
                Start = start, Duration = t.AttackDuration,
            });
            _busy[it.IdA] = start + t.AttackDuration;
            _atkCtxActive = true;
            _atkCtxAttacker = it.IdA;
            _atkCtxTarget = it.IdB;
            _atkCtxHit = start + hitOffset;
        }

        private void OnCast(StreamItem it, PlannerTimingTable timings)
        {
            UnitTiming t = timings.Get(it.IdA);
            float hitOffset = t.CastHitPercent * t.CastDuration;
            float start = Math.Max(it.Tick * GameConst.TickSeconds, BusyOf(it.IdA));
            // 施法不做 I2 预扣：多目标名单出手时未知——交付时钳制兜底（OnDamaged）
            _nodes.Add(new PlannedNode
            {
                Kind = PlannedNodeKind.Cast, UnitId = it.IdA, TargetId = it.IdC,
                SkillId = it.IdB, V0 = it.V0, Seq = it.Seq, Tick = it.Tick,
                Start = start, Duration = t.CastDuration,
            });
            _busy[it.IdA] = start + t.CastDuration;
            _castCtxCaster = it.IdA;
            _castCtxHit = start + hitOffset;
        }

        private void OnMoved(StreamItem it, PlannerTimingTable timings)
        {
            _castCtxCaster = 0;   // 新动作事件：施法块终结
            UnitTiming t = timings.Get(it.IdA);
            _releaseAt.TryGetValue(CellKey(it.ToX, it.ToY), out float free);
            // 缺失 = 0：进场合法性逻辑层棋盘已保证（非法进场的 Move 事件根本不会发生）
            float start = Math.Max(it.Tick * GameConst.TickSeconds, BusyOf(it.IdA));
            // I3/I4：到达 = Start+MoveDuration ≥ 释放时刻（"倒地前格不让位"的机制本身）
            start = Math.Max(start, free - t.MoveDuration);
            _nodes.Add(new PlannedNode
            {
                Kind = PlannedNodeKind.Move, UnitId = it.IdA,
                CellX = it.CellX, CellY = it.CellY, ToX = it.ToX, ToY = it.ToY,
                Seq = it.Seq, Tick = it.Tick, Start = start, Duration = t.MoveDuration,
            });
            _busy[it.IdA] = start + t.MoveDuration;
            // 起点格保守持到走完才让渡（时刻单调：走完 ≥ 自己到达 ≥ 前任离场，直接覆盖安全）
            _releaseAt[CellKey(it.CellX, it.CellY)] = start + t.MoveDuration;
        }

        private void OnDied(StreamItem it, PlannerTimingTable timings)
        {
            // 倒地起于致死伤害的交付时刻（"死亡到达即封口、倒地时刻=包裹送达"）；
            // 紧邻前条必是致死 Damage（TakeDamage 内同步两播），_lastDamage 恒有值——防御兜底 L
            float fall = _lastDamage.TryGetValue(it.IdA, out float last) ? last : it.Tick * GameConst.TickSeconds;
            UnitTiming t = timings.Get(it.IdA);
            _nodes.Add(new PlannedNode
            {
                Kind = PlannedNodeKind.Death, UnitId = it.IdA,
                CellX = it.CellX, CellY = it.CellY, Seq = it.Seq, Tick = it.Tick,
                Start = fall, Duration = t.DeathDuration,
            });
            _busy[it.IdA] = fall + t.DeathDuration;   // 覆盖而非 max：死亡可打断自身动作（封口）
            if (!_chain.TryGetValue(it.IdA, out float c) || fall > c) _chain[it.IdA] = fall;
            int cellKey = CellKey(it.CellX, it.CellY);
            _pendingDeath[it.IdA] = new PendingDeath(cellKey, fall + t.DeathDuration);
            _deathStartAtCell[cellKey] = fall;   // 矿石链：本格最后一次倒地的开始时刻
        }

        // ─────────────────────── 伤害与数值交付 ───────────────────────

        private void OnDamaged(StreamItem it, bool atkActive)
        {
            float L = it.Tick * GameConst.TickSeconds;
            float d;
            if (atkActive && _atkCtxAttacker == it.IdB && _atkCtxTarget == it.IdA)
            {
                // 普攻配对（紧邻下一条）：落点已预扣过链约束，此处的 max 是防御纵深
                d = Math.Max(Math.Max(_atkCtxHit, L), NextDelivery(it.IdA));
            }
            else if (_castCtxCaster != 0 && _castCtxCaster == it.IdB)
            {
                // 施法块配对：多目标共享同一命中时刻；钳制兜底（见类注释"已记录让步"）
                d = Math.Max(Math.Max(_castCtxHit, L), NextDelivery(it.IdA));
            }
            else
            {
                // 独立伤害（DoT/灼烧等 tick 末来源）：落逻辑时刻、按链递增
                d = Math.Max(L, NextDelivery(it.IdA));
                if (_castCtxCaster != 0 && it.IdB != _castCtxCaster)
                    _castCtxCaster = 0;   // 异源伤害：施法块终结（防御探针）
            }
            _nodes.Add(new PlannedNode
            {
                Kind = PlannedNodeKind.Damage, UnitId = it.IdA, TargetId = it.IdB,
                V0 = it.V0, V1 = it.V1, Seq = it.Seq, Tick = it.Tick,
                Start = d, Duration = 0f,
            });
            _chain[it.IdA] = d;
            _lastDamage[it.IdA] = d;
        }

        private void OnHealed(StreamItem it) => EmitValue(PlannedNodeKind.Heal, it);

        private void OnManaChanged(StreamItem it)
        {
            EmitValue(PlannedNodeKind.Mana, it);
            if (it.IdA == _castCtxCaster)
                _castCtxCaster = 0;   // ClearMana 恒发 ManaChanged——施法块的可靠终结探针（KeepMana 例外走兜底钳制）
        }

        private void OnMaxHpChanged(StreamItem it) => EmitValue(PlannedNodeKind.MaxHp, it);

        /// <summary>数值交付（回血/蓝/上限）：落逻辑时刻、按接收者链递增；
        /// 不占动作、不打断动作（"数值事件排水直设"——被打得血条乱跳的单位照常开拍）。</summary>
        private void EmitValue(PlannedNodeKind kind, StreamItem it)
        {
            float d = Math.Max(it.Tick * GameConst.TickSeconds, NextDelivery(it.IdA));
            _nodes.Add(new PlannedNode
            {
                Kind = kind, UnitId = it.IdA, V0 = it.V0, V1 = it.V1,
                Seq = it.Seq, Tick = it.Tick, Start = d, Duration = 0f,
            });
            _chain[it.IdA] = d;
        }

        // ─────────────────────── 让格 / 掉矿 / 终局 ───────────────────────

        private void OnRemoved(StreamItem it)
        {
            // 让格节点 = 格位释放时刻。战斗期 Removed 恒跟在 Died 后（HandleKill）——
            // DoT 致死无 Removed：无让格节点、releaseAt 无写者（该格永不释放，批次A 挂账镜像）
            float release;
            if (_pendingDeath.TryGetValue(it.IdA, out PendingDeath pd))
            {
                release = pd.FallEnd;
                _pendingDeath.Remove(it.IdA);
                _releaseAt[pd.CellKey] = pd.FallEnd;   // 倒地完成：该格让渡（写者=占用人，时刻单调）
            }
            else
            {
                release = it.Tick * GameConst.TickSeconds;   // 理论不可达（防御）：立即释放
                int key = CellKey(it.CellX, it.CellY);
                _releaseAt.TryGetValue(key, out float old);
                _releaseAt[key] = Math.Max(old, release);
            }
            _nodes.Add(new PlannedNode
            {
                Kind = PlannedNodeKind.Remove, UnitId = it.IdA,
                CellX = it.CellX, CellY = it.CellY, Seq = it.Seq, Tick = it.Tick,
                Start = release, Duration = 0f,
            });
        }

        private void OnOreDropped(StreamItem it)
        {
            // 掉矿链到死亡格最后一次倒地的开始之后（随击杀包裹闪现）；无记录（理论不可达）= 落逻辑时刻
            float L = it.Tick * GameConst.TickSeconds;
            float d = _deathStartAtCell.TryGetValue(CellKey(it.CellX, it.CellY), out float fall)
                ? Math.Max(L, fall + DeliveryGap)
                : L;
            _nodes.Add(new PlannedNode
            {
                Kind = PlannedNodeKind.OreDrop, UnitId = it.IdA, V0 = it.V0,
                CellX = it.CellX, CellY = it.CellY, Seq = it.Seq, Tick = it.Tick,
                Start = d, Duration = 0f,
            });
        }

        private void OnBattleEnded(StreamItem it)
        {
            _castCtxCaster = 0;
            _nodes.Add(new PlannedNode
            {
                Kind = PlannedNodeKind.BattleEnd, V0 = it.V0, V1 = it.V1,
                Seq = it.Seq, Tick = it.Tick, Start = it.Tick * GameConst.TickSeconds, Duration = 0f,
            });
        }

        // ─────────────────────── 杂项 ───────────────────────

        private float BusyOf(int unitId) => _busy.TryGetValue(unitId, out float b) ? b : 0f;

        /// <summary>接收者链的下一笔最早时刻：有链 = 链值+Gap；无链 = 0（首笔只受逻辑时刻约束）。</summary>
        private float NextDelivery(int unitId) => _chain.TryGetValue(unitId, out float c) ? c + DeliveryGap : 0f;

        private static int CellKey(byte x, byte y) => y * GameConst.BoardWidth + x;

        private static int CompareNodes(PlannedNode a, PlannedNode b)
        {
            int c = a.Start.CompareTo(b.Start);
            return c != 0 ? c : a.Seq.CompareTo(b.Seq);
        }

        private void Reset(int capacity)
        {
            _nodes = new List<PlannedNode>(capacity);
            _busy = new Dictionary<int, float>(32);
            _chain = new Dictionary<int, float>(32);
            _lastDamage = new Dictionary<int, float>(32);
            _releaseAt = new Dictionary<int, float>(16);
            _pendingDeath = new Dictionary<int, PendingDeath>(16);
            _deathStartAtCell = new Dictionary<int, float>(16);
            _castCtxCaster = 0;
            _atkCtxActive = false;
        }

        private struct PendingDeath
        {
            public int CellKey;
            public float FallEnd;
            public PendingDeath(int cellKey, float fallEnd)
            {
                CellKey = cellKey;
                FallEnd = fallEnd;
            }
        }
    }
}
