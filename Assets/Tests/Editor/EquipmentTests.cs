using System;
using System.Collections.Generic;
using System.Linq;
using AutoChess.Game;
using NUnit.Framework;

namespace AutoChess.Tests
{
    /// <summary>
    /// 阶段3 批次3：装备系统（决策 #12 三散件·八大件·升级分支制；P3-2/P3-4 拍板）。
    ///
    /// 锁死的契约：
    ///  · 表：3 散件 + 8 大件；散件持分支指针、大件持升级费；构造即校验（id/修饰器/分支引用/费用）
    ///  · 穿戴：背包实例 → 空槽；拒绝矩阵 = 非 Prep / 无表 / 未知单位 / 槽号越界 / 背包无此件 / 槽已占
    ///  · 挂账（P3-2）：只挂场上玩家单位（与羁绊/策略同构重算制）；备战席穿着待命合法、上场才生效
    ///  · 盖章：装备号段 1000 + 槽位号（槽 0/1/2 = 1000/1001/1002 互不干扰）
    ///  · 卸下：消耗 1 个拆卸器（#12⑦ 消耗品）；无拆卸器 / 空槽纯拒绝
    ///  · 升级：仅备战期 + TrySpend 严格扣款；原地（槽位与实例号不变）；一次定型（大件不可再升）
    ///  · 卖棋子：装备全额返还背包（含大件）；升级费不退（卖价只按棋子算）
    ///  · 合成（2026-10-29 拍板）：锚点装备继承给合成体、材料装备退回背包
    ///  · 开局送 2 个拆卸器（2026-10-29 拍板）；掉落来源批次6 接入
    /// </summary>
    [TestFixture]
    public class EquipmentTests
    {
        // ── 基建 ──

        private static UnitStats Stats(int hp = 500, int atk = 100, float atkIv = 1f)
            => new UnitStats
            {
                MaxHp = hp, AttackPower = atk, Armor = 0, MagicResist = 0, AbilityPower = 100,
                AttackRange = 1, AttackInterval = atkIv, MoveInterval = 1f,
                CritChance = 0, CritDamage = 50, MaxMana = 0, ManaPerHit = 0, ManaRegenPerSecond = 0,
            };

        private static CharacterTable SingleChar(int hp = 500, int atk = 100, float atkIv = 1f)
            => new CharacterTable(new[] { new CharacterDef(1, "棋子1", 1, Stats(hp, atk, atkIv), null) });

        /// <summary>注入包：单角色全 1 费（L2 下 100% 出它）；装备表可换（默认 Default；null = 无表容忍）。</summary>
        private static GameTables Tables(CharacterTable chars = null, EquipmentTable equipment = null)
            => new GameTables
            {
                Characters = chars ?? SingleChar(),
                Odds = ShopOdds.Default,
                Rounds = RoundTable.Default,
                Enemies = EnemyTable.Empty,
                Synergies = SynergyTable.Default,
                Ores = OreTable.Default,
                Policies = new PolicyTable(new PolicyDef[0]),
                Skills = SkillTable.Default,
                Equipment = equipment ?? EquipmentTable.Default,
            };

        /// <summary>买架上任意有货的一格（单角色表下买到谁恒定；架空先刷新——BuyAny 套路）。</summary>
        private static void BuyAny(GameSession s)
        {
            int slot = Array.FindIndex(s.Shop.Offers, o => o.Id != 0);
            Assert.GreaterOrEqual(slot, 0, "架上至少有一格有货");
            Assert.IsTrue(s.Buy(slot), "买入应成功");
        }

        /// <summary>买一张并上场（买完在备战席尾部；板位 = 等级 2 起步足够）。</summary>
        private static Unit BuyAndPlace(GameSession s, Pos pos)
        {
            BuyAny(s);
            Unit u = s.Bench[s.Bench.Count - 1];
            Assert.IsTrue(s.PlaceOnBoard(u.Id, pos), "上场应成功");
            return u;
        }

        private static EquipmentDef Def(int id, EquipmentKind kind, StatModifier[] mods, int[] options, int cost)
            => new EquipmentDef(id, $"装备{id}", "", kind, mods, options, cost);

        private static readonly StatModifier[] OneAttack
            = { new StatModifier(StatKind.AttackPower, StatOp.Flat, 1f) };

        // ── 表结构 ──

