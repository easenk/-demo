using System;
using System.Collections.Generic;
using AutoChess.Presentation;
using NUnit.Framework;

namespace AutoChess.Tests
{
    /// <summary>
    /// 阶段4 批次B2：时间轴游标验收（#23④ 播放器的纯C#时钟核）。
    ///
    /// 分节契约：
    ///  · 派发序：按时间轴既定 (Start,Seq) 全序；Start == playhead 即派发（边界含端点）；
    ///  · 速率旋钮：Advance 推进量 = 真实秒 × 速率（0=暂停冻结姿势账本、0.5/1/2 同一旋钮）；
    ///    负速率/负步长一律抛（旋钮没有倒放）；
    ///  · 死亡封口（4B1 拍板的播放侧落法）：同单位 Start ≥ 倒地Start 的动作节点
    ///    （Move/Attack/Cast）被丢弃不派发；交付节点（伤害/让格/终局）不受封口照常兑现；
    ///    倒地前的动作保留（死亡打断的是"之后"的迟到动作）；
    ///  · 终局：全部派发且 playhead ≥ Makespan 才终；空时间轴构造即终、推进零派发零异常；
    ///    Makespan 同刻的尾节点（终局条）恰好派发。
    ///
    /// 合成时间轴经 InternalsVisibleTo 直构（4B1 同款入口——违规场景手写时间轴，
    /// 那正是"构造上不可能"要证明的东西）。
    /// </summary>
    public sealed class TimelineCursorTests
    {
        // ── 帮助：合成时间轴（构造信任输入有序——与规划器产出同款 (Start,Seq) 排序） ──

        private static PlannedTimeline Build(params PlannedNode[] nodes)
        {
            var list = new List<PlannedNode>(nodes);
            list.Sort((a, b) =>
            {
                int c = a.Start.CompareTo(b.Start);
                return c != 0 ? c : a.Seq.CompareTo(b.Seq);
            });
            return new PlannedTimeline(list);
        }

        private static PlannedNode N(PlannedNodeKind kind, float start, int seq,
            int unitId = 1, float duration = 0f)
            => new PlannedNode { Kind = kind, Start = start, Seq = seq, UnitId = unitId, Duration = duration };

        private sealed class Sink
        {
            public readonly List<PlannedNode> Got = new List<PlannedNode>();
            public int Count => Got.Count;
            public void On(PlannedNode n) => Got.Add(n);
        }

        // ─────────────────────── 空时间轴与终局 ───────────────────────

        [Test]
        public void Cursor_EmptyTimeline_FinishedAtOnce_AdvanceSafe()
        {
            var cursor = new TimelineCursor(Build());
            Assert.IsTrue(cursor.IsFinished, "空时间轴（秒胜链）构造即终局");
            Assert.AreEqual(0, cursor.TotalCount);
            int dispatched = cursor.Advance(5f, n => Assert.Fail("空轴不应派发"));
            Assert.AreEqual(0, dispatched);
        }

        [Test]
        public void Cursor_NotFinishedUntilPlayheadReachesMakespan()
        {
            // Move(Start=1, Duration=4) → Makespan=5：节点在 1s 派发，但 4.9s 仍未终（动作在途）
            var cursor = new TimelineCursor(Build(N(PlannedNodeKind.Move, 1f, 1, duration: 4f)));
            var sink = new Sink();
            cursor.Advance(1f, sink.On);
            Assert.AreEqual(1, sink.Count, "Start 到点即派发");
            cursor.Advance(3.9f, sink.On);
            Assert.IsFalse(cursor.IsFinished, "playhead 4.9s < Makespan 5s——在途动作未完不终");
            cursor.Advance(0.1f, sink.On);
            Assert.IsTrue(cursor.IsFinished, "playhead 抵达 Makespan 即终");
        }

        [Test]
        public void Cursor_TailNodesAtMakespan_DispatchedExactly()
        {
            // 交付节点 Duration=0 → Start=2 即 Makespan：BattleEnd 与同刻伤害都应派发
            var cursor = new TimelineCursor(Build(
                N(PlannedNodeKind.Damage, 2f, 1, unitId: 2),
                N(PlannedNodeKind.BattleEnd, 2f, 2)));
            var sink = new Sink();
            cursor.Advance(2f, sink.On);
            Assert.AreEqual(2, sink.Count, "Makespan 同刻的尾节点恰好派发");
            Assert.AreEqual(PlannedNodeKind.Damage, sink.Got[0].Kind);
            Assert.AreEqual(PlannedNodeKind.BattleEnd, sink.Got[1].Kind, "同刻按 Seq 分先后");
            Assert.IsTrue(cursor.IsFinished);
        }

        // ─────────────────────── 派发序与边界 ───────────────────────

        [Test]
        public void Cursor_DispatchOrder_StartThenSeq()
        {
            var cursor = new TimelineCursor(Build(
                N(PlannedNodeKind.Attack, 1f, 5, unitId: 1),
                N(PlannedNodeKind.Attack, 1f, 3, unitId: 2),
                N(PlannedNodeKind.Move, 0.5f, 9, unitId: 3)));
            var sink = new Sink();
            cursor.Advance(10f, sink.On);
            Assert.AreEqual(3, sink.Count);
            Assert.AreEqual(0.5f, sink.Got[0].Start, "先按 Start");
            Assert.AreEqual(3, sink.Got[1].Seq, "同刻按 Seq");
            Assert.AreEqual(5, sink.Got[2].Seq);
            Assert.AreEqual(3, cursor.DispatchedCount);
            Assert.AreEqual(0, cursor.CappedCount);
        }

