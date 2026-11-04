using System;
using System.Collections.Generic;
using AutoChess.Game;
using AutoChess.Presentation;
using NUnit.Framework;

namespace AutoChess.Tests
{
    /// <summary>
    /// 阶段4 批次B1：规划器验收（#23③——事件流 → 全局交付时间轴；三不变式 + 守约对齐性质）。
    ///
    /// 分节契约：
    ///  · 守约对齐：资产守约（动作时长≤对应动作间隔）时出手全落逻辑时刻——零等待零漂移
    ///    （"零等待"按 1e-6 秒容差断言：浮点尾数级偏差视为零，真实等待是毫秒量级、泾渭分明；
    ///    违约时等待沿 busy 账本级联增长、恒不早于逻辑；
    ///  · I1 同单位串行：移动没走完攻击不开拍；死亡可打断自身动作（封口）、死后无动作；
    ///  · I2 同目标交付序：不同前摇的后手攻击被预扣到先手之后（倒置/打尸体的构造性杜绝），
    ///    落点仍在自己挥击窗内；Damage/Heal/Mana 全走同一接收者链；数值事件不占动作
    ///    （吃延迟闪白的单位开拍不推迟——跨单位零级联）；
    ///  · I3+I4 占位互斥：击杀链倒地起=致死交付、让格=倒地完成、后继者到达≥让格时刻；
    ///    DoT 致死无让格节点（该格永不释放的镜像前提）；
    ///  · 伤害配对：普攻=紧邻下一条（落命中帧）；施法多目标同一命中时刻、块内
    ///    Died/Removed/Healed(施法者) 插花不破坏后续配对、清蓝终块后同源伤害不误配；
    ///    独立伤害（DoT/灼烧）落逻辑时刻；
    ///  · 矿石：按死亡格链到倒地开始+Gap；无记录（理论不可达）落逻辑时刻；
    ///  · 确定性：同流同表两算（同实例/异实例）逐节点全等；同刻多节点按 Seq 稳定排序；
    ///  · 真战：覆盖场景（PreviewTests 同款，一场打满 11 类事件）全时间轴过六道检查器——
    ///    不早于逻辑 / I1 串行 / I2 链严格 / 普攻落点=Start+命中偏移且在挥击窗内 /
    ///    占位区间逐格互斥（从 tick0 快照播种重建） / 条数=流条数+节点种类全覆盖；
    ///    DoT 真战尸体持格到 Makespan。
    ///
    /// 合成流经 InternalsVisibleTo 直构（Presentation/AssemblyInfo.cs）——
    /// 违规场景在真实逻辑里构造不出来，那正是要证明的"构造上不可能"。
    ///
    /// ★浮点实战坑（2026-11-03 本批踩实，后续表现层测试沿用）：断言期望值禁止写
    /// `10 * Ts` 这类"字面量 × 静态字段"——编译器按常量折叠算出未舍入的双精度值，
    /// 与规划器运行时 int×const float 的单精度乘法差 1 个尾数（约 1e-8 秒），
    /// 0 容差断言必红。一律走 L(tick) 帮助方法（运行时乘法，与规划器逐位相同），
    /// 且"测试侧算术 vs 规划器输出"的相等断言统一 1e-6f 容差；规划器输出互比
    /// （同两个 float）才允许精确断言。
    /// 场景表组与 PreviewTests 同款拷贝（各测试文件自持底座，互不引用）。
    /// </summary>
    [TestFixture]
    public class PlannerTests
    {
        private const uint SeedA = 20261103u;

        // ─────────────────────── 合成流底座 ───────────────────────

        private static BattleEventStream NewStream() => new BattleEventStream(64);

        /// <summary>逻辑时刻：与规划器同表达式（运行时 int × TickSeconds）——
        /// 见类注释"浮点实战坑"，测试内禁止直接写 `N * Ts` 字面量折叠形态。</summary>
        private static float L(int tick) => tick * GameConst.TickSeconds;

        private static void AddItem(BattleEventStream s, StreamEventKind kind, int tick,
            int idA = 0, int idB = 0, int idC = 0, int v0 = 0, int v1 = 0,
            byte cx = 0, byte cy = 0, byte tx = 0, byte ty = 0)
        {
            s.Add(new StreamItem
            {
                Kind = kind, Tick = tick, Seq = s.Count,
                IdA = idA, IdB = idB, IdC = idC, V0 = v0, V1 = v1,
                CellX = cx, CellY = cy, ToX = tx, ToY = ty,
            });
        }

        /// <summary>时刻数据（字段序=攻击时长/攻击命中帧/施法时长/施法命中帧/移动/倒地）。</summary>
        private static UnitTiming T(float atk, float atkHit, float cast, float castHit, float move, float death)
            => new UnitTiming
            {
                AttackDuration = atk, AttackHitPercent = atkHit,
                CastDuration = cast, CastHitPercent = castHit,
                MoveDuration = move, DeathDuration = death,
            };

        /// <summary>命中偏移（与规划器同表达式：百分比×时长）。</summary>
        private static float Hit(float duration, float percent) => percent * duration;

        private static PlannerTimingTable Table(UnitTiming all) => new PlannerTimingTable(all);

        private static PlannedTimeline Plan(BattleEventStream s, PlannerTimingTable t) => new BattlePlanner().Plan(s, t);

        private static PlannedNode BySeq(PlannedTimeline tl, int seq)
        {
            for (int i = 0; i < tl.Count; i++)
                if (tl[i].Seq == seq) return tl[i];
            Assert.Fail($"Seq {seq} 不在时间轴上");
            return default;
        }

        private static List<PlannedNode> All(PlannedTimeline tl, PlannedNodeKind kind)
        {
            var list = new List<PlannedNode>();
            for (int i = 0; i < tl.Count; i++)
                if (tl[i].Kind == kind) list.Add(tl[i]);
            return list;
        }

        // ─────────────────────── ① 守约对齐性质 ───────────────────────

