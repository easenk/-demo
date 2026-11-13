using System;
using UnityEngine;

namespace AutoChess.Presentation
{
    /// <summary>
    /// 通用播放器（#23④，批次B2 骨架、批次4E 排空域）：时间轴 + 快照 + 速率旋钮 → 画面。
    /// 不认识游戏规则——本体只有"钟"（TimelineCursor 每帧按 unscaledDeltaTime×速率推进）
    /// 与两个出口：NodeDispatched（节点到了，画面层自己解码）、Finished（投完恰好一次——
    /// 4E 排空闸门的条件①信号源）。快照的画面消费归视图层（4C 起=UnitActor）。
    /// 为什么 unscaledDeltaTime：#23⑥ 顿帧/震屏将走播放速率脉冲而非 timeScale——
    /// 播放钟不吃全局 timeScale，旋钮就是唯一的速度来源。
    ///
    /// 排空域（4E）：投完（含 BattleEnd）后时钟不冻结，继续按速率推进——晚生特效/收招定格/
    /// 让格淡出按 playhead 到点自尽，画面彻底静默由 BattleDrainGate 认定（结算不早于画面见玩家）。
    /// Stop = 熔断冻结（保险丝路径专用）：停钟停派发、Time 保留——画面停在当前姿势；
    /// 正常开闸不调 Stop，备战期待机呼吸照常循环。
    /// </summary>
    public sealed class TimelinePlayer : MonoBehaviour
    {
        private TimelineCursor _cursor;
        private bool _finishedFired;
        private bool _stopped;

        /// <summary>节点到达（时间轴序）——画面层唯一入口。</summary>
        public event Action<PlannedNode> NodeDispatched;

        /// <summary>投完恰好一次（全部节点派发且 playhead ≥ Makespan）；空时间轴首帧即触发。
        /// 4E 起 Finished 只当排空闸门的条件①信号——结算由闸门开闸触发，不再直连。</summary>
        public event Action Finished;

        public bool IsPlaying => _cursor != null && !_cursor.IsFinished;
        /// <summary>① 是否投完（闸门条件读数；无时间轴=未投）。</summary>
        public bool IsFinished => _cursor != null && _cursor.IsFinished;
        public float Time => _cursor != null ? _cursor.Time : 0f;
        public float Makespan => _cursor != null ? _cursor.Makespan : 0f;
        /// <summary>进度条（0~1 钳制——排空域 playhead 会越过 Makespan，不让条溢出）。</summary>
        public float Progress => _cursor == null || _cursor.Makespan <= 0f
            ? 0f
            : Mathf.Clamp01(_cursor.Time / _cursor.Makespan);
        public int DispatchedCount => _cursor != null ? _cursor.DispatchedCount : 0;
        public int TotalCount => _cursor != null ? _cursor.TotalCount : 0;
        public int CappedCount => _cursor != null ? _cursor.CappedCount : 0;

        /// <summary>速率旋钮：0=暂停 / 0.5=慢动作 / 1=常速 / 2=二倍（#23④ 同一旋钮；
        /// 排空域照吃——暂停即排空冻结，闸门按 playhead 判超时不误伤）。</summary>
        public float Rate
        {
            get => _cursor != null ? _cursor.Rate : 1f;
            set { if (_cursor != null) _cursor.Rate = value; }
        }

        /// <summary>装填时间轴并从 0 秒开演（同一条时间轴可重放——游标全新、熔断解除）。</summary>
        public void Play(PlannedTimeline timeline)
        {
            _cursor = new TimelineCursor(timeline ?? throw new ArgumentNullException(nameof(timeline)));
            _finishedFired = false;
            _stopped = false;
        }

        /// <summary>熔断冻结（保险丝路径专用）：停钟停派发，Time 保留——画面停在当前姿势。</summary>
        public void Stop()
        {
            _stopped = true;
        }

        private void Update()
        {
            if (_cursor == null || _stopped) return;
            _cursor.Advance(UnityEngine.Time.unscaledDeltaTime, n => NodeDispatched?.Invoke(n));
            if (_cursor.IsFinished && !_finishedFired)
            {
                _finishedFired = true;
                Finished?.Invoke();
            }
        }
    }
}
