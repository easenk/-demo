using System;
using UnityEngine;

namespace AutoChess.Presentation
{
    /// <summary>
    /// 通用播放器（#23④，批次B2 骨架）：时间轴 + 快照 + 速率旋钮 → 画面。
    /// 不认识游戏规则——本体只有"钟"（TimelineCursor 每帧按 unscaledDeltaTime×速率推进）
    /// 与两个出口：NodeDispatched（节点到了，画面层自己解码）、Finished（播完——
    /// 4E 排空闸门的"全部演绎空"信号源之一）。快照的画面消费归视图层（4B2=DebugBattleView
    /// 按开战帧建方体；4C 起=UnitActor）。
    /// 为什么 unscaledDeltaTime：#23⑥ 顿帧/震屏将走播放速率脉冲而非 timeScale——
    /// 播放钟不吃全局 timeScale，旋钮就是唯一的速度来源。
    /// </summary>
    public sealed class TimelinePlayer : MonoBehaviour
    {
        private TimelineCursor _cursor;
        private bool _finishedFired;

        /// <summary>节点到达（时间轴序）——画面层唯一入口。</summary>
        public event Action<PlannedNode> NodeDispatched;

        /// <summary>播完恰好一次（全部节点派发且 playhead ≥ Makespan）；空时间轴首帧即触发。</summary>
        public event Action Finished;

        public bool IsPlaying => _cursor != null && !_cursor.IsFinished;
        public float Time => _cursor != null ? _cursor.Time : 0f;
        public float Makespan => _cursor != null ? _cursor.Makespan : 0f;
        public float Progress => _cursor == null || _cursor.Makespan <= 0f ? 0f : _cursor.Time / _cursor.Makespan;
        public int DispatchedCount => _cursor != null ? _cursor.DispatchedCount : 0;
        public int TotalCount => _cursor != null ? _cursor.TotalCount : 0;
        public int CappedCount => _cursor != null ? _cursor.CappedCount : 0;

        /// <summary>速率旋钮：0=暂停 / 0.5=慢动作 / 1=常速 / 2=二倍（#23④ 同一旋钮）。</summary>
        public float Rate
        {
            get => _cursor != null ? _cursor.Rate : 1f;
            set { if (_cursor != null) _cursor.Rate = value; }
        }

        /// <summary>装填时间轴并从 0 秒开演（同一条时间轴可重放——游标全新）。</summary>
        public void Play(PlannedTimeline timeline)
        {
            _cursor = new TimelineCursor(timeline ?? throw new ArgumentNullException(nameof(timeline)));
            _finishedFired = false;
        }

        private void Update()
        {
            if (_cursor == null) return;
            if (!_cursor.IsFinished)
                _cursor.Advance(UnityEngine.Time.unscaledDeltaTime, n => NodeDispatched?.Invoke(n));
            if (_cursor.IsFinished && !_finishedFired)
            {
                _finishedFired = true;
                Finished?.Invoke();
            }
        }
    }
}