        [Test]
        public void Table_Default_ThreeComponents_EightFinished_BranchMatrix()
        {
            var t = EquipmentTable.Default;
            Assert.AreEqual(11, t.All.Count, "#12：3 散件 + 8 大件 = 11 条");

            // 散件分支矩阵（#12②：物理 3 分支 / 法术 2 / 防御 3）
            CollectionAssert.AreEqual(new[] { 11, 12, 13 }, t.Get(1).UpgradeOptions, "物理散件 → 输出/攻速/暴击");
            CollectionAssert.AreEqual(new[] { 21, 22 }, t.Get(2).UpgradeOptions, "法术散件 → 回蓝/法强");
            CollectionAssert.AreEqual(new[] { 31, 32, 33 }, t.Get(3).UpgradeOptions, "防御散件 → 生命/物防/法防");

            // 大件费用（占位 5~8 金，#12③）
            Assert.AreEqual(5, t.Get(11).UpgradeCost);
            Assert.AreEqual(6, t.Get(12).UpgradeCost);
            Assert.AreEqual(8, t.Get(13).UpgradeCost);
            Assert.AreEqual(5, t.Get(21).UpgradeCost);
            Assert.AreEqual(6, t.Get(22).UpgradeCost);
            Assert.AreEqual(5, t.Get(31).UpgradeCost);
            Assert.AreEqual(6, t.Get(32).UpgradeCost);
            Assert.AreEqual(6, t.Get(33).UpgradeCost);

            // 修饰器抽查：攻速大件 = 攻击间隔 -12%（间隔维修饰器语义）
            var haste = t.Get(12).Modifiers;
            Assert.AreEqual(1, haste.Length);
            Assert.AreEqual(StatKind.AttackInterval, haste[0].Kind);
            Assert.AreEqual(StatOp.Percent, haste[0].Op);
            Assert.AreEqual(-12f, haste[0].Value, 0.001f);

            // 阶段2 矿池/策略占位 EquipmentId=1 的含义锁死 = 物理散件
            Assert.AreEqual(EquipmentKind.Component, t.Get(1).Kind);
        }

        [Test]
        public void Table_Ctor_ValidationMatrix()
        {
            Assert.Throws<ArgumentException>(() => new EquipmentTable(new[]   // id 必须 ≥ 1
                { Def(0, EquipmentKind.Finished, OneAttack, null, 5) }));
            Assert.Throws<ArgumentException>(() => new EquipmentTable(new[]   // id 重复
            {
                Def(1, EquipmentKind.Finished, OneAttack, null, 5),
                Def(1, EquipmentKind.Finished, OneAttack, null, 5),
            }));
            Assert.Throws<ArgumentException>(() => new EquipmentTable(new[]   // 修饰器不能为空
                { Def(1, EquipmentKind.Finished, new StatModifier[0], null, 5) }));
            Assert.Throws<ArgumentException>(() => new EquipmentTable(new[]   // 散件必须带分支
                { Def(1, EquipmentKind.Component, OneAttack, null, 0) }));
            Assert.Throws<ArgumentException>(() => new EquipmentTable(new[]   // 分支指向散件（必须是大件）
            {
                Def(1, EquipmentKind.Component, OneAttack, new[] { 2 }, 0),
                Def(2, EquipmentKind.Component, OneAttack, new[] { 1 }, 0),
            }));
            Assert.Throws<ArgumentException>(() => new EquipmentTable(new[]   // 分支指向不存在的 id
                { Def(1, EquipmentKind.Component, OneAttack, new[] { 99 }, 0) }));
            Assert.Throws<ArgumentException>(() => new EquipmentTable(new[]   // 大件必须带升级费
                { Def(11, EquipmentKind.Finished, OneAttack, null, 0) }));
            Assert.Throws<ArgumentException>(() => new EquipmentTable(new[]   // 大件不带分支（一次定型）
                { Def(11, EquipmentKind.Finished, OneAttack, new[] { 12 }, 5) }));
        }

        // ── 背包与拆卸器 ──

        [Test]
        public void StartRun_GrantsTwoUnsmithTools_AndPublishesSnapshot()
        {
            var bus = new EventBus();
            var events = new List<InventoryChangedEvent>();
            bus.Subscribe<InventoryChangedEvent>(e => events.Add(e));
            var s = new GameSession(bus, 0u, Tables());

            Assert.AreEqual(0, s.UnsmithToolCount, "StartRun 前无拆卸器");
            Assert.IsTrue(s.StartRun());
            Assert.AreEqual(GameSession.StartUnsmithTools, s.UnsmithToolCount, "开局送 2 个（2026-10-29 拍板）");
            Assert.AreEqual(1, events.Count, "送拆卸器广播一次背包快照");
            Assert.AreEqual(0, events[0].EquipmentCount, "快照：背包 0 件装备");
            Assert.AreEqual(2, events[0].UnsmithTools, "快照：2 个拆卸器");
        }