        [Test]
        public void Plan_CompliantAttackChain_ZeroWaitExactStarts()
        {
            // 守约：攻击动画 14 tick < 15 tick 间隔——所有约束不绑定，出手全落逻辑时刻
            var s = NewStream();
            AddItem(s, StreamEventKind.UnitAttacked, 10, 1, 2);
            AddItem(s, StreamEventKind.UnitDamaged, 10, 2, 1, 30, 70);
            AddItem(s, StreamEventKind.UnitAttacked, 25, 1, 2);
            AddItem(s, StreamEventKind.UnitDamaged, 25, 2, 1, 30, 40);
            AddItem(s, StreamEventKind.UnitAttacked, 40, 1, 2);
            AddItem(s, StreamEventKind.UnitDamaged, 40, 2, 1, 40, 0);
            AddItem(s, StreamEventKind.BattleEnded, 41, v0: 1, v1: 41);

            float dur = L(14);
            var tl = Plan(s, Table(T(dur, 0.5f, dur, 0.5f, L(9), L(12))));
            Assert.AreEqual(7, tl.Count, "一一流条目一节点");

            var a1 = BySeq(tl, 0); var a2 = BySeq(tl, 2); var a3 = BySeq(tl, 4);
            Assert.AreEqual(L(10), a1.Start, 1e-6f, "首击落逻辑时刻（零等待）");
            Assert.AreEqual(L(25), a2.Start, 1e-6f, "第二击落逻辑时刻——零等待零漂移");
            Assert.AreEqual(L(40), a3.Start, 1e-6f, "第三击落逻辑时刻");

            var d1 = BySeq(tl, 1); var d2 = BySeq(tl, 3); var d3 = BySeq(tl, 5);
            Assert.AreEqual(L(10) + Hit(dur, 0.5f), d1.Start, 1e-6f, "配对伤害落命中帧");
            Assert.AreEqual(L(25) + Hit(dur, 0.5f), d2.Start, 1e-6f);
            Assert.AreEqual(L(40) + Hit(dur, 0.5f), d3.Start, 1e-6f);
            Assert.Greater(d3.Start, d2.Start, "同目标交付按逻辑序递增");

            Assert.AreEqual(L(40) + dur, tl.Makespan, 1e-6f, "总长=逻辑时刻+末动作时长（零等待=不欠账）");
        }

        [Test]
        public void Plan_BoundaryFit_NoVisibleDrift()
        {
            // 边界守约：动画时长 == 攻击间隔（15 tick）——busy 与逻辑时刻的浮点尾数竞争（±1 ulp），
            // 工程口径"零漂移"= Start 偏差 ≤ 1e-6 秒
            var s = NewStream();
            AddItem(s, StreamEventKind.UnitAttacked, 10, 1, 2);
            AddItem(s, StreamEventKind.UnitDamaged, 10, 2, 1, 30, 70);
            AddItem(s, StreamEventKind.UnitAttacked, 25, 1, 2);
            AddItem(s, StreamEventKind.UnitDamaged, 25, 2, 1, 30, 40);

            float dur = L(15);
            var tl = Plan(s, Table(T(dur, 0.5f, dur, 0.5f, L(9), L(12))));
            var a1 = BySeq(tl, 0); var a2 = BySeq(tl, 2);
            Assert.AreEqual(L(10), a1.Start, 1e-6f, "首击无竞争");
            Assert.AreEqual(L(25), a2.Start, 1e-6f, "边界守约零可见漂移");
            Assert.GreaterOrEqual(a2.Start, a1.Start + a1.Duration - 1e-6f, "I1 串行在边界仍成立");
            Assert.Greater(BySeq(tl, 3).Start, BySeq(tl, 1).Start, "同目标交付严格递增");
        }

        [Test]
        public void Plan_OverlongAttack_CascadeWaitsGrow()
        {
            // 违约：动画 24 tick > 15 tick 间隔——出手沿 busy 账本逐次后移，等待级联增长
            var s = NewStream();
            for (int k = 0; k < 3; k++)
            {
                int tick = 10 + 15 * k;
                AddItem(s, StreamEventKind.UnitAttacked, tick, 1, 2);
                AddItem(s, StreamEventKind.UnitDamaged, tick, 2, 1, 30, 70);
            }
            float dur = L(24);
            var tl = Plan(s, Table(T(dur, 0.5f, dur, 0.5f, L(9), L(12))));
            var a1 = BySeq(tl, 0); var a2 = BySeq(tl, 2); var a3 = BySeq(tl, 4);

            Assert.AreEqual(L(10), a1.Start, 1e-6f, "首击落逻辑时刻");
            Assert.AreEqual(a1.Start + dur, a2.Start, 1e-6f, "第二击被 busy 推到第一击收招（零空转串行）");
            Assert.AreEqual(a2.Start + dur, a3.Start, 1e-6f, "第三击继续顺延");
            float wait2 = a2.Start - L(25), wait3 = a3.Start - L(40);
            Assert.Greater(wait3, wait2, "等待沿级联传播增长（违约的代价累积）");
            Assert.GreaterOrEqual(a2.Start, L(25), "恒不早于逻辑（第 2 击）");
            Assert.GreaterOrEqual(a3.Start, L(40), "恒不早于逻辑（第 3 击）");
            Assert.GreaterOrEqual(a3.Start, a2.Start + a2.Duration - 1e-6f, "I1 在违约下仍焊死");
        }

        // ─────────────────────── ② I1 同单位串行 ───────────────────────

        [Test]
        public void Plan_SameUnitActionSerial_MoveBlocksAttack()
        {
            // I1：攻击（tick10，动画 15 tick）→ 移动（tick12 逻辑）→ 攻击（tick14 逻辑）——
            // 移动等攻击收招、第二击等移动走完（"移动播完才攻击"的机制本身）
            var s = NewStream();
            AddItem(s, StreamEventKind.UnitAttacked, 10, 1, 2);
            AddItem(s, StreamEventKind.UnitDamaged, 10, 2, 1, 30, 70);
            AddItem(s, StreamEventKind.UnitMoved, 12, 1, cx: 4, cy: 2, tx: 5, ty: 2);
            AddItem(s, StreamEventKind.UnitAttacked, 14, 1, 2);
            AddItem(s, StreamEventKind.UnitDamaged, 14, 2, 1, 30, 40);

            float atk = L(15); float move = L(9);
            var tl = Plan(s, Table(T(atk, 0.5f, atk, 0.5f, move, L(12))));
            var mv = BySeq(tl, 2); var a2 = BySeq(tl, 3);

            Assert.AreEqual(L(10) + atk, mv.Start, 1e-6f, "移动等攻击收招（busy 串行）");
            Assert.AreEqual(mv.Start + move, a2.Start, 1e-6f, "攻击等移动走完——移动播完才攻击");
            Assert.AreEqual(a2.Start + Hit(atk, 0.5f), BySeq(tl, 4).Start, 1e-6f, "配对伤害落命中帧");
        }

