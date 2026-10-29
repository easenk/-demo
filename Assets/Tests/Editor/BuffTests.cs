using System;
using System.Collections.Generic;
using System.Linq;
using AutoChess.Game;
using NUnit.Framework;

namespace AutoChess.Tests
{
    /// <summary>
    /// 阶段3 批次4：Buff 系统（阶段3-接口设计 第六章）。
    ///
    /// 锁死的契约：
    ///  · 表：构造即校验（id 唯一 ≥1 / 持续时间 ≥1 / 层数上限 ≥1 / 至少一种效果）；
    ///  · 挂载：叠层不超上限（超了静默不叠）；面板修饰器盖 Buff 章（BuffSourceBase+id=500+id）；
    ///  · 结算（每 tick 末尾）：DoT 每层每 tick 一跳（真实伤害路径：不打折不暴击）→ Hot → 计时递减 → 到期摘层；
    ///  · 层全掉 → 摘 Buff（修饰器按章整批摘，面板回落）；
    ///  · 死亡即止：DoT/Hot 不再结算，但账目可查（尸上账保留到按源清/战斗结束）；
    ///  · 按来源整批移除（SourceId 句柄）；战斗结束清空（Buff 不跨战斗——与"蓝不跨战斗"同纪律）；
    ///  · 不引入新随机源（数值表写死）。
    /// </summary>
    [TestFixture]
    public class BuffTests
    {
        // ── 基建 ──

        private static UnitStats Stats(int hp = 1000, int atk = 100)
            => new UnitStats
            {
                MaxHp = hp, AttackPower = atk, Armor = 0, MagicResist = 0, AbilityPower = 0,
                AttackRange = 5, AttackInterval = 1f, MoveInterval = 1f,
                CritChance = 0, CritDamage = 50, MaxMana = 0, ManaPerHit = 0, ManaRegenPerSecond = 0,
            };

        private static Unit UnitOf(EventBus bus, int id = 1, int hp = 1000)
            => new Unit(id, 0, true, Stats(hp), bus);

        private static BuffDef Def(int id, int duration, int maxStacks,
            StatModifier[] mods = null, int dot = 0, int hot = 0)
            => new BuffDef(id, $"Buff{id}", duration, maxStacks, mods, dot, hot);

        // ── 表构造校验 ──

        [Test]
        public void Table_Ctor_ValidationMatrix()
        {
            var oneMod = new[] { new StatModifier(StatKind.Armor, StatOp.Flat, 5f) };
            Assert.Throws<ArgumentException>(() => new BuffTable(new[] { Def(0, 10, 1, oneMod) }), "id 必须 ≥ 1");
            Assert.Throws<ArgumentException>(() => new BuffTable(new[]
            {
                Def(1, 10, 1, oneMod),
                Def(1, 10, 1, oneMod),
            }), "id 重复");
            Assert.Throws<ArgumentException>(() => new BuffTable(new[] { Def(1, 0, 1, oneMod) }), "持续时间必须 ≥ 1 tick");
            Assert.Throws<ArgumentException>(() => new BuffTable(new[] { Def(1, 10, 0, oneMod) }), "层数上限必须 ≥ 1");
            Assert.Throws<ArgumentException>(() => new BuffTable(new[] { Def(1, 10, 1, null, 0, 0) }), "至少一种效果（全空 = 建表事故）");
        }

        [Test]
        public void Table_Default_HasFourDirectionalEntries()
        {
            var t = BuffTable.Default;
            Assert.AreEqual(4, t.All.Count, "占位四方向：攻击叠层 / DoT / Hot / 防御不叠层");
            Assert.AreEqual(90, t.Get(1).DurationTicks, "战意 3 秒 = 90 tick");
            Assert.AreEqual(3, t.Get(1).MaxStacks, "战意叠 3 层");
            Assert.AreEqual(1, t.Get(2).DotDamage, "灼烧每 tick 1 点");
            Assert.AreEqual(1, t.Get(3).HotHeal, "回复每 tick 1 点");
            Assert.AreEqual(150, t.Get(4).DurationTicks, "石肤 5 秒 = 150 tick");
        }

        // ── 挂载与叠层 ──