        [Test]
        public void AddEquipment_UniqueInstanceIds_AndInventoryEvents()
        {
            var bus = new EventBus();
            var events = new List<InventoryChangedEvent>();
            bus.Subscribe<InventoryChangedEvent>(e => events.Add(e));
            var s = new GameSession(bus, 0u, Tables());
            Assert.IsTrue(s.StartRun());

            int a = s.AddEquipment(1);
            int b = s.AddEquipment(1);
            int c = s.AddEquipment(2);

            Assert.Greater(a, 0, "发放成功返回实例号");
            Assert.AreNotEqual(a, b);
            Assert.AreNotEqual(b, c);
            Assert.AreNotEqual(a, c, "实例号互不相等（全局唯一）");
            Assert.AreEqual(3, s.Inventory.EquipmentCount, "背包 3 件");
            Assert.AreEqual(4, events.Count, "开局送拆卸器 1 次 + 发放 3 次 = 4 次广播");
            Assert.AreEqual(0, s.AddEquipment(99), "表外 id 返回 0 哨兵");
            Assert.AreEqual(0, s.AddEquipment(0), "id 0 返回 0 哨兵");
        }

        // ── 穿戴 ──

        [Test]
        public void Equip_BoardUnit_AppliesModifiers_ExactLedger()
        {
            var bus = new EventBus();
            var equipped = new List<EquipmentEquippedEvent>();
            var inventory = new List<InventoryChangedEvent>();
            bus.Subscribe<EquipmentEquippedEvent>(e => equipped.Add(e));
            bus.Subscribe<InventoryChangedEvent>(e => inventory.Add(e));
            var s = new GameSession(bus, 0u, Tables());
            Assert.IsTrue(s.StartRun());
            Unit u = BuyAndPlace(s, new Pos(0, 0));
            Assert.AreEqual(100, u.EffectiveStats.AttackPower, "裸装基线");

            int id = s.AddEquipment(1);                       // 物理散件：攻击 +8
            Assert.IsTrue(s.Equip(u.Id, 0, id));

            Assert.AreEqual(108, u.EffectiveStats.AttackPower, "场上穿戴即生效：100 + 8 = 108");
            Assert.AreEqual(1, equipped.Count);
            Assert.AreEqual(u.Id, equipped[0].UnitId);
            Assert.AreEqual(id, equipped[0].InstanceId);
            Assert.AreEqual(1, equipped[0].DefId);
            Assert.AreEqual(0, equipped[0].SlotIndex);
            Assert.AreEqual(0, s.Inventory.EquipmentCount, "背包已出空");
            Assert.AreEqual(0, s.Inventory.Items.Count, "背包列表空（实例唯一归属已转移）");
            Assert.AreEqual(3, inventory.Count, "开局送拆卸器 1 + 发放 1 + 穿戴 1 = 3 次广播");
            Assert.AreEqual(0, inventory[inventory.Count - 1].EquipmentCount, "穿戴后的快照：0 件");
        }

        [Test]
        public void Equip_BenchUnit_WearsButNoModifiers_UntilPlaced()
        {
            var s = new GameSession(new EventBus(), 0u, Tables());
            Assert.IsTrue(s.StartRun());
            BuyAny(s);
            Unit u = s.Bench[0];
            int id = s.AddEquipment(1);

            Assert.IsTrue(s.Equip(u.Id, 0, id), "备战席单位可以穿（P3-2：穿着待命合法）");
            Assert.AreEqual(100, u.EffectiveStats.AttackPower, "备战席不享受装备加成（只挂场上）");
            Assert.IsNotNull(u.EquipmentSlots[0], "槽位已占有");

            Assert.IsTrue(s.PlaceOnBoard(u.Id, new Pos(1, 0)));
            Assert.AreEqual(108, u.EffectiveStats.AttackPower, "上场瞬间生效（重算制补挂）");
        }