        [Test]
        public void Plan_DeathInterruptsOwnAction_FallAtKillingBlow()
        {
            // U 先出手（动画 15 tick），下一 tick 被 T 反杀——倒地起于 T 的命中交付，
            // 早于 U 自己的收招（死亡打断自身动作=封口）
            var s = NewStream();
            AddItem(s, StreamEventKind.UnitAttacked, 10, 1, 2);   // U 攻 T
            AddItem(s, StreamEventKind.UnitDamaged, 10, 2, 1, 30, 70);
            AddItem(s, StreamEventKind.UnitAttacked, 11, 2, 1);   // T 反杀 U
            AddItem(s, StreamEventKind.UnitDamaged, 11, 1, 2, 70, 0);
            AddItem(s, StreamEventKind.UnitDied, 11, 1, cx: 3, cy: 2);
            AddItem(s, StreamEventKind.UnitRemoved, 11, 1, cx: 3, cy: 2);

            float dur = L(15);
            var tl = Plan(s, Table(T(dur, 0.5f, dur, 0.5f, L(9), L(12))));
            var uAtk = BySeq(tl, 0); var kill = BySeq(tl, 3); var die = BySeq(tl, 4);

            Assert.AreEqual(L(11) + Hit(dur, 0.5f), kill.Start, 1e-6f, "致死伤害落 T 的命中帧");
            Assert.AreEqual(kill.Start, die.Start, "倒地起于致死包裹送达（规划器输出互比=精确）");
            Assert.Less(die.Start, uAtk.Start + dur, "死亡打断自身动作——倒地早于自身收招（封口）");
        }

        // ─────────────────────── ③ I2 同目标交付序 ───────────────────────

        [Test]
        public void Plan_DifferentWindups_NoInversionOnSameTarget()
        {
            // I2 主验算：A 命中帧 90%（前摇长）、B 命中帧 10%（前摇短）——逻辑上 B 后手，
            // 不设防的世界 B 反而先见血（倒置/打尸体）。规划器预扣后：B 开拍推迟、落点排在 A 后
            var s = NewStream();
            AddItem(s, StreamEventKind.UnitAttacked, 10, 1, 3);   // A 攻 T
            AddItem(s, StreamEventKind.UnitDamaged, 10, 3, 1, 30, 70);
            AddItem(s, StreamEventKind.UnitAttacked, 11, 2, 3);   // B 攻 T（逻辑后手）
            AddItem(s, StreamEventKind.UnitDamaged, 11, 3, 2, 30, 40);

            float dur = L(15);
            var table = new PlannerTimingTable(T(dur, 0.9f, dur, 0.5f, L(9), L(12)));
            table.Set(2, T(dur, 0.1f, dur, 0.5f, L(9), L(12)));   // B 单位覆盖：短前摇
            var tl = Plan(s, table);

            var d1 = BySeq(tl, 1); var a2 = BySeq(tl, 2); var d2 = BySeq(tl, 3);
            Assert.Greater(d2.Start, d1.Start, "后手 B 的伤害必然排在先手 A 之后——无倒置无打尸体");
            Assert.AreEqual(d1.Start + BattlePlanner.DeliveryGap, d2.Start, 1e-6f, "落点钳在链后第一位");
            float offsetB = Hit(dur, 0.1f);
            Assert.AreEqual(d1.Start + BattlePlanner.DeliveryGap - offsetB, a2.Start, 1e-6f,
                "B 的开拍被预扣=链后第一位减命中偏移（先顿一下再挥）");
            Assert.GreaterOrEqual(d2.Start, a2.Start, "落点不早于开拍");
            Assert.LessOrEqual(d2.Start, a2.Start + dur + 1e-6f, "落点仍在 B 自己的挥击窗内");
        }

        [Test]
        public void Plan_ValueEvents_ChainPerReceiver()
        {
            // Damage→Heal→Mana 走同一接收者链、严格递增——I2 对全部数值事件生效
            var s = NewStream();
            AddItem(s, StreamEventKind.UnitAttacked, 10, 1, 3);
            AddItem(s, StreamEventKind.UnitDamaged, 10, 3, 1, 40, 60);
            AddItem(s, StreamEventKind.UnitHealed, 11, 3, v0: 5, v1: 65);
            AddItem(s, StreamEventKind.ManaChanged, 12, 3, v0: 10, v1: 30);

            float dur = L(15);
            var tl = Plan(s, Table(T(dur, 0.9f, dur, 0.5f, L(9), L(12))));
            var d = BySeq(tl, 1); var h = BySeq(tl, 2); var m = BySeq(tl, 3);

            Assert.AreEqual(L(10) + Hit(dur, 0.9f), d.Start, 1e-6f, "长前摇的命中晚交付");
            Assert.AreEqual(d.Start + BattlePlanner.DeliveryGap, h.Start, 1e-6f,
                "回血被命中拖到链后（同目标序优先于准时）");
            Assert.AreEqual(h.Start + BattlePlanner.DeliveryGap, m.Start, 1e-6f, "蓝条接续递增");
            Assert.Greater(h.Start, L(11), "回血晚于自身逻辑时刻——链约束生效");
        }

        [Test]
        public void Plan_ActorNotDelayedByIncomingDelivery()
        {
            // 数值事件不占动作：T 吃了 A 的长前摇延迟闪白，T 自己的出手不推迟（跨单位零级联）
            var s = NewStream();
            AddItem(s, StreamEventKind.UnitAttacked, 10, 1, 3);
            AddItem(s, StreamEventKind.UnitDamaged, 10, 3, 1, 40, 60);
            AddItem(s, StreamEventKind.UnitAttacked, 11, 3, 4);   // T 反击（逻辑下一 tick）
            AddItem(s, StreamEventKind.UnitDamaged, 11, 4, 3, 40, 60);

            float dur = L(15);
            var tl = Plan(s, Table(T(dur, 0.9f, dur, 0.5f, L(9), L(12))));
            Assert.AreEqual(L(11), BySeq(tl, 2).Start, 1e-6f, "T 的开拍不被收到的延迟交付拖住");
            Assert.Greater(BySeq(tl, 1).Start, L(11), "场景成立前提：A 的命中交付确实晚于 T 的逻辑出手");
        }

        // ─────────────────────── ④ I3+I4 占位互斥 ───────────────────────