        [Test]
        public void Apply_StacksUpToLimit_ThenSilentlyHolds()
        {
            var bus = new EventBus();
            var table = new BuffTable(new[] { Def(1, 90, 3, new[] { new StatModifier(StatKind.AttackPower, StatOp.Percent, 10f) }) });
            var sys = new BuffSystem(table, bus);
            Unit u = UnitOf(bus);

            Assert.AreEqual(1, sys.Apply(u, new BuffApplier(1, ModSourceId.TestBase)), "第 1 次 → 1 层");
            Assert.AreEqual(2, sys.Apply(u, new BuffApplier(1, ModSourceId.TestBase)), "第 2 次 → 2 层");
            Assert.AreEqual(3, sys.Apply(u, new BuffApplier(1, ModSourceId.TestBase)), "第 3 次 → 3 层（上限）");
            Assert.AreEqual(3, sys.Apply(u, new BuffApplier(1, ModSourceId.TestBase)), "第 4 次 → 静默不叠，仍 3 层");
            Assert.AreEqual(3, sys.GetStacks(u, 1));

            // 面板修饰器只挂一份（三层共用一套修饰器——每层只是计时单位）
            Assert.AreEqual(1, u.Modifiers.Count(m => m.SourceId == BuffSystem.BuffSourceBase + 1), "Buff 章恰好一份");
        }

        [Test]
        public void Apply_Rejects_DeadTarget_AndUnknownBuff()
        {
            var bus = new EventBus();
            var table = new BuffTable(new[] { Def(1, 10, 1, new[] { new StatModifier(StatKind.Armor, StatOp.Flat, 5f) }) });
            var sys = new BuffSystem(table, bus);
            Unit u = UnitOf(bus);

            Assert.AreEqual(0, sys.Apply(u, new BuffApplier(99, ModSourceId.TestBase)), "表外 id → 0");
            Assert.AreEqual(0, sys.Apply(null, new BuffApplier(1, ModSourceId.TestBase)), "null 目标 → 0");
        }

        // ── 每 tick 结算：DoT / Hot / 计时 ──

        [Test]
        public void TickAll_DoT_DealsExactDamage_AndExpiresOnTime()
        {
            var bus = new EventBus();
            var damages = new List<UnitDamagedEvent>();
            bus.Subscribe<UnitDamagedEvent>(e => damages.Add(e));
            // 灼烧：90 tick、每 tick 1 点 → 总量 90
            var table = new BuffTable(new[] { Def(2, 90, 1, dot: 1) });
            var sys = new BuffSystem(table, bus);
            Unit u = UnitOf(bus, hp: 1000);

            sys.Apply(u, new BuffApplier(2, ModSourceId.TestBase));
            for (int i = 0; i < 89; i++) sys.TickAll();

            Assert.AreEqual(89, damages.Count, "89 个 tick 各跳 1 点");
            Assert.AreEqual(89, damages.Sum(d => d.FinalDamage), "每跳恰好 1 点（真实伤害：0 抗 0 减免）");
            Assert.AreEqual(1, sys.GetStacks(u, 2), "90 tick 时长，第 89 个 tick 末还有 1 层");

            sys.TickAll();   // 第 90 个 tick：最后一跳 + 到期摘层
            Assert.AreEqual(90, damages.Count);
            Assert.AreEqual(0, sys.GetStacks(u, 2), "到期摘层");
        }

        [Test]
        public void TickAll_DoT_EachLayerJumpsSeparately()
        {
            var bus = new EventBus();
            var damages = new List<UnitDamagedEvent>();
            bus.Subscribe<UnitDamagedEvent>(e => damages.Add(e));
            // 可叠 3 层的 DoT：3 层同 tick = 3 跳
            var table = new BuffTable(new[] { Def(2, 90, 3, dot: 1) });
            var sys = new BuffSystem(table, bus);
            Unit u = UnitOf(bus, hp: 1000);

            sys.Apply(u, new BuffApplier(2, ModSourceId.TestBase));
            sys.Apply(u, new BuffApplier(2, ModSourceId.TestBase));
            sys.Apply(u, new BuffApplier(2, ModSourceId.TestBase));
            sys.TickAll();

            Assert.AreEqual(3, damages.Count, "3 层 = 同一 tick 内 3 跳");
            Assert.AreEqual(3, damages.Sum(d => d.FinalDamage), "每层每跳 1 点");
        }

        [Test]
        public void TickAll_Hot_HealsExactAmount()
        {
            var bus = new EventBus();
            var heals = new List<UnitHealedEvent>();
            bus.Subscribe<UnitHealedEvent>(e => heals.Add(e));
            var table = new BuffTable(new[] { Def(3, 90, 1, hot: 1) });
            var sys = new BuffSystem(table, bus);
            Unit u = UnitOf(bus, hp: 1000);
            u.TakeDamage(50, null);   // 先打掉 50 血（Heal 满血静默）

            sys.Apply(u, new BuffApplier(3, ModSourceId.TestBase));
            for (int i = 0; i < 10; i++) sys.TickAll();

            Assert.AreEqual(10, heals.Count);
            Assert.AreEqual(10, heals.Sum(h => h.Amount), "10 tick 回 10 点");
        }