        [Test]
        public void Equip_RejectionMatrix()
        {
            var bus = new EventBus();
            var equipped = new List<EquipmentEquippedEvent>();
            bus.Subscribe<EquipmentEquippedEvent>(e => equipped.Add(e));
            var s = new GameSession(bus, 0u, Tables());

            Assert.IsFalse(s.Equip(1, 0, 1), "非 Prep（PreRun）纯拒绝");
            int id = s.AddEquipment(1);                        // 发放口不设 Phase 门禁（PreRun 也能加）
            Assert.IsTrue(s.StartRun());
            BuyAny(s);
            Unit u = s.Bench[0];

            Assert.IsFalse(s.Equip(9999, 0, id), "未知单位");
            Assert.IsFalse(s.Equip(u.Id, -1, id), "槽号 -1");
            Assert.IsFalse(s.Equip(u.Id, 3, id), "槽号 3（只有 0~2）");
            Assert.IsFalse(s.Equip(u.Id, 0, 4242), "背包无此实例");
            Assert.IsTrue(s.Equip(u.Id, 0, id));
            int id2 = s.AddEquipment(1);
            Assert.IsFalse(s.Equip(u.Id, 0, id2), "槽已占");
            Assert.AreEqual(1, equipped.Count, "全部拒绝零广播——只有一次成功");
            Assert.AreEqual(1, s.Inventory.EquipmentCount, "被拒的那件仍在背包");
        }

        [Test]
        public void Equip_ThreeSlots_ThreeStamps_UnequipStripsOnlyOneSlot()
        {
            var s = new GameSession(new EventBus(), 0u, Tables());
            Assert.IsTrue(s.StartRun());
            Unit u = BuyAndPlace(s, new Pos(0, 0));

            int a = s.AddEquipment(1);
            int b = s.AddEquipment(1);
            int c = s.AddEquipment(1);
            Assert.IsTrue(s.Equip(u.Id, 0, a));
            Assert.IsTrue(s.Equip(u.Id, 1, b));
            Assert.IsTrue(s.Equip(u.Id, 2, c));

            Assert.AreEqual(124, u.EffectiveStats.AttackPower, "三槽三件同叠：100 + 8×3 = 124");
            var stamps = u.Modifiers.Where(m => m.SourceId >= ModSourceId.EquipmentBase
                                             && m.SourceId < ModSourceId.EquipmentBase + 3)
                                    .Select(m => m.SourceId).OrderBy(x => x).ToArray();
            CollectionAssert.AreEqual(new[] { 1000, 1001, 1002 }, stamps, "盖章 = 1000 + 槽位号，三槽三章");

            Assert.IsTrue(s.Unequip(u.Id, 1), "卸下 1 号槽");
            Assert.AreEqual(116, u.EffectiveStats.AttackPower, "只摘 1 号槽的章：100 + 8×2 = 116");
        }

        // ── 重算制（P3-2：与羁绊/策略同构） ──

        [Test]
        public void Equipment_Recalculate_TakeOffStrips_PlaceOnReapplies()
        {
            var s = new GameSession(new EventBus(), 0u, Tables());
            Assert.IsTrue(s.StartRun());
            Unit u = BuyAndPlace(s, new Pos(0, 0));
            int id = s.AddEquipment(1);
            Assert.IsTrue(s.Equip(u.Id, 0, id));
            Assert.AreEqual(108, u.EffectiveStats.AttackPower);

            Assert.IsTrue(s.TakeOffBoard(u.Id));
            Assert.AreEqual(100, u.EffectiveStats.AttackPower, "下场即摘干净（备战席不享受）");

            Assert.IsTrue(s.PlaceOnBoard(u.Id, new Pos(0, 0)));
            Assert.AreEqual(108, u.EffectiveStats.AttackPower, "重新上场补挂");
        }

        [Test]
        public void Equipment_MaxHpLinkage_OnEquipAndTakeOff()
        {
            var s = new GameSession(new EventBus(), 0u, Tables());
            Assert.IsTrue(s.StartRun());
            Unit u = BuyAndPlace(s, new Pos(0, 0));
            Assert.AreEqual(500, u.EffectiveStats.MaxHp);

            int id = s.AddEquipment(3);                        // 防御散件：生命 +40、双抗 +5
            Assert.IsTrue(s.Equip(u.Id, 0, id));
            Assert.AreEqual(540, u.EffectiveStats.MaxHp, "生命上限 500 + 40");
            Assert.AreEqual(540, u.CurrentHp, "满血穿上：ApplyMaxHpDelta 等量加血");
            Assert.AreEqual(5, u.EffectiveStats.Armor, "护甲 0 + 5");

            Assert.IsTrue(s.TakeOffBoard(u.Id));
            Assert.AreEqual(500, u.EffectiveStats.MaxHp, "下场摘账：上限回 500");
            Assert.AreEqual(500, u.CurrentHp, "收缩钳制：540 → 500（不是按差值减）");
        }

        // ── 卸下与拆卸器 ──

