using System;
using AutoChess.Presentation;
using NUnit.Framework;

namespace AutoChess.Tests
{
    /// <summary>
    /// 阶段4 批次4E：排空闸门验收（#23⑧——结算的唯一门禁，开闸才回调 SettleBattle）。
    ///
    /// 分节契约：
    ///  · 四条件齐备才正常开闸：①事件流投完（dispatchComplete）+ ②演绎空（动作在途/
    ///    存活特效/在空飘字/未发火演出条目四计数全零）+ ③在途子弹清零 + ④倒地动画播完
    ///    ——缺任何一项都 Waiting，六个计数逐项单独非零各锁一条；
    ///  · 保险丝一（排空超时 5 表演秒，可配）：投完观测时刻起算的 playhead 差、严格超过
    ///    才熔断（恰好 5.0 不熔）；playhead 冻结（暂停）永不熔——倍速/暂停与闸门解耦；
    ///  · 保险丝二（播放绝对熔断 900 墙钟秒，可配）：构造读数起算的墙钟差（含暂停时长），
    ///    达到即熔、压倒一切（①未达成的病态长轴也熔）；差一拍不熔；
    ///  · 秒胜链：空时间轴首帧即全零直开；
    ///  · 投完时刻锁存：排空窗口从首次观测到 dispatchComplete 的 playhead 起算；
    ///  · 配置校验：两个时限非正数即拒（配置错误 fail fast）。
    /// 时钟读数全部由测试注入（纯 C#，EditMode 直测）。
    /// </summary>
    public sealed class DrainGateTests
    {
        private static DrainCounts Zeros => new DrainCounts();

        // ─────────────────────── 四条件与正常开闸 ───────────────────────

        [Test]
        public void Gate_AllClear_WhenDispatchCompleteAndAllCountsZero()
        {
            var gate = new BattleDrainGate(100f);
            var r = gate.Evaluate(100.1f, 12f, true, Zeros);
            Assert.AreEqual(DrainGateResult.OpenAllClear, r, "四条件齐备——正常开闸");
        }

        [Test]
        public void Gate_Waits_WhileStreamStillDispatching()
        {
            var gate = new BattleDrainGate(100f);
            // 计数全零但事件流未投完（战斗还在演）——条件①不满足，闸门必须关着
            var r = gate.Evaluate(100.1f, 5f, false, Zeros);
            Assert.AreEqual(DrainGateResult.Waiting, r);
        }

        [Test]
        public void Gate_Waits_OnEachNonZeroDrainCount()
        {
            // 六个计数逐项非零：任何一个画面尾巴没收，闸门都不开（②③④逐项锁）
            Assert.AreEqual(DrainGateResult.Waiting,
                new BattleDrainGate(100f).Evaluate(100.1f, 10f, true, new DrainCounts { BusyActors = 1 }),
                "② 动作在途演员非零");
            Assert.AreEqual(DrainGateResult.Waiting,
                new BattleDrainGate(100f).Evaluate(100.1f, 10f, true, new DrainCounts { AliveEffects = 1 }),
                "② 存活特效非零（含晚生特效）");
            Assert.AreEqual(DrainGateResult.Waiting,
                new BattleDrainGate(100f).Evaluate(100.1f, 10f, true, new DrainCounts { AliveFloats = 1 }),
                "② 在空飘字非零");
            Assert.AreEqual(DrainGateResult.Waiting,
                new BattleDrainGate(100f).Evaluate(100.1f, 10f, true, new DrainCounts { PendingVisualFires = 1 }),
                "② 已排未发火的演出条目非零");
            Assert.AreEqual(DrainGateResult.Waiting,
                new BattleDrainGate(100f).Evaluate(100.1f, 10f, true, new DrainCounts { InFlightProjectiles = 1 }),
                "③ 在途弹非零");
            Assert.AreEqual(DrainGateResult.Waiting,
                new BattleDrainGate(100f).Evaluate(100.1f, 10f, true, new DrainCounts { PendingDeaths = 1 }),
                "④ 倒地动画未播完");
        }

        [Test]
        public void Gate_NormalDrain_OpensWhenTailClears()
        {
            // 正常排空：投完（playhead 10）→ 倒地还在演（Waiting）→ 1.3 表演秒后画面静默 → 开闸
            var gate = new BattleDrainGate(100f);
            Assert.AreEqual(DrainGateResult.Waiting,
                gate.Evaluate(100.1f, 10f, true, new DrainCounts { PendingDeaths = 2 }));
            Assert.AreEqual(DrainGateResult.OpenAllClear,
                gate.Evaluate(101f, 11.3f, true, Zeros), "尾巴 1.3 表演秒 < 5——正常收尾开闸");
        }

        [Test]
        public void Gate_EmptyBattle_AllClearAtOnce()
        {
            // 秒胜链：空时间轴首帧即投完+全零——闸门当帧直开（结算入口统一）
            var gate = new BattleDrainGate(100f);
            Assert.AreEqual(DrainGateResult.OpenAllClear, gate.Evaluate(100.01f, 0f, true, Zeros));
        }

        // ─────────────────────── 保险丝一：排空超时（5 表演秒） ───────────────────────