        [Test]
        public void Cursor_Boundary_StartEqualToPlayheadDispatches()
        {
            var cursor = new TimelineCursor(Build(N(PlannedNodeKind.Mana, 1f, 1)));
            int n = cursor.Advance(1f, new Sink().On);
            Assert.AreEqual(1, n, "Start == playhead 属于「到达」（含端点）");
        }

        [Test]
        public void Cursor_NullSink_ClockStillAdvances()
        {
            var cursor = new TimelineCursor(Build(N(PlannedNodeKind.Heal, 1f, 1)));
            cursor.Advance(0.5f, null);
            Assert.AreEqual(0.5f, cursor.Time);
            Assert.AreEqual(0, cursor.DispatchedCount);
            Assert.DoesNotThrow(() => cursor.Advance(0.5f, null));
            Assert.AreEqual(1, cursor.DispatchedCount, "跨过 Start 后计数照常入账");
        }

        // ─────────────────────── 速率旋钮 ───────────────────────

        [Test]
        public void Cursor_RateDial_AdvanceEqualsDeltaTimesRate()
        {
            var cursor = new TimelineCursor(Build(N(PlannedNodeKind.Move, 3f, 1, duration: 1f)));
            cursor.Rate = 2f;
            cursor.Advance(1f, null);
            Assert.AreEqual(2f, cursor.Time, "二倍速：1s 真实 = 2s 表演");
            var sink = new Sink();
            cursor.Advance(1f, sink.On);
            Assert.AreEqual(1, sink.Count, "playhead 4s 已越过 Start=3——派发");
        }

        [Test]
        public void Cursor_RateZero_PauseFreezesTimeAndDispatch()
        {
            var cursor = new TimelineCursor(Build(N(PlannedNodeKind.Attack, 1f, 1)));
            cursor.Rate = 0f;
            var sink = new Sink();
            int n = cursor.Advance(10f, sink.On);
            Assert.AreEqual(0, n, "暂停=零推进，永不派发");
            Assert.AreEqual(0f, cursor.Time);
            cursor.Rate = 1f;                       // 旋钮回常速继续
            Assert.AreEqual(1, cursor.Advance(1f, sink.On));
        }

        [Test]
        public void Cursor_NegativeRateOrDelta_Throws()
        {
            var cursor = new TimelineCursor(Build());
            Assert.Throws<ArgumentOutOfRangeException>(() => cursor.Rate = -0.5f, "旋钮没有倒放");
            Assert.Throws<ArgumentOutOfRangeException>(() => cursor.Advance(-1f, null), "步长不可为负");
        }

        // ─────────────────────── 死亡封口（4B1 拍板的播放侧落法） ───────────────────────

        [Test]
        public void Cursor_DeathCap_LateActionsDropped_DeliveriesSurvive()
        {
            // unit1 倒地(Start=2.0) → 迟到动作 Attack(2.5)/Move(3.0) 被封；Damage(2.8) 照派
            var cursor = new TimelineCursor(Build(
                N(PlannedNodeKind.Death, 2f, 1, unitId: 1, duration: 0.4f),
                N(PlannedNodeKind.Attack, 2.5f, 2, unitId: 1),
                N(PlannedNodeKind.Damage, 2.8f, 3, unitId: 1),
                N(PlannedNodeKind.Move, 3f, 4, unitId: 1)));
            var sink = new Sink();
            cursor.Advance(10f, sink.On);
            Assert.AreEqual(2, sink.Count, "Death + Damage 派发");
            Assert.AreEqual(PlannedNodeKind.Death, sink.Got[0].Kind);
            Assert.AreEqual(PlannedNodeKind.Damage, sink.Got[1].Kind, "交付节点不受封口");
            Assert.AreEqual(2, cursor.CappedCount, "Attack 与 Move 各封一条");
            Assert.AreEqual(2, cursor.DispatchedCount);
        }

        [Test]
        public void Cursor_DeathCap_ActionBeforeFallKept()
        {
            // Attack(1.5) 早于倒地(2.0)：保留（倒地打断的是"之后"的迟到动作，不是在途动作）
            var cursor = new TimelineCursor(Build(
                N(PlannedNodeKind.Attack, 1.5f, 1, unitId: 1, duration: 0.45f),
                N(PlannedNodeKind.Death, 2f, 2, unitId: 1, duration: 0.4f)));
            var sink = new Sink();
            cursor.Advance(10f, sink.On);
            Assert.AreEqual(2, sink.Count, "倒地前的动作照常派发");
            Assert.AreEqual(0, cursor.CappedCount);
        }

        [Test]
        public void Cursor_DeathCap_OtherUnitsUnaffected()
        {
            var cursor = new TimelineCursor(Build(
                N(PlannedNodeKind.Death, 2f, 1, unitId: 1, duration: 0.4f),
                N(PlannedNodeKind.Attack, 2.5f, 2, unitId: 7),
                N(PlannedNodeKind.Move, 3f, 3, unitId: 8, duration: 0.3f)));
            var sink = new Sink();
            cursor.Advance(10f, sink.On);
            Assert.AreEqual(3, sink.Count, "封口只作用于死者本人的动作");
            Assert.AreEqual(0, cursor.CappedCount);
        }
    }
}