        [Test]
        public void Plan_KillChain_FallAtDelivery_CellFreedAtFallEnd()
        {
            // 击杀链：倒地起=致死交付、让格=倒地完成；B 走进尸体格的到达 ≥ 让格时刻
            var s = NewStream();
            AddItem(s, StreamEventKind.UnitAttacked, 10, 1, 3);
            AddItem(s, StreamEventKind.UnitDamaged, 10, 3, 1, 99, 0);
            AddItem(s, StreamEventKind.UnitDied, 10, 3, cx: 2, cy: 4);
            AddItem(s, StreamEventKind.UnitRemoved, 10, 3, cx: 2, cy: 4);
            AddItem(s, StreamEventKind.UnitMoved, 11, 2, cx: 2, cy: 3, tx: 2, ty: 4);   // B 走进尸体格
            AddItem(s, StreamEventKind.BattleEnded, 12, v0: 1, v1: 12);

            float dur = L(15); float death = L(12); float move = L(9);
            var tl = Plan(s, Table(T(dur, 0.5f, dur, 0.5f, move, death)));
            var dmg = BySeq(tl, 1); var die = BySeq(tl, 2); var rem = BySeq(tl, 3); var mv = BySeq(tl, 4);

            Assert.AreEqual(dmg.Start, die.Start, "倒地起于致死包裹送达（规划器输出互比=精确）");
            Assert.AreEqual(die.Start + death, rem.Start, 1e-6f, "让格=倒地完成时刻（倒地前格不让位）");
            Assert.AreEqual(rem.Start, mv.Start + move, 1e-6f, "B 的到达恰在让格时刻（等满才进——踩尸体格被构造性杜绝）");
            Assert.Greater(mv.Start, L(11), "B 的开拍晚于自身逻辑时刻（被占位账本推迟）");
        }

        [Test]
        public void Plan_DoTDeath_NoRemoveNode()
        {
            // DoT 致死：无 Removed → 无让格节点（"该格永不释放"的镜像前提：占位账本没有写者）
            var s = NewStream();
            AddItem(s, StreamEventKind.UnitDamaged, 30, 3, 7, 2, 0);   // 独立伤害（DoT，来源 7）
            AddItem(s, StreamEventKind.UnitDied, 30, 3, cx: 2, cy: 4);
            AddItem(s, StreamEventKind.BattleEnded, 31, v0: 1, v1: 31);

            var tl = Plan(s, Table(T(L(15), 0.5f, L(15), 0.5f, L(9), L(12))));
            Assert.AreEqual(0, All(tl, PlannedNodeKind.Remove).Count, "DoT 尸体没有让格节点");
            Assert.AreEqual(L(30), BySeq(tl, 1).Start, 1e-6f,
                "无在途包裹：倒地立即（落该 DoT 伤害的逻辑交付时刻）");
        }

        // ─────────────────────── ⑤ 伤害配对 ───────────────────────

        [Test]
        public void Plan_CastMultiTarget_SingleHitMoment()
        {
            // 施法多目标：全部配对到同一命中时刻；清蓝（ManaChanged）后同源伤害不再误配旧块
            var s = NewStream();
            AddItem(s, StreamEventKind.SkillCast, 20, 5, 90, 3, v0: 1);   // C(5) 施技能 90、主目标 T(3)
            AddItem(s, StreamEventKind.UnitDamaged, 20, 3, 5, 30, 70);
            AddItem(s, StreamEventKind.UnitDamaged, 20, 4, 5, 30, 70);
            AddItem(s, StreamEventKind.ManaChanged, 20, 5, v0: 0, v1: 40);   // 清蓝=块终结
            AddItem(s, StreamEventKind.UnitDamaged, 50, 4, 5, 10, 60);       // C 的灼烧光环（tick 末独立伤害）

            float cast = L(18);
            var tl = Plan(s, Table(T(L(15), 0.5f, cast, 0.5f, L(9), L(12))));
            float hit = L(20) + Hit(cast, 0.5f);
            Assert.AreEqual(L(20), BySeq(tl, 0).Start, 1e-6f, "施法出手落逻辑时刻");
            Assert.AreEqual(hit, BySeq(tl, 1).Start, 1e-6f, "主目标配对命中时刻");
            Assert.AreEqual(hit, BySeq(tl, 2).Start, 1e-6f, "第二目标同一命中时刻（一次施法一个命中时刻）");
            Assert.AreEqual(L(50), BySeq(tl, 4).Start, 1e-6f, "清蓝终块后同源伤害走独立路径（落逻辑时刻，不误配旧块）");
        }

        [Test]
        public void Plan_CastKillInterleaves_PairingSurvives()
        {
            // 块内插花（Died/Removed/Healed(施法者)=击杀回复）不清块——后续目标仍配对同一命中时刻。
            // Healed 按排水直设落逻辑时刻（早于命中画面=已知边界，本条就地锁形）
            var s = NewStream();
            AddItem(s, StreamEventKind.SkillCast, 20, 5, 90, 3);
            AddItem(s, StreamEventKind.UnitDamaged, 20, 3, 5, 99, 0);
            AddItem(s, StreamEventKind.UnitDied, 20, 3, cx: 1, cy: 4);
            AddItem(s, StreamEventKind.UnitHealed, 20, 5, v0: 10, v1: 60);
            AddItem(s, StreamEventKind.UnitRemoved, 20, 3, cx: 1, cy: 4);
            AddItem(s, StreamEventKind.UnitDamaged, 20, 4, 5, 30, 70);   // 第二目标
            AddItem(s, StreamEventKind.ManaChanged, 20, 5, v0: 0, v1: 40);

            float cast = L(18); float death = L(12);
            var tl = Plan(s, Table(T(L(15), 0.5f, cast, 0.5f, L(9), death)));
            float hit = L(20) + Hit(cast, 0.5f);
            Assert.AreEqual(hit, BySeq(tl, 1).Start, 1e-6f, "主目标");
            Assert.AreEqual(hit, BySeq(tl, 5).Start, 1e-6f, "插花后第二目标仍配对同一命中时刻");
            Assert.AreEqual(hit, BySeq(tl, 2).Start, 1e-6f, "倒地起于致死交付（施法命中）");
            Assert.AreEqual(hit + death, BySeq(tl, 4).Start, 1e-6f, "让格=倒地完成");
            Assert.AreEqual(L(20), BySeq(tl, 3).Start, 1e-6f, "击杀回复按排水直设落逻辑时刻（已知边界就地锁形）");
        }

