using System;

namespace AutoChess.Presentation
{
    /// <summary>
    /// 排空闸门评估结果（#23⑧，批次4E）。
    /// </summary>
    public enum DrainGateResult
    {
        /// <summary>四条件未齐——闸门继续关着，结算不许发生。</summary>
        Waiting = 0,
        /// <summary>四条件齐备——正常开闸（唯一正常出口）。</summary>
        OpenAllClear,
        /// <summary>保险丝一：排空超时——投完 5 表演秒画面没收尾（bug 保险，正常流程不应触发）。</summary>
        OpenDrainTimeout,
        /// <summary>保险丝二：播放绝对熔断——墙钟 15 分钟到顶（含暂停时长；bug 保险）。</summary>
        OpenAbsoluteTimeout,
    }

    /// <summary>
    /// 排空计数包（4E 闸门读数）：调用方每帧从视图层收集喂给闸门，本结构纯数据。
    /// ② 全部演绎空 = 前四项全零（动作在途演员 / 存活特效[含晚生] / 在空飘字 / 已排未发火演出条目）；
    /// ③ 在途子弹清零 = InFlightProjectiles；
    /// ④ 倒地动画播完 = PendingDeaths。
    /// 让格淡出（尸体 0.25s 缩没）不在内——DoT 尸体永远没有 Remove 节点，等它会永远开不了闸
    /// （批次A 挂账镜像：DoT 致死无清格事件、尸体留格）；淡出由 playhead 继续推进自然走完。
    /// </summary>
    public struct DrainCounts
    {
        /// <summary>② 动作在途的演员数（移动补间/攻击窗+收招定格未走完）。</summary>
        public int BusyActors;
        /// <summary>② 存活特效数（playhead 域寿命未走完——含 Makespan 后才寿终的晚生特效）。</summary>
        public int AliveEffects;
        /// <summary>② 在空飘字数（0.9 秒上飘淡出未走完）。</summary>
        public int AliveFloats;
        /// <summary>② 已排定但尚未发火的演出条目数（出弹/爆炸/短爆——FireAt 尚未到点）。</summary>
        public int PendingVisualFires;
        /// <summary>③ 在途弹数。</summary>
        public int InFlightProjectiles;
        /// <summary>④ 倒地动画未播完的演员数。</summary>
        public int PendingDeaths;

        /// <summary>②③④ 三条件的合并判据（六计数全零）。</summary>
        public bool IsIdle =>
            BusyActors <= 0 && AliveEffects <= 0 && AliveFloats <= 0 && PendingVisualFires <= 0
            && InFlightProjectiles <= 0 && PendingDeaths <= 0;
    }

    /// <summary>
    /// 排空闸门（#23⑧，批次4E）：事件流投完之后、画面彻底演完之前，把结算挡在门外——
    /// 开闸才回调 GameSession.SettleBattle()（接线在 BattleRoot，结算不早于画面见玩家）。
    ///
    /// 四条件（#23 落死口径）：① 事件流投完（含 BattleEnd 终局条）+ ②③④（见 DrainCounts）。
    /// 纯 C# 零引擎依赖、时钟读数全注入——EditMode 直测；一个实例服务一场战斗。
    ///
    /// 两级保险丝（2026-10-19 用户定的数，构造可配）——均为 bug 保险，正常流程永不触发：
    ///  一=排空超时 5 秒（表演秒域：投完观测时刻起算的 playhead 差，严格超过才熔断——
    ///    暂停不推进不误伤，倍速语义与闸门解耦；只逮「画面永远收不了尾」的真 bug）；
    ///  二=播放绝对熔断 900 秒（墙钟域：构造读数起算的墙钟差，含暂停时长——兜住一切
    ///    「永不开闸」的 bug，包括速率卡 0 / 时间轴病态超长 / ① 永远不达成）。
    /// </summary>
    public sealed class BattleDrainGate
    {
        /// <summary>保险丝一默认值（排空超时，表演秒）。</summary>
        public const float DefaultDrainTimeoutSeconds = 5f;
        /// <summary>保险丝二默认值（播放绝对熔断，墙钟秒 = 15 分钟）。</summary>
        public const float DefaultAbsoluteTimeoutSeconds = 900f;

        private readonly float _drainTimeout;
        private readonly float _absoluteTimeout;
        private readonly float _beginWall;
        private bool _dispatchSeen;
        private float _dispatchPlayhead;

        /// <param name="wallNow">墙钟读数（生产=Time.realtimeSinceStartup 同域；测试注入）。</param>
        /// <param name="drainTimeoutSeconds">保险丝一：排空超时（表演秒，默认 5）。</param>
        /// <param name="absoluteTimeoutSeconds">保险丝二：播放绝对熔断（墙钟秒，默认 900=15 分钟）。</param>
        public BattleDrainGate(float wallNow,
            float drainTimeoutSeconds = DefaultDrainTimeoutSeconds,
            float absoluteTimeoutSeconds = DefaultAbsoluteTimeoutSeconds)
        {
            if (drainTimeoutSeconds <= 0f)
                throw new ArgumentOutOfRangeException(nameof(drainTimeoutSeconds), "排空超时必须为正数（配置错误）");
            if (absoluteTimeoutSeconds <= 0f)
                throw new ArgumentOutOfRangeException(nameof(absoluteTimeoutSeconds), "绝对熔断必须为正数（配置错误）");
            _beginWall = wallNow;
            _drainTimeout = drainTimeoutSeconds;
            _absoluteTimeout = absoluteTimeoutSeconds;
        }

        /// <summary>① 是否已观测到投完（首次观测时刻即排空期起算点）。</summary>
        public bool DispatchSeen => _dispatchSeen;

        /// <summary>
        /// 每帧评估（BattleRoot.Update 喂读数）。wallNow 与构造读数同域；playhead=表演秒；
        /// dispatchComplete=①（TimelinePlayer.IsFinished——全部节点派发完）。
        /// </summary>
        public DrainGateResult Evaluate(float wallNow, float playhead, bool dispatchComplete, DrainCounts counts)
        {
            if (dispatchComplete && !_dispatchSeen)
            {
                _dispatchSeen = true;
                _dispatchPlayhead = playhead;
            }

            // 保险丝二先判：绝对上限压倒一切（含 ① 未达成的病态长轴/速率卡 0）
            if (wallNow - _beginWall >= _absoluteTimeout)
                return DrainGateResult.OpenAbsoluteTimeout;

            // 四条件齐备才正常开闸
            if (dispatchComplete && counts.IsIdle)
                return DrainGateResult.OpenAllClear;

            // 保险丝一：仅排空期生效（① 已观测、playhead 仍在推进却超时收不了尾）
            if (_dispatchSeen && playhead - _dispatchPlayhead > _drainTimeout)
                return DrainGateResult.OpenDrainTimeout;

            return DrainGateResult.Waiting;
        }
    }
}