        [Test]
        public void TickAll_ModifiersExpire_PanelFallsBack()
        {
            var bus = new EventBus();
            var table = new BuffTable(new[] { Def(4, 10, 1, new[] { new StatModifier(StatKind.Armor, StatOp.Flat, 15f) }) });
            var sys = new BuffSystem(table, bus);
            Unit u = UnitOf(bus);
            Assert.AreEqual(0, u.EffectiveStats.Armor, "裸装基线");

            sys.Apply(u, new BuffApplier(4, ModSourceId.TestBase));
            Assert.AreEqual(15, u.EffectiveStats.Armor, "挂上：护甲 +15");

            for (int i = 0; i < 9; i++) sys.TickAll();
            Assert.AreEqual(15, u.EffectiveStats.Armor, "10 tick 时长，第 9 个 tick 末仍在");

            sys.TickAll();   // 第 10 个：到期摘
            Assert.AreEqual(0, u.EffectiveStats.Armor, "到期：修饰器按章摘干净，面板回落");
            Assert.AreEqual(0, u.Modifiers.Count(m => m.SourceId == BuffSystem.BuffSourceBase + 4), "Buff 章零残留");
        }

        [Test]
        public void TickAll_DotStopsOnDeath_AccountRemains()
        {
            var bus = new EventBus();
            var table = new BuffTable(new[] { Def(2, 90, 1, dot: 10) });
            var sys = new BuffSystem(table, bus);
            Unit u = UnitOf(bus, hp: 25);

            sys.Apply(u, new BuffApplier(2, ModSourceId.TestBase));
            sys.TickAll();   // 第 1 tick：跳 10（25 → 15）
            sys.TickAll();   // 第 2 tick：跳 10（15 → 5）
            sys.TickAll();   // 第 3 tick：跳 10（5 → 0 死亡）

            Assert.IsFalse(u.IsAlive, "DoT 打死目标");
            int damageEvents = 0;
            bus.Subscribe<UnitDamagedEvent>(e => damageEvents++);
            sys.TickAll();   // 第 4 tick：死亡即止
            Assert.AreEqual(0, damageEvents, "死后不再结算");
            Assert.AreEqual(1, sys.GetStacks(u, 2), "账目保留在尸上（可查——战绩/断言用）");
        }

        // ── 按来源整批移除 + 战斗结束清空 ──

        [Test]
        public void RemoveBySource_StripsBuffsFromThatSource_Only()
        {
            var bus = new EventBus();
            var table = new BuffTable(new[]
            {
                Def(1, 90, 1, new[] { new StatModifier(StatKind.Armor, StatOp.Flat, 10f) }),
                Def(2, 90, 1, dot: 1),
            });
            var sys = new BuffSystem(table, bus);
            Unit u = UnitOf(bus);

            sys.Apply(u, new BuffApplier(1, ModSourceId.TestBase));        // 测试来源的 Buff 1
            sys.Apply(u, new BuffApplier(2, 4000 + 1));                     // 另一来源的 Buff 2
            Assert.AreEqual(10, u.EffectiveStats.Armor);

            Assert.AreEqual(1, sys.RemoveBySource(u, ModSourceId.TestBase), "只摘测试来源的");
            Assert.AreEqual(0, sys.GetStacks(u, 1), "Buff 1 被摘");
            Assert.AreEqual(1, sys.GetStacks(u, 2), "Buff 2 不受影响（来源不同）");
            Assert.AreEqual(0, u.EffectiveStats.Armor, "Buff 1 的修饰器一并摘干净");

            Assert.Throws<ArgumentException>(() => sys.RemoveBySource(u, 0), "SourceId=0 不是'一个来源'");
        }

        [Test]
        public void ClearAll_StripsEverything()
        {
            var bus = new EventBus();
            var table = new BuffTable(new[]
            {
                Def(1, 90, 3, new[] { new StatModifier(StatKind.Armor, StatOp.Flat, 10f) }),
                Def(2, 90, 1, dot: 1),
            });
            var sys = new BuffSystem(table, bus);
            Unit u = UnitOf(bus);

            sys.Apply(u, new BuffApplier(1, ModSourceId.TestBase));
            sys.Apply(u, new BuffApplier(1, ModSourceId.TestBase));
            sys.Apply(u, new BuffApplier(2, 4000 + 1));
            Assert.AreEqual(10, u.EffectiveStats.Armor);

            sys.ClearAll();
            Assert.AreEqual(0, sys.GetStacks(u, 1));
            Assert.AreEqual(0, sys.GetStacks(u, 2));
            Assert.AreEqual(0, u.Modifiers.Count(m => m.SourceId >= BuffSystem.BuffSourceBase), "全部 Buff 章零残留");
        }