        [Test]
        public void Gate_DrainTimeout_TripsAfterFivePlayheadSeconds()
        {
            var gate = new BattleDrainGate(100f);
            Assert.AreEqual(DrainGateResult.Waiting,
                gate.Evaluate(100.1f, 10f, true, new DrainCounts { InFlightProjectiles = 1 }),
                "排空窗口起算=投完观测时刻（playhead 10）");
            Assert.AreEqual(DrainGateResult.OpenDrainTimeout,
                gate.Evaluate(100.2f, 15.1f, true, new DrainCounts { InFlightProjectiles = 1 }),
                "playhead 走过 5.1 表演秒仍未收——保险丝一熔断");
            Assert.IsTrue(gate.DispatchSeen, "投完已观测");
        }

        [Test]
        public void Gate_DrainTimeout_BoundaryExactlyFive_Waits()
        {
            var gate = new BattleDrainGate(100f);
            gate.Evaluate(100.1f, 10f, true, new DrainCounts { PendingDeaths = 1 });
            Assert.AreEqual(DrainGateResult.Waiting,
                gate.Evaluate(100.2f, 15f, true, new DrainCounts { PendingDeaths = 1 }),
                "恰好 5 表演秒不算超（严格大于才熔）");
        }

        [Test]
        public void Gate_DrainTimeout_FrozenPlayhead_NeverTrips()
        {
            // 暂停语义：playhead 冻结（速率=0），排空窗口不推进——闸门不误伤
            // （墙钟走多久都不熔；真卡死由保险丝二兜底）
            var gate = new BattleDrainGate(100f);
            gate.Evaluate(100.1f, 10f, true, new DrainCounts { AliveEffects = 1 });
            Assert.AreEqual(DrainGateResult.Waiting,
                gate.Evaluate(700f, 10f, true, new DrainCounts { AliveEffects = 1 }),
                "暂停 600 墙钟秒：playhead 未动，保险丝一不该熔（<900 保险丝二也不到）");
        }

        // ─────────────────────── 保险丝二：播放绝对熔断（900 墙钟秒） ───────────────────────

        [Test]
        public void Gate_AbsoluteTimeout_TripsAtWallCap_EvenBeforeDispatch()
        {
            var gate = new BattleDrainGate(100f);
            Assert.AreEqual(DrainGateResult.OpenAbsoluteTimeout,
                gate.Evaluate(1000f, 3f, false, Zeros),
                "墙钟到 900 即熔——病态长轴（①永远不达成）也兜得住");
        }

        [Test]
        public void Gate_AbsoluteTimeout_BoundaryJustBelow_Waits()
        {
            var gate = new BattleDrainGate(100f);
            Assert.AreEqual(DrainGateResult.Waiting,
                gate.Evaluate(999.9f, 3f, false, Zeros),
                "899.9 墙钟秒未到上限（达到即熔的边界锁）");
        }

        [Test]
        public void Gate_AbsoluteTimeout_DominatesDrainTimeout()
        {
            // 两根保险丝同时满足熔断条件：绝对熔断优先（先判——压倒一切）
            var gate = new BattleDrainGate(100f);
            gate.Evaluate(100.1f, 10f, true, new DrainCounts { AliveEffects = 1 });
            Assert.AreEqual(DrainGateResult.OpenAbsoluteTimeout,
                gate.Evaluate(1000f, 20f, true, new DrainCounts { AliveEffects = 1 }),
                "排空超时与绝对熔断同时到——报绝对熔断");
        }

        // ─────────────────────── 投完时刻锁存与配置校验 ───────────────────────

        [Test]
        public void Gate_DispatchMomentLatchedAtFirstObservation()
        {
            // 排空窗口从「首次观测到投完」的 playhead 锁存（评估可能隔帧才来——不重算）
            var gate = new BattleDrainGate(100f);
            Assert.AreEqual(DrainGateResult.Waiting,
                gate.Evaluate(101f, 8f, true, new DrainCounts { PendingDeaths = 1 }));
            Assert.AreEqual(DrainGateResult.OpenDrainTimeout,
                gate.Evaluate(101.1f, 13.05f, true, new DrainCounts { PendingDeaths = 1 }),
                "窗口按锁存的 playhead 8 起算（13.05-8=5.05 > 5）");
        }

        [Test]
        public void Gate_NonPositiveTimeoutConfig_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new BattleDrainGate(0f, 0f),
                "排空超时=0 非法");
            Assert.Throws<ArgumentOutOfRangeException>(() => new BattleDrainGate(0f, -1f),
                "排空超时为负非法");
            Assert.Throws<ArgumentOutOfRangeException>(() => new BattleDrainGate(0f, 5f, 0f),
                "绝对熔断=0 非法");
            Assert.Throws<ArgumentOutOfRangeException>(() => new BattleDrainGate(0f, 5f, -900f),
                "绝对熔断为负非法");
        }

        [Test]
        public void Gate_Defaults_UserSpecifiedFuseNumbers()
        {
            // 用户定的数（2026-10-19 #23 拍板）：排空约 5 秒 / 播放绝对熔断 15 分钟——默认值锁死
            Assert.AreEqual(5f, BattleDrainGate.DefaultDrainTimeoutSeconds);
            Assert.AreEqual(900f, BattleDrainGate.DefaultAbsoluteTimeoutSeconds, "15 分钟");
        }
    }
}