        [Test]
        public void Unequip_ConsumesTool_ReturnsGearToInventory()
        {
            var bus = new EventBus();
            var unequipped = new List<EquipmentUnequippedEvent>();
            bus.Subscribe<EquipmentUnequippedEvent>(e => unequipped.Add(e));
            var s = new GameSession(bus, 0u, Tables());
            Assert.IsTrue(s.StartRun());
            Unit u = BuyAndPlace(s, new Pos(0, 0));
            int id = s.AddEquipment(1);
            Assert.IsTrue(s.Equip(u.Id, 0, id));
            Assert.AreEqual(2, s.UnsmithToolCount, "开局 2 个拆卸器");

            Assert.IsTrue(s.Unequip(u.Id, 0));
            Assert.AreEqual(1, s.UnsmithToolCount, "卸下一次消耗一个");
            Assert.IsNull(u.EquipmentSlots[0], "槽已空");
            Assert.IsNotNull(s.Inventory.Find(id), "装备回背包（同一实例号）");
            Assert.AreEqual(100, u.EffectiveStats.AttackPower, "修饰器已摘");
            Assert.AreEqual(1, unequipped.Count);
            Assert.AreEqual(u.Id, unequipped[0].UnitId);
            Assert.AreEqual(id, unequipped[0].InstanceId);
            Assert.AreEqual(1, unequipped[0].DefId);
            Assert.AreEqual(0, unequipped[0].SlotIndex);
        }

        [Test]
        public void Unequip_RejectionMatrix_EmptySlot_NoTools_NotPrep()
        {
            var s = new GameSession(new EventBus(), 0u, Tables());
            Assert.IsTrue(s.StartRun());
            BuyAny(s);
            Unit u = s.Bench[0];

            Assert.IsFalse(s.Unequip(u.Id, 0), "空槽拒绝");
            Assert.IsFalse(s.Unequip(9999, 0), "未知单位");
            Assert.IsFalse(s.Unequip(u.Id, 3), "槽号越界");

            // 耗光拆卸器（开局 2 个：卸两次用完）——先穿两件再卸两次
            int a = s.AddEquipment(1);
            int b = s.AddEquipment(1);
            Assert.IsTrue(s.Equip(u.Id, 0, a));
            Assert.IsTrue(s.Equip(u.Id, 1, b));
            Assert.IsTrue(s.Unequip(u.Id, 0));
            Assert.IsTrue(s.Unequip(u.Id, 1));
            Assert.AreEqual(0, s.UnsmithToolCount, "拆卸器已耗光");
            Assert.AreEqual(2, s.Inventory.EquipmentCount, "a、b 两件都在背包");

            int c = s.AddEquipment(1);
            Assert.IsTrue(s.Equip(u.Id, 0, c));
            Assert.IsFalse(s.Unequip(u.Id, 0), "无拆卸器拒绝");
            Assert.IsNotNull(u.EquipmentSlots[0], "被拒后槽内装备原样（纯拒绝四不动）");
            Assert.AreEqual(2, s.Inventory.EquipmentCount, "背包没多出装备（仍是 a、b）");

            // 非 Prep 拒绝：上场打一场空编成（秒胜进 Settlement）
            Assert.IsTrue(s.PlaceOnBoard(u.Id, new Pos(0, 0)));
            Assert.IsTrue(s.StartBattle());
            Assert.AreEqual(GamePhase.Settlement, s.Phase);
            Assert.IsFalse(s.Unequip(u.Id, 0), "Settlement 期拒绝");
        }

        // ── 升级 ──

        [Test]
        public void Upgrade_InPlace_OnceOnly_ExactLedger()
        {
            var bus = new EventBus();
            var upgraded = new List<EquipmentUpgradedEvent>();
            bus.Subscribe<EquipmentUpgradedEvent>(e => upgraded.Add(e));
            var s = new GameSession(bus, 0u, Tables());
            Assert.IsTrue(s.StartRun());
            Unit u = BuyAndPlace(s, new Pos(0, 0));
            int id = s.AddEquipment(1);                        // 物理散件
            Assert.IsTrue(s.Equip(u.Id, 0, id));
            Assert.AreEqual(108, u.EffectiveStats.AttackPower);
            s.Eco.AddGold(10);

            Assert.IsTrue(s.UpgradeEquipment(u.Id, 0, 0), "升 0 号分支（11 输出，5 金）");
            Assert.AreEqual(120, u.EffectiveStats.AttackPower, "原地换效果：100 + 20 = 120（散件 +8 摘、大件 +20 挂）");
            Assert.AreEqual(11, u.EquipmentSlots[0].DefId, "原地升级：槽位不变、DefId 改写");
            Assert.AreEqual(id, u.EquipmentSlots[0].InstanceId, "实例号不变（同一件装备）");
            Assert.AreEqual(1, upgraded.Count);
            Assert.AreEqual(1, upgraded[0].OldDefId);
            Assert.AreEqual(11, upgraded[0].NewDefId, "事件携带所选分支");
            Assert.AreEqual(5, upgraded[0].GoldSpent);

            Assert.IsFalse(s.UpgradeEquipment(u.Id, 0, 0), "一次定型：大件不可再升");
            Assert.IsFalse(s.UpgradeEquipment(u.Id, 0, 1), "不可换分支（重铸不存在，#12③）");
        }