        [Test]
        public void Plan_UnpairedDamage_DeliversAtLogicTime()
        {
            // 独立伤害（DoT/灼烧等 tick 末来源）：落逻辑时刻；同 tick 同目标第二笔按链+Gap 递增
            var s = NewStream();
            AddItem(s, StreamEventKind.UnitDamaged, 20, 3, 7, 2, 98);
            AddItem(s, StreamEventKind.UnitDamaged, 20, 3, 7, 2, 96);
            var tl = Plan(s, Table(T(L(15), 0.5f, L(15), 0.5f, L(9), L(12))));
            Assert.AreEqual(L(20), BySeq(tl, 0).Start, 1e-6f, "独立伤害落逻辑时刻");
            Assert.AreEqual(L(20) + BattlePlanner.DeliveryGap, BySeq(tl, 1).Start, 1e-6f, "链上第二笔按 Gap 递增");
        }

        // ─────────────────────── ⑥ 矿石 / 终局 / 边界形态 ───────────────────────

        [Test]
        public void Plan_OreDrop_ChainedToDeathAtSameCell()
        {
            var s = NewStream();
            AddItem(s, StreamEventKind.UnitAttacked, 10, 1, 3);
            AddItem(s, StreamEventKind.UnitDamaged, 10, 3, 1, 99, 0);
            AddItem(s, StreamEventKind.UnitDied, 10, 3, cx: 2, cy: 4);
            AddItem(s, StreamEventKind.OreDropped, 10, idA: 77, v0: 1, cx: 2, cy: 4);
            var tl = Plan(s, Table(T(L(15), 0.5f, L(15), 0.5f, L(9), L(12))));
            Assert.AreEqual(BySeq(tl, 2).Start + BattlePlanner.DeliveryGap, BySeq(tl, 3).Start, 1e-6f,
                "掉矿链到死亡格的倒地开始之后（随击杀包裹闪现）");

            // 防御：无死亡记录的格子（理论不可达）落逻辑时刻
            var s2 = NewStream();
            AddItem(s2, StreamEventKind.OreDropped, 9, idA: 78, v0: 2, cx: 0, cy: 5);
            var tl2 = Plan(s2, Table(T(L(15), 0.5f, L(15), 0.5f, L(9), L(12))));
            Assert.AreEqual(L(9), tl2[0].Start, 1e-6f);
        }

        [Test]
        public void Plan_BattleEndNode_AtLogicalTime()
        {
            var s = NewStream();
            AddItem(s, StreamEventKind.BattleEnded, 100, v0: 1, v1: 100);
            var tl = Plan(s, Table(T(L(15), 0.5f, L(15), 0.5f, L(9), L(12))));
            Assert.AreEqual(1, tl.Count);
            var n = tl[0];
            Assert.AreEqual(PlannedNodeKind.BattleEnd, n.Kind);
            Assert.AreEqual(L(100), n.Start, 1e-6f, "终局标记落逻辑时刻");
            Assert.AreEqual(1, n.V0);
            Assert.AreEqual(100, n.V1);
            Assert.AreEqual(L(100), tl.Makespan, 1e-6f, "BattleEnd 时长 0——Makespan=终局时刻");
        }

        [Test]
        public void Plan_EmptyStream_EmptyTimeline()
        {
            var tl = Plan(NewStream(), Table(T(L(15), 0.5f, L(15), 0.5f, L(9), L(12))));
            Assert.AreEqual(0, tl.Count, "秒胜链：空流=空时间轴");
            Assert.AreEqual(0f, tl.Makespan);
        }

        [Test]
        public void Plan_SameInputTwice_IdenticalTimeline()
        {
            var s = BuildMediumStream();
            var table = Table(T(L(15), 0.5f, L(18), 0.5f, L(9), L(12)));
            var planner = new BattlePlanner();
            var a = planner.Plan(s, table);
            var b = planner.Plan(s, table);                  // 同实例复算
            var c = new BattlePlanner().Plan(s, table);      // 异实例各算
            Assert.AreEqual(a.Count, b.Count);
            Assert.AreEqual(a.Count, c.Count);
            for (int i = 0; i < a.Count; i++)
            {
                Assert.IsTrue(a[i].Equals(b[i]), $"同实例两算第 {i} 节点漂移");
                Assert.IsTrue(a[i].Equals(c[i]), $"异实例两算第 {i} 节点漂移");
            }
            Assert.AreEqual(a.Makespan, c.Makespan);
        }

        [Test]
        public void Plan_TiesSortedBySeq()
        {
            // 同 tick 两个单位先后出手（合法流形：出手与伤害同步相邻）：同刻开拍、同刻命中，
            // 时间轴同刻按 Seq 稳定排列（跨目标同刻合法=AOE 同帧的排序基础）
            var s = NewStream();
            AddItem(s, StreamEventKind.UnitAttacked, 10, 1, 3);
            AddItem(s, StreamEventKind.UnitDamaged, 10, 3, 1, 10, 90);
            AddItem(s, StreamEventKind.UnitAttacked, 10, 2, 4);
            AddItem(s, StreamEventKind.UnitDamaged, 10, 4, 2, 10, 90);
            float dur = L(15);
            var tl = Plan(s, Table(T(dur, 0.5f, dur, 0.5f, L(9), L(12))));
            Assert.AreEqual(4, tl.Count);
            Assert.AreEqual(L(10), tl[0].Start, 1e-6f);
            Assert.AreEqual(0, tl[0].Seq, "先出手者先开拍（Seq 排序）");
            Assert.AreEqual(L(10), tl[1].Start, 1e-6f);
            Assert.AreEqual(2, tl[1].Seq, "同刻开拍按 Seq 排");
            Assert.AreEqual(L(10) + Hit(dur, 0.5f), tl[2].Start, 1e-6f);
            Assert.AreEqual(1, tl[2].Seq);
            Assert.AreEqual(L(10) + Hit(dur, 0.5f), tl[3].Start, 1e-6f);
            Assert.AreEqual(3, tl[3].Seq, "同刻命中按 Seq 排");
        }

        private static BattleEventStream BuildMediumStream()
        {
            var s = NewStream();
            AddItem(s, StreamEventKind.UnitAttacked, 10, 1, 3);
            AddItem(s, StreamEventKind.UnitDamaged, 10, 3, 1, 40, 60);
            AddItem(s, StreamEventKind.UnitMoved, 12, 3, cx: 2, cy: 4, tx: 3, ty: 4);
            AddItem(s, StreamEventKind.SkillCast, 20, 3, 90, 1);
            AddItem(s, StreamEventKind.UnitDamaged, 20, 1, 3, 55, 5);
            AddItem(s, StreamEventKind.UnitDied, 20, 1, cx: 4, cy: 2);
            AddItem(s, StreamEventKind.UnitRemoved, 20, 1, cx: 4, cy: 2);
            AddItem(s, StreamEventKind.OreDropped, 20, idA: 77, v0: 1, cx: 4, cy: 2);
            AddItem(s, StreamEventKind.ManaChanged, 20, 3, v0: 0, v1: 30);
            AddItem(s, StreamEventKind.BattleEnded, 21, v0: 1, v1: 21);
            return s;
        }

