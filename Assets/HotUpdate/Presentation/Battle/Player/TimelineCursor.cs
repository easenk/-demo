using System;
using System.Collections.Generic;

namespace AutoChess.Presentation
{
    /// <summary>
    /// 时间轴游标（#23④ 播放器的纯C#时钟核，批次B2）：表演秒 playhead + 速率旋钮 + 节点派发。
    /// 与渲染完全解耦——TimelinePlayer 只拿它当表走，画面层订阅派发回调即可（不认识游戏规则）。
    ///
    /// 速率=表演速率旋钮（#23④：暂停0/慢动作0.5/常速1/二倍2 同一旋钮）：Advance 推进量 =
    /// 真实秒 × 速率；画面层一切姿势按 playhead 参数化——倍速/暂停对全部演出自动生效、零漂移。
    ///
    /// 死亡封口的播放侧落法（4B1 拍板，本类落实）：构造时扫全部 Death 节点建"单位→倒地Start"表；
    /// 派发时丢弃"同一单位 Start ≥ 倒地Start"的动作节点（Move/Attack/Cast——病态级联防御：
    /// 资产严重违约下死者被推后到倒地之后的迟到动作不再演出；合规资产下不可达，构造上兜底）。
    /// 交付节点（伤害/回蓝/让格/终局等）不受封口——账目与终局照常兑现。
    /// </summary>
    public sealed class TimelineCursor
    {
        private readonly PlannedTimeline _timeline;
        private readonly Dictionary<int, float> _deathStart = new Dictionary<int, float>(16);
        private int _next;
        private float _time;
        private float _rate = 1f;

        public TimelineCursor(PlannedTimeline timeline)
        {
            _timeline = timeline ?? throw new ArgumentNullException(nameof(timeline));
            for (int i = 0; i < timeline.Count; i++)
            {
                PlannedNode n = timeline[i];
                if (n.Kind == PlannedNodeKind.Death)
                    _deathStart[n.UnitId] = n.Start;   // 一个单位至多一次死亡，后写同值无害
            }
        }

        /// <summary>表演秒（playhead）。</summary>
        public float Time => _time;

        /// <summary>速率旋钮：0=暂停、0.5=慢动作、1=常速、2=二倍（负值非法——暂停就是 0）。</summary>
        public float Rate
        {
            get => _rate;
            set
            {
                if (value < 0f) throw new ArgumentOutOfRangeException(nameof(value), "速率不可为负（暂停=0）");
                _rate = value;
            }
        }

        public float Makespan => _timeline.Makespan;
        public int TotalCount => _timeline.Count;

        /// <summary>已派发条数（被死亡封口跳过的节点不计——封口=不存在）。</summary>
        public int DispatchedCount { get; private set; }

        /// <summary>被死亡封口跳过的动作节点数（观察口；合规资产下恒 0）。</summary>
        public int CappedCount { get; private set; }

        /// <summary>全部节点派发完且 playhead ≥ Makespan（空时间轴构造即终局）。</summary>
        public bool IsFinished => _next >= _timeline.Count && _time >= _timeline.Makespan;

        /// <summary>
        /// 推进 deltaSeconds 真实秒，派发所有 Start ≤ playhead 的未派发节点
        /// （时间轴既定的 (Start,Seq) 全序——跨目标同刻合法、同刻按 Seq 分先后）。
        /// onNode 为 null 时只走钟（测试/拖动场景）。返回本次派发条数。
        /// </summary>
        public int Advance(float deltaSeconds, Action<PlannedNode> onNode)
        {
            if (deltaSeconds < 0f) throw new ArgumentOutOfRangeException(nameof(deltaSeconds));
            if (IsFinished) return 0;
            _time += deltaSeconds * _rate;

            int dispatched = 0;
            while (_next < _timeline.Count && _timeline[_next].Start <= _time)
            {
                PlannedNode n = _timeline[_next];
                _next++;
                if (IsCapped(n)) { CappedCount++; continue; }
                onNode?.Invoke(n);
                DispatchedCount++;
                dispatched++;
            }
            return dispatched;
        }

        // 只封动作节点；交付节点（Damage/Heal/Mana/MaxHp/Death/Remove/OreDrop/BattleEnd）照常兑现
        private bool IsCapped(PlannedNode n)
        {
            if (n.Kind != PlannedNodeKind.Move
                && n.Kind != PlannedNodeKind.Attack
                && n.Kind != PlannedNodeKind.Cast)
                return false;
            return _deathStart.TryGetValue(n.UnitId, out float fall) && n.Start >= fall;
        }
    }
}