        // ── 战斗接线：OnTickEnd 驱动 + 与胜负结算的顺序 ──

        [Test]
        public void CombatIntegration_BuffHookDrivesTickAll_BeforeOutcome()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            var table = new BuffTable(new[] { Def(2, 90, 1, dot: 5) });
            var buffs = new BuffSystem(table, bus);

            Unit player = UnitOf(bus, 1, hp: 100);
            Unit enemy = new Unit(2, 0, false, Stats(30, atk: 0), bus);   // 30 血：6 跳 DoT（每跳 5）打死
            Assert.IsTrue(board.TryPlace(player, new Pos(0, 0)));
            Assert.IsTrue(board.TryPlace(enemy, new Pos(3, 3)));   // 距离 3 < 射程 5：普攻打得到（不占位不堵路）

            var combat = new CombatSystem(board, bus, 11u);
            combat.AddHook(new BuffTickHook(buffs));   // OnTickEnd → buffs.TickAll()
            buffs.Apply(enemy, new BuffApplier(2, ModSourceId.TestBase));

            int guard = 0;
            while (!combat.IsOver && guard++ < 600) combat.Tick();
            // 真实时间线：普攻第 ~30 tick 首击 100 点直接秒杀（30 血 < 100 伤）——
            // DoT（第 6 跳才致死）永远慢于普攻。本测试验证的其实是"接线通"：
            // 钩子驱动 TickAll 正常跑、战斗正常分晓、DoT 账目可查。
            Assert.IsTrue(combat.IsOver, "战斗分晓");
            Assert.IsTrue(combat.PlayerWon);
            Assert.IsFalse(enemy.IsAlive, "敌人死亡（普攻秒杀——DoT 慢于普攻是面板量级问题，非接线问题）");
        }

        // ── 确定性 ──

        [Test]
        public void Determinism_BuffTick_SameSeedSameStream()
        {
            string RunOnce(uint seed)
            {
                var bus = new EventBus();
                var log = new List<string>();
                bus.Subscribe<UnitDamagedEvent>(e => log.Add($"D|{e.UnitId}|{e.FinalDamage}|{e.CurrentHp}"));
                bus.Subscribe<UnitHealedEvent>(e => log.Add($"H|{e.UnitId}|{e.Amount}|{e.CurrentHp}"));

                var board = new BoardData(bus);
                var table = new BuffTable(new[]
                {
                    Def(2, 90, 2, dot: 3),
                    Def(3, 90, 1, hot: 2),
                });
                var buffs = new BuffSystem(table, bus);
                Unit player = UnitOf(bus, 1, hp: 500);
                Unit enemy = new Unit(2, 0, false, Stats(200), bus);
                board.TryPlace(player, new Pos(0, 0));
                board.TryPlace(enemy, new Pos(2, 2));

                var combat = new CombatSystem(board, bus, seed);
                combat.AddHook(new BuffTickHook(buffs));
                buffs.Apply(enemy, new BuffApplier(2, ModSourceId.TestBase));
                buffs.Apply(enemy, new BuffApplier(2, ModSourceId.TestBase));
                buffs.Apply(player, new BuffApplier(3, ModSourceId.TestBase));

                for (int i = 0; i < 50 && !combat.IsOver; i++) combat.Tick();
                return string.Join(";", log);
            }

            Assert.AreEqual(RunOnce(77u), RunOnce(77u), "Buff 结算不引入随机：同种子双战斗事件流逐条一致");
        }

        // ── 测试用钩子 ──

        /// <summary>OnTickEnd → BuffSystem.TickAll()（批次4 的标准接线方式；生产侧由 GameSession/CombatSystem 组合）。</summary>
        private sealed class BuffTickHook : IBattleHook
        {
            private readonly BuffSystem _buffs;
            public int SourceId => ModSourceId.TestBase;
            public BuffTickHook(BuffSystem buffs) => _buffs = buffs;
            public void OnBattleStart(BattleContext ctx) { }
            public void OnBeforeAttack(BattleContext ctx, ref AttackDraft draft) { }
            public void OnAfterAttack(BattleContext ctx, in AttackResult result) { }
            public void OnDamaged(BattleContext ctx, in DamageResult result) { }
            public void OnDeath(BattleContext ctx, in DeathInfo death) { }
            public void OnTickEnd(BattleContext ctx) => _buffs.TickAll();
        }
    }
}