        // ─────────────────────── ⑦ 真战：全时间轴不变式检查器 ───────────────────────

        [Test]
        public void Plan_RealCoverageBattle_AllInvariantsHold()
        {
            var (s, r) = RunCoveragePreview(SeedA);
            var timings = RealTable(s);
            var tl = new BattlePlanner().Plan(r.Stream, timings);

            Assert.Greater(tl.Count, 60, "事件规模下限（对齐 PreviewTests 覆盖场景的规模验收）");

            // 形状：条数=流条数、Seq 全覆盖、排序键单调、节点种类全覆盖
            Assert.AreEqual(r.Stream.Count, tl.Count, "一一流条目一节点");
            var seqSeen = new HashSet<int>();
            var kinds = new HashSet<PlannedNodeKind>();
            for (int i = 0; i < tl.Count; i++)
            {
                Assert.IsTrue(seqSeen.Add(tl[i].Seq), $"Seq {tl[i].Seq} 重复");
                kinds.Add(tl[i].Kind);
                if (i > 0)
                {
                    Assert.LessOrEqual(tl[i - 1].Start, tl[i].Start, "时间轴按 Start 单调");
                    if (tl[i - 1].Start == tl[i].Start)
                        Assert.Less(tl[i - 1].Seq, tl[i].Seq, "同刻按 Seq 递增");
                }
                // 恒等式：永不早于逻辑
                Assert.GreaterOrEqual(tl[i].Start, L(tl[i].Tick),
                    $"节点 Seq {tl[i].Seq} 早于逻辑时刻——画面不可能早于逻辑");
            }
            Assert.AreEqual(r.Stream.Count, seqSeen.Count);
            foreach (PlannedNodeKind k in Enum.GetValues(typeof(PlannedNodeKind)))
                Assert.IsTrue(kinds.Contains(k), $"{k} 未出现——覆盖场景退化");

            CheckActionSerial(tl);
            CheckDeliveryChain(tl);
            CheckAttackPairing(tl, timings);
            CheckCellExclusivity(tl, r.Snapshots[0]);
        }

        [Test]
        public void Plan_RealBattle_SameSeed_IdenticalTimelines()
        {
            var run1 = RunCoveragePreview(SeedA);
            var run2 = RunCoveragePreview(SeedA);
            var t1 = new BattlePlanner().Plan(run1.r.Stream, RealTable(run1.s));
            var t2 = new BattlePlanner().Plan(run2.r.Stream, RealTable(run2.s));
            Assert.AreEqual(t1.Count, t2.Count);
            for (int i = 0; i < t1.Count; i++)
                Assert.IsTrue(t1[i].Equals(t2[i]), $"同种子两场的时间轴第 {i} 节点漂移——确定性红线");
            Assert.AreEqual(t1.Makespan, t2.Makespan);
        }

        [Test]
        public void Plan_RealDotBattle_CorpseHoldsCellToMakespan()
        {
            var (s, r) = RunDotPreview(SeedA);
            var tl = new BattlePlanner().Plan(r.Stream, RealTable(s));
            int ratId = s.EnemyRoster[0].Id;

            bool hasDeath = false, hasRemove = false;
            for (int i = 0; i < tl.Count; i++)
            {
                if (tl[i].UnitId != ratId) continue;
                if (tl[i].Kind == PlannedNodeKind.Death) hasDeath = true;
                if (tl[i].Kind == PlannedNodeKind.Remove) hasRemove = true;
            }
            Assert.IsTrue(hasDeath, "病鼠有倒地节点");
            Assert.IsFalse(hasRemove, "DoT 致死无让格节点（该格永不释放——批次A 挂账镜像）");

            // 全盘占位互斥（DoT 尸体区间持到 Makespan，无重叠即通过）
            CheckCellExclusivity(tl, r.Snapshots[0]);
        }

        // ─────────────────────── 检查器（验收口径的代码化） ───────────────────────

        /// <summary>I1：按流序逐单位检查动作串行；死亡例外（可打断自身动作）且死后无动作。</summary>
        private static void CheckActionSerial(PlannedTimeline tl)
        {
            var perUnit = new Dictionary<int, List<PlannedNode>>();
            for (int i = 0; i < tl.Count; i++)
            {
                PlannedNode n = tl[i];
                if (n.Kind != PlannedNodeKind.Move && n.Kind != PlannedNodeKind.Attack
                    && n.Kind != PlannedNodeKind.Cast && n.Kind != PlannedNodeKind.Death) continue;
                if (!perUnit.TryGetValue(n.UnitId, out var list)) perUnit[n.UnitId] = list = new List<PlannedNode>();
                list.Add(n);
            }
            foreach (var kv in perUnit)
            {
                kv.Value.Sort((a, b) => a.Seq.CompareTo(b.Seq));
                bool dead = false, hasPrev = false;
                PlannedNode prev = default;
                foreach (var n in kv.Value)
                {
                    if (n.Kind == PlannedNodeKind.Death) { dead = true; continue; }
                    Assert.IsFalse(dead, $"I1 破：单位 {kv.Key} 死后仍有动作（Seq {n.Seq}）");
                    if (hasPrev)
                        Assert.GreaterOrEqual(n.Start, prev.Start + prev.Duration,
                            $"I1 破：单位 {kv.Key} 的 Seq {n.Seq} 与上一动作重叠");
                    prev = n; hasPrev = true;
                }
            }
        }