        [Test]
        public void Upgrade_BranchSelection_CostsAndEvents()
        {
            // 分支 0 = 21 回蓝（5 金）：命中回蓝 +5、每秒回蓝 +1（不带法强维——散件 +8 被摘）
            {
                var bus = new EventBus();
                var upgraded = new List<EquipmentUpgradedEvent>();
                bus.Subscribe<EquipmentUpgradedEvent>(e => upgraded.Add(e));
                var s = new GameSession(bus, 0u, Tables());
                Assert.IsTrue(s.StartRun());
                Unit u = BuyAndPlace(s, new Pos(0, 0));
                int g = s.AddEquipment(2);                     // 法术散件：法强 +8
                Assert.IsTrue(s.Equip(u.Id, 0, g));
                Assert.AreEqual(108, u.EffectiveStats.AbilityPower, "散件法强 100 + 8");
                s.Eco.AddGold(10);

                Assert.IsTrue(s.UpgradeEquipment(u.Id, 0, 0), "选 0 号分支 = 21 回蓝");
                Assert.AreEqual(21, u.EquipmentSlots[0].DefId);
                Assert.AreEqual(5, u.EffectiveStats.ManaPerHit, "命中回蓝 0 + 5");
                Assert.AreEqual(1, u.EffectiveStats.ManaRegenPerSecond, "每秒回蓝 0 + 1");
                Assert.AreEqual(100, u.EffectiveStats.AbilityPower, "21 号不带法强维：摘散件后法强回 100（分支取舍）");
                Assert.AreEqual(5, upgraded[0].GoldSpent, "21 号分支费 5 金");
            }

            // 分支 1 = 22 法强（6 金）：法强 +20
            {
                var bus = new EventBus();
                var upgraded = new List<EquipmentUpgradedEvent>();
                bus.Subscribe<EquipmentUpgradedEvent>(e => upgraded.Add(e));
                var s = new GameSession(bus, 0u, Tables());
                Assert.IsTrue(s.StartRun());
                Unit u = BuyAndPlace(s, new Pos(0, 0));
                int g = s.AddEquipment(2);
                Assert.IsTrue(s.Equip(u.Id, 0, g));
                s.Eco.AddGold(10);
                int goldBefore = s.Eco.Gold;

                Assert.IsTrue(s.UpgradeEquipment(u.Id, 0, 1), "选 1 号分支 = 22 法强");
                Assert.AreEqual(22, u.EquipmentSlots[0].DefId);
                Assert.AreEqual(120, u.EffectiveStats.AbilityPower, "法强大件：100 + 20 = 120");
                Assert.AreEqual(goldBefore - 6, s.Eco.Gold, "22 号分支费 6 金（TrySpend 严格扣款）");
                Assert.AreEqual(22, upgraded[0].NewDefId);
                Assert.AreEqual(6, upgraded[0].GoldSpent, "事件携带实付金额");
            }
        }

        [Test]
        public void Upgrade_RejectionMatrix()
        {
            var s = new GameSession(new EventBus(), 0u, Tables());
            Assert.IsTrue(s.StartRun());
            BuyAny(s);
            Unit u = s.Bench[0];

            Assert.IsFalse(s.UpgradeEquipment(u.Id, 0, 0), "空槽拒绝");

            int id = s.AddEquipment(1);
            Assert.IsTrue(s.Equip(u.Id, 0, id));
            Assert.IsFalse(s.UpgradeEquipment(u.Id, 0, -1), "分支号 -1");
            Assert.IsFalse(s.UpgradeEquipment(u.Id, 0, 3), "分支号 3（物理散件只有 0~2）");
            Assert.IsFalse(s.UpgradeEquipment(u.Id, 5, 0), "槽号越界");
            Assert.IsFalse(s.UpgradeEquipment(9999, 0, 0), "未知单位");

            int goldBefore = s.Eco.Gold;                       // 开局 3 金 - 买棋 1 金 = 2 金 < 5
            Assert.IsFalse(s.UpgradeEquipment(u.Id, 0, 0), "钱不够（2 金 < 5 金）纯拒绝");
            Assert.AreEqual(goldBefore, s.Eco.Gold, "一分不扣");
            Assert.AreEqual(1, u.EquipmentSlots[0].DefId, "装备未被改动");
        }