        /// <summary>I2：按流序逐单位检查交付链——数值交付严格递增（≥+Gap）、死亡=致死交付同刻。</summary>
        private static void CheckDeliveryChain(PlannedTimeline tl)
        {
            var perUnit = new Dictionary<int, List<PlannedNode>>();
            for (int i = 0; i < tl.Count; i++)
            {
                PlannedNode n = tl[i];
                if (n.Kind != PlannedNodeKind.Damage && n.Kind != PlannedNodeKind.Heal
                    && n.Kind != PlannedNodeKind.Mana && n.Kind != PlannedNodeKind.MaxHp
                    && n.Kind != PlannedNodeKind.Death) continue;
                if (!perUnit.TryGetValue(n.UnitId, out var list)) perUnit[n.UnitId] = list = new List<PlannedNode>();
                list.Add(n);
            }
            foreach (var kv in perUnit)
            {
                var list = kv.Value;
                list.Sort((a, b) => a.Seq.CompareTo(b.Seq));
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i].Kind == PlannedNodeKind.Death)
                    {
                        Assert.Greater(i, 0, $"单位 {kv.Key} 的死亡前必须有致死伤害");
                        Assert.AreEqual(PlannedNodeKind.Damage, list[i - 1].Kind,
                            $"单位 {kv.Key} 的死亡必紧跟致死伤害（流序）");
                        Assert.AreEqual(list[i - 1].Start, list[i].Start, "倒地=致死交付同刻");
                        continue;
                    }
                    if (i > 0 && list[i - 1].Kind != PlannedNodeKind.Death)
                        Assert.GreaterOrEqual(list[i].Start, list[i - 1].Start + BattlePlanner.DeliveryGap,
                            $"I2 破：单位 {kv.Key} 的 Seq {list[i].Seq} 交付未按链递增");
                }
            }
        }

        /// <summary>普攻配对契约：Seq+1 必为配对伤害（源码序），落点=Start+命中偏移且在挥击窗内。</summary>
        private static void CheckAttackPairing(PlannedTimeline tl, PlannerTimingTable timings)
        {
            var bySeq = new Dictionary<int, PlannedNode>();
            for (int i = 0; i < tl.Count; i++) bySeq[tl[i].Seq] = tl[i];
            for (int i = 0; i < tl.Count; i++)
            {
                PlannedNode n = tl[i];
                if (n.Kind != PlannedNodeKind.Attack) continue;
                Assert.IsTrue(bySeq.TryGetValue(n.Seq + 1, out PlannedNode dmg)
                    && dmg.Kind == PlannedNodeKind.Damage && dmg.UnitId == n.TargetId,
                    $"普攻 Seq {n.Seq} 的紧邻下一条必须是配对伤害（源码序契约）");
                UnitTiming t = timings.Get(n.UnitId);
                float offset = Hit(t.AttackDuration, t.AttackHitPercent);
                Assert.AreEqual(n.Start + offset, dmg.Start, 1e-6f, $"普攻 Seq {n.Seq} 的落点=Start+命中偏移");
                Assert.LessOrEqual(dmg.Start, n.Start + n.Duration + 1e-6f, "命中在挥击窗内（预扣构造保证）");
            }
        }

        /// <summary>I3/I4 验收口径代码化："任一格任一时刻只有一个呈现单位"。
        /// 从 tick0 快照播种占用，按时间轴重放占位区间：站位持有到"离开"——
        /// 移动=起点格持到补间走完、新格从到达持有；死亡=持有到让格时刻（无让格节点=DoT 尸体
        /// 持到 Makespan，镜像"该格不释放"）。区间两两不重叠（端点相接合法）。</summary>
        private static void CheckCellExclusivity(PlannedTimeline tl, BattleSnapshot seedFrame)
        {
            var intervals = new Dictionary<int, List<float[]>>();
            var openCell = new Dictionary<int, int>();
            var openSince = new Dictionary<int, float>();

            void Open(int unit, int key, float since)
            {
                openCell[unit] = key;
                openSince[unit] = since;
            }
            void Close(int unit, float until)
            {
                // 病态封口动作（死后延迟开拍的移动）合规资产下不可达——防御忽略
                if (!openCell.ContainsKey(unit)) return;
                int key = openCell[unit];
                float since = openSince[unit];
                if (!intervals.TryGetValue(key, out var list)) intervals[key] = list = new List<float[]>();
                list.Add(new[] { since, until });
                openCell.Remove(unit);
                openSince.Remove(unit);
            }

            for (int i = 0; i < seedFrame.Units.Length; i++)
                Open(seedFrame.Units[i].UnitId, seedFrame.Units[i].Y * GameConst.BoardWidth + seedFrame.Units[i].X, 0f);

            // 死亡让渡的终点：有让格节点=让格时刻；无（DoT 尸体）=Makespan
            var removeAt = new Dictionary<int, float>();
            for (int i = 0; i < tl.Count; i++)
                if (tl[i].Kind == PlannedNodeKind.Remove) removeAt[tl[i].UnitId] = tl[i].Start;

            for (int i = 0; i < tl.Count; i++)
            {
                PlannedNode n = tl[i];
                if (n.Kind == PlannedNodeKind.Move)
                {
                    Close(n.UnitId, n.Start + n.Duration);   // 起点格保守持到走完（与规划器同款口径）
                    Open(n.UnitId, n.ToY * GameConst.BoardWidth + n.ToX, n.Start + n.Duration);
                }
                else if (n.Kind == PlannedNodeKind.Death)
                {
                    float end = removeAt.TryGetValue(n.UnitId, out float rel) ? rel : tl.Makespan;
                    Close(n.UnitId, end);
                }
            }
            var leftovers = new List<int>(openCell.Keys);
            foreach (int unit in leftovers) Close(unit, tl.Makespan);   // 幸存者站位持到终局

            foreach (var kv in intervals)
            {
                var list = kv.Value;
                list.Sort((a, b) => a[0].CompareTo(b[0]));
                for (int i = 1; i < list.Count; i++)
                    Assert.LessOrEqual(list[i - 1][1], list[i][0] + 1e-6f,
                        $"I3/I4 破：格 {kv.Key} 占用区间重叠（踩格/踩尸体）");
            }
        }

        // ─────────────────────── 真战场景底座（与 PreviewTests 同款拷贝，各文件自持） ───────────────────────

        /// <summary>覆盖场景表：一场战斗打满 11 类白名单事件（PreviewTests 同款）。</summary>
        private static GameTables CoverageTables()
        {
            var heroStats = new UnitStats
            {
                MaxHp = 500, AttackPower = 25, Armor = 0, MagicResist = 0, AbilityPower = 100,
                AttackRange = 4, AttackInterval = 0.8f, MoveInterval = 1f,
                CritChance = 0, CritDamage = 50,
                MaxMana = 30, ManaPerHit = 10, ManaRegenPerSecond = 0,
            };
            var chars = new CharacterTable(new[]
            {
                new CharacterDef(1, "验收术士", 1, heroStats, Array.Empty<SynergyId>(), RoleId.Mage, 90),
            });

            var foeStats = new UnitStats
            {
                MaxHp = 400, AttackPower = 30, Armor = 0, MagicResist = 0, AbilityPower = 0,
                AttackRange = 1, AttackInterval = 1f, MoveInterval = 0.5f,
                CritChance = 0, CritDamage = 0,
                MaxMana = 10, ManaPerHit = 0, ManaRegenPerSecond = 10,
            };
            var enemies = new EnemyTable(new[]
            {
                new EnemyRoundEntry(1, new[]
                {
                    new EnemySpawn("验收傀儡", foeStats, new Pos(4, 0), 1, 1, 91),   // 矿挡位 1、技能 91
                }),
            });

            var skills = new SkillTable(new SkillDef[]
            {
                new SkillDef(90, "验收奥术", DamageType.Magic, 40, 0, 0, 50, 0, 4, SkillTargetRule.NearestEnemy, false),
                new SkillDef(91, "验收魔盾", DamageType.Physical, 0, 0, 0, 0, 0, 1, SkillTargetRule.Self, false,
                    buffId: 92, buffTarget: SkillBuffTarget.Targets),
            });

            var buffs = new BuffTable(new BuffDef[]
            {
                new BuffDef(92, "验收魔盾体", 300, 1,
                    new[] { new StatModifier(StatKind.MaxHp, StatOp.Flat, 100f) }, 0, 1),
            });

            var ores = new OreTable(new[]
            {
                new OreDef(11, OreRewardType.Gold, "验收金", 5, 0),
                new OreDef(21, OreRewardType.Gold, "验收金·中", 8, 0),
                new OreDef(31, OreRewardType.Gold, "验收金·高", 10, 0),
            }, new[]
            {
                new OreTierDef(1, new[] { 11 }),
                new OreTierDef(2, new[] { 21 }),
                new OreTierDef(3, new[] { 31 }),
            });

            return new GameTables
            {
                Characters = chars,
                Odds = AllCostOne(),
                Rounds = RoundTable.Default,
                Enemies = enemies,
                Synergies = SynergyTable.Default,
                Ores = ores,
                Policies = new PolicyTable(new PolicyDef[0]),   // 空表：奖励关胜后 0 候选静默（批次7 坑②）
                Skills = skills,
                Equipment = EquipmentTable.Default,
                Buffs = buffs,
            };
        }

        /// <summary>DoT 击杀场景表（PreviewTests 同款）：病鼠 5 血必死于毒——锁"DoT 致死无 Removed、尸体占格"。</summary>
        private static GameTables DotKillTables()
        {
            var heroStats = new UnitStats
            {
                MaxHp = 500, AttackPower = 1, Armor = 0, MagicResist = 0, AbilityPower = 0,
                AttackRange = 4, AttackInterval = 10f, MoveInterval = 1f,
                CritChance = 0, CritDamage = 0,
                MaxMana = 30, ManaPerHit = 0, ManaRegenPerSecond = 15,   // 2 秒满蓝 → tick 60 首次施法
            };
            var chars = new CharacterTable(new[]
            {
                new CharacterDef(1, "验收毒师", 1, heroStats, Array.Empty<SynergyId>(), RoleId.Mage, 80),
            });

            var foeStats = new UnitStats
            {
                MaxHp = 5, AttackPower = 0, Armor = 0, MagicResist = 0, AbilityPower = 0,
                AttackRange = 1, AttackInterval = 1f, MoveInterval = 0.5f,
                CritChance = 0, CritDamage = 0, MaxMana = 0, ManaPerHit = 0, ManaRegenPerSecond = 0,
            };
            var enemies = new EnemyTable(new[]
            {
                new EnemyRoundEntry(1, new[] { new EnemySpawn("验收病鼠", foeStats, new Pos(4, 0), 1, 1) }),
            });

            var skills = new SkillTable(new SkillDef[]
            {
                new SkillDef(80, "验收毒咒", DamageType.Magic, 1, 0, 0, 0, 0, 4, SkillTargetRule.NearestEnemy, false,
                    buffId: 93, buffTarget: SkillBuffTarget.Targets),
            });
            var buffs = new BuffTable(new BuffDef[]
            {
                new BuffDef(93, "验收毒", 300, 1, null, 2, 0),
            });

            return new GameTables
            {
                Characters = chars,
                Odds = AllCostOne(),
                Rounds = RoundTable.Default,
                Enemies = enemies,
                Synergies = SynergyTable.Default,
                Ores = OreTable.Default,
                Policies = new PolicyTable(new PolicyDef[0]),
                Skills = skills,
                Equipment = EquipmentTable.Default,
                Buffs = buffs,
            };
        }

        private static ShopOdds AllCostOne()
        {
            var t = new int[9, 6];
            for (int level = 2; level <= 8; level++) t[level, 1] = 100;
            return new ShopOdds(t);
        }

        /// <summary>覆盖场景整场预演（同种子=同战斗；术士 (0,0) 对 (4,0) 傀儡——距离 4 逼出走位）。</summary>
        private static (GameSession s, BattlePreviewResult r) RunCoveragePreview(uint seed)
        {
            var s = new GameSession(new EventBus(), seed, CoverageTables());
            Assert.IsTrue(s.StartRun());
            Assert.IsTrue(s.Buy(0), "1 费棋：开局 3 金买得起");
            Assert.IsTrue(s.PlaceOnBoard(s.Bench[0].Id, new Pos(0, 0)));
            var p = new BattlePreviewer();
            Assert.IsTrue(p.Start(s), "预演开战");
            return (s, p.RunToEnd());
        }

        /// <summary>DoT 场景整场预演。</summary>
        private static (GameSession s, BattlePreviewResult r) RunDotPreview(uint seed)
        {
            var s = new GameSession(new EventBus(), seed, DotKillTables());
            Assert.IsTrue(s.StartRun());
            Assert.IsTrue(s.Buy(0));
            Assert.IsTrue(s.PlaceOnBoard(s.Bench[0].Id, new Pos(0, 0)));
            var p = new BattlePreviewer();
            Assert.IsTrue(p.Start(s));
            return (s, p.RunToEnd());
        }

        /// <summary>生产形态镜像：4B2 组合根从名册逐单位填表（拍板的占位默认值）。</summary>
        private static PlannerTimingTable RealTable(GameSession s)
        {
            UnitTiming timing = T(0.45f, 0.5f, 0.60f, 0.5f, 0.30f, 0.40f);
            var table = new PlannerTimingTable(timing);
            foreach (var u in s.PlayerBoard) table.Set(u.Id, timing);
            foreach (var u in s.EnemyRoster) table.Set(u.Id, timing);
            return table;
        }
    }
}