        // ── 卖棋子返还 ──

        [Test]
        public void Sell_ReturnsAllGear_IncludingUpgraded()
        {
            var bus = new EventBus();
            var inventory = new List<InventoryChangedEvent>();
            bus.Subscribe<InventoryChangedEvent>(e => inventory.Add(e));
            var s = new GameSession(bus, 0u, Tables());
            Assert.IsTrue(s.StartRun());
            Unit u = BuyAndPlace(s, new Pos(0, 0));
            int a = s.AddEquipment(1);
            int b = s.AddEquipment(2);
            Assert.IsTrue(s.Equip(u.Id, 0, a));
            Assert.IsTrue(s.Equip(u.Id, 1, b));
            s.Eco.AddGold(10);
            Assert.IsTrue(s.UpgradeEquipment(u.Id, 0, 0));     // a 升成大件 11

            Assert.IsTrue(s.Sell(u.Id));
            Assert.AreEqual(2, s.Inventory.EquipmentCount, "两件全部返还（含大件）");
            Assert.AreEqual(11, s.Inventory.Find(a).DefId, "大件原样回包（升级不因卖出而回退）");
            Assert.AreEqual(2, s.Inventory.Find(b).DefId);
            Assert.AreEqual(2, inventory[inventory.Count - 1].EquipmentCount, "卖返还广播背包快照（2 件）");
        }

        [Test]
        public void Sell_UpgradeFeeNotRefunded()
        {
            var s = new GameSession(new EventBus(), 0u, Tables());
            Assert.IsTrue(s.StartRun());
            Unit u = BuyAndPlace(s, new Pos(0, 0));
            s.Eco.AddGold(10);
            // 时间线：3 + 10 = 13 → 买棋 -1 = 12 → 升级 -5 = 7 → 卖出 +1（1 费 1 星卖价）= 8
            int id = s.AddEquipment(1);
            Assert.IsTrue(s.Equip(u.Id, 0, id));
            Assert.IsTrue(s.UpgradeEquipment(u.Id, 0, 0));
            Assert.IsTrue(s.Sell(u.Id));

            Assert.AreEqual(8, s.Eco.Gold, "卖价只按棋子算（1 金）；升级费 5 金不退（#12⑥ 防升级再卖出刷钱）");
            Assert.AreEqual(1, s.Inventory.EquipmentCount, "装备本体回包（只是不折现）");
        }

        // ── 合成（2026-10-29 拍板：锚点保留、材料回背包） ──

        [Test]
        public void Merge_AnchorKeepsGear_MaterialGearReturns()
        {
            var s = new GameSession(new EventBus(), 0u, Tables());
            Assert.IsTrue(s.StartRun());

            BuyAny(s);                                          // 第 1 张（锚点候选：备战席最早者）
            Unit anchor = s.Bench[0];
            BuyAny(s);                                          // 第 2 张（材料）
            Unit material = s.Bench[1];

            int gearA = s.AddEquipment(1);
            int gearB = s.AddEquipment(2);
            Assert.IsTrue(s.Equip(anchor.Id, 0, gearA), "锚点穿物理散件");
            Assert.IsTrue(s.Equip(material.Id, 0, gearB), "材料穿法术散件");

            BuyAny(s);                                          // 第 3 张 → 触发合成（3 金刚好买 3 张）

            Assert.AreEqual(1, s.Bench.Count, "合成后只剩合成体");
            Unit merged = s.Bench[0];
            Assert.AreEqual(2, merged.Star, "2 星合成体");
            Assert.AreEqual(gearA, merged.EquipmentSlots[0].InstanceId, "锚点装备继承给合成体（同一实例）");
            Assert.AreEqual(1, merged.EquipmentSlots[0].DefId);
            Assert.IsNull(merged.EquipmentSlots[1], "材料装备不跟合成体");
            Assert.IsNotNull(s.Inventory.Find(gearB), "材料装备退回背包");
            Assert.AreEqual(2, s.Inventory.Find(gearB).DefId, "退回的是原样散件（不升级不销毁）");
            Assert.AreEqual(1, s.Inventory.EquipmentCount);
        }

        // ── 无表容忍 + 快照事件 + 相位门禁 + 兜底重算 ──

        [Test]
        public void NoEquipmentTable_AllOperationsReject_EnterPrepSurvives()
        {
            // 内联注入包（不走 Tables 助手——助手的 ?? 默认值会吞掉 null）
            var s = new GameSession(new EventBus(), 0u, new GameTables
            {
                Characters = SingleChar(),
                Odds = ShopOdds.Default,
                Rounds = RoundTable.Default,
                Enemies = EnemyTable.Empty,
                Synergies = SynergyTable.Default,
                Ores = OreTable.Default,
                Policies = new PolicyTable(new PolicyDef[0]),
                Skills = SkillTable.Default,
                Equipment = null,                              // 无装备表
            });
            Assert.AreEqual(0, s.AddEquipment(1), "无表：发放返回 0");
            Assert.IsTrue(s.StartRun());
            BuyAny(s);
            Unit u = s.Bench[0];
            Assert.IsFalse(s.Equip(u.Id, 0, 1), "无表：穿戴拒绝");
            Assert.IsFalse(s.UpgradeEquipment(u.Id, 0, 0), "无表：升级拒绝");

            // 兜底重算不炸：打一场空编成（秒胜）→ StartPrep → EnterPrep 第 6 步三合一重算
            Assert.IsTrue(s.PlaceOnBoard(u.Id, new Pos(0, 0)));
            Assert.IsTrue(s.StartBattle());
            Assert.IsTrue(s.StartPrep());
            Assert.AreEqual(GamePhase.Prep, s.Phase, "无表也能正常走完回合循环（回归红线）");
        }

        [Test]
        public void InventoryChanged_SnapshotFields_AcrossOperations()
        {
            var bus = new EventBus();
            var events = new List<InventoryChangedEvent>();
            bus.Subscribe<InventoryChangedEvent>(e => events.Add(e));
            var s = new GameSession(bus, 0u, Tables());
            Assert.IsTrue(s.StartRun());                        // 事件 0：(0, 2)
            BuyAny(s);
            Unit u = s.Bench[0];
            int id = s.AddEquipment(1);                         // 事件 1：(1, 2)
            Assert.IsTrue(s.Equip(u.Id, 0, id));                // 事件 2：(0, 2)
            Assert.IsTrue(s.Unequip(u.Id, 0));                  // 事件 3：(1, 1)——拆卸器消耗

            Assert.AreEqual(4, events.Count);
            Assert.AreEqual(0, events[0].EquipmentCount); Assert.AreEqual(2, events[0].UnsmithTools);
            Assert.AreEqual(1, events[1].EquipmentCount); Assert.AreEqual(2, events[1].UnsmithTools);
            Assert.AreEqual(0, events[2].EquipmentCount); Assert.AreEqual(2, events[2].UnsmithTools);
            Assert.AreEqual(1, events[3].EquipmentCount); Assert.AreEqual(1, events[3].UnsmithTools);
        }

        [Test]
        public void EquipmentOps_RejectedOutsidePrep()
        {
            var s = new GameSession(new EventBus(), 0u, Tables());
            Assert.IsTrue(s.StartRun());
            Unit u = BuyAndPlace(s, new Pos(0, 0));
            int id = s.AddEquipment(1);
            Assert.IsTrue(s.Equip(u.Id, 0, id));

            Assert.IsTrue(s.StartBattle());                     // 空编成秒胜 → Settlement
            Assert.AreEqual(GamePhase.Settlement, s.Phase);
            int id2 = s.AddEquipment(1);                        // 发放口不设 Phase 门禁（批次6 掉落接入用）
            Assert.IsFalse(s.Equip(u.Id, 1, id2), "Settlement 期穿戴拒绝");
            Assert.IsFalse(s.Unequip(u.Id, 0), "Settlement 期卸下拒绝");
            Assert.IsFalse(s.UpgradeEquipment(u.Id, 0, 0), "Settlement 期升级拒绝");
        }

        [Test]
        public void EnterPrep_RecalculatesEquipmentModifiers()
        {
            var s = new GameSession(new EventBus(), 0u, Tables());
            Assert.IsTrue(s.StartRun());
            Unit u = BuyAndPlace(s, new Pos(0, 0));
            int id = s.AddEquipment(1);
            Assert.IsTrue(s.Equip(u.Id, 0, id));
            Assert.AreEqual(108, u.EffectiveStats.AttackPower);

            Assert.IsTrue(s.StartBattle());                     // 空编成秒胜（Settle 内联）
            Assert.IsTrue(s.StartPrep());                       // EnterPrep 第 6 步三合一兜底重算
            Assert.AreEqual(108, u.EffectiveStats.AttackPower, "跨回合后装备修饰器由兜底重算重新挂上");
        }
    }
}
