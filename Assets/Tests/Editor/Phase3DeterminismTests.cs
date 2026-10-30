using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AutoChess.Game;
using NUnit.Framework;

namespace AutoChess.Tests
{
    /// <summary>
    /// 阶段3 批次6：确定性总验收（阶段3 收官）——把 Phase2DeterminismTests 的"同种子同操作=同结果"
    /// 端到端套路扩展到阶段3 全部新系统：装备（矿发/策发/穿戴/拆卸/升级）、技能（施法循环）、
    /// Buff（策略钩子驱动的灼烧 DoT）、战斗钩子（增伤乘区）。
    ///
    /// 场景（注入包 + 固定脚本，无决策器——操作序列写死，两 Session 必然同步）：
    ///  · R1 奖励关：验收术士（带技能 3 奥术飞弹：魔法 / 基础 30 / 法强系数 40 / 施法距离 3）
    ///    vs 双挡位矿鼠（T1 装备矿 / T2 拆卸器矿）→ 必胜 → 掉两矿；
    ///  · R2 备战：捡装备矿（物理散件入包）→ 捡拆卸器矿（+2）→ 选策略「魔契」
    ///    （法术散件 + 拆卸器 ×1 + 增伤 20% 钩子 + 敌方灼烧钩子）
    ///    → 穿物理散件（槽 0）→ 升级成分支 0「输出」（+20 攻，花 5 金）
    ///    → 穿法术散件（槽 1）→ 卸下（耗 1 拆卸器）→ 再穿回；
    ///  · R2 普通关：强化后的术士（攻 40+20 / 法强 100+8 / 增伤 20% / 敌方灼烧）vs 石像（600 血）→ 必胜；
    ///  · 对比点：R3 备战期的全部状态 + 34 种事件全字段的完整事件流。
    ///
    /// 分节契约：
    ///  · 主验收：3 组种子 × 双 Session——事件流逐条一致（34 种事件全捕获 = Phase2 的 30 种 + 装备四事件；
    ///    先整体快速比对、失败定位首处差异）+ 终局状态逐项一致（血/金/战斗种子/已选策略/背包/
    ///    拆卸器/装备槽/有效面板——阶段5 快照重演的地基）；
    ///  · 指纹验收（防空转假绿）：技能施法/装备四事件/矿两类奖励/策略选定/灼烧跳伤/增伤乘幅
    ///    各自必须在事件流里留痕，且关键数值精确锁定（攻 60 = 40+20、法强 108 = 100+8、
    ///    拆卸器 4 = 开局 2 + 矿 2 + 策 1 - 卸 1、金币 10 = 3 - 1买 + 5胜 + 2策 - 5升 + 5胜 + 1连胜）。
    /// </summary>
    [TestFixture]
    public class Phase3DeterminismTests
    {
        // ─────────────────────── 测试基建：事件全捕获（34 种） ───────────────────────

        /// <summary>34 种事件全订阅，每条拼全字段成一行（int/bool/枚举全可字符串化——无浮点，逐字节可比）。
        /// 与 Phase2DeterminismTests 的差异：+装备四事件（Equipped/Unequipped/Upgraded/Inv）。</summary>
        private sealed class EventRecorder
        {
            public readonly List<string> Log = new List<string>(4096);

            private void Add(string line) => Log.Add(line);

            public void SubscribeAll(EventBus bus)
            {
                bus.Subscribe<UnitPlacedEvent>(e => Add($"Placed|{e.UnitId}|{e.Cell.X},{e.Cell.Y}|{e.IsPlayerSide}"));
                bus.Subscribe<UnitMovedEvent>(e => Add($"Moved|{e.UnitId}|{e.FromCell.X},{e.FromCell.Y}|{e.ToCell.X},{e.ToCell.Y}"));
                bus.Subscribe<UnitRemovedEvent>(e => Add($"Removed|{e.UnitId}|{e.Cell.X},{e.Cell.Y}"));
                bus.Subscribe<UnitAttackedEvent>(e => Add($"Attacked|{e.AttackerId}|{e.TargetId}|{e.IsCrit}"));
                bus.Subscribe<SkillCastEvent>(e => Add($"SkillCast|{e.CasterId}|{e.SkillId}|{e.TargetId}|{e.IsCrit}"));
                bus.Subscribe<UnitDamagedEvent>(e => Add($"Damaged|{e.UnitId}|{e.SourceUnitId}|{e.FinalDamage}|{e.CurrentHp}"));
                bus.Subscribe<UnitDiedEvent>(e => Add($"Died|{e.UnitId}|{e.Cell.X},{e.Cell.Y}"));
                bus.Subscribe<UnitHealedEvent>(e => Add($"Healed|{e.UnitId}|{e.Amount}|{e.CurrentHp}"));
                bus.Subscribe<UnitMaxHpChangedEvent>(e => Add($"MaxHp|{e.UnitId}|{e.MaxHp}|{e.CurrentHp}"));
                bus.Subscribe<UnitRestoredEvent>(e => Add($"Restored|{e.UnitId}|{e.CurrentHp}"));
                bus.Subscribe<BattleEndedEvent>(e => Add($"BattleEnded|{e.PlayerWon}|{e.TotalTicks}"));
                bus.Subscribe<ManaChangedEvent>(e => Add($"Mana|{e.UnitId}|{e.CurrentMana}|{e.MaxMana}"));
                bus.Subscribe<GoldChangedEvent>(e => Add($"Gold|{e.Gold}|{e.Delta}"));
                bus.Subscribe<XpChangedEvent>(e => Add($"Xp|{e.Level}|{e.XpProgress}|{e.XpToNextLevel}"));
                bus.Subscribe<LevelUpEvent>(e => Add($"LevelUp|{e.Level}"));
                bus.Subscribe<IncomeSettledEvent>(e => Add(
                    $"Income|{e.Bill.FixedGold}|{e.Bill.InterestGold}|{e.Bill.StreakGold}|{e.Bill.TotalGold}|{e.Bill.WinStreak}"));
                bus.Subscribe<ShopRolledEvent>(e => Add("Rolled|" + string.Join(",", e.CharIds)));
                bus.Subscribe<ShopBoughtEvent>(e => Add($"Bought|{e.SlotIndex}|{e.CharId}|{e.GoldLeft}"));
                bus.Subscribe<SynergiesChangedEvent>(e =>
                {
                    var sb = new StringBuilder("Synergies");
                    foreach (var info in e.Snapshot) sb.Append($"|{info.SynergyId}:{info.Count}:{info.TierIndex}");
                    Add(sb.ToString());
                });
                bus.Subscribe<HpChangedEvent>(e => Add($"Hp|{e.Hp}|{e.Delta}"));
                bus.Subscribe<UnitAcquiredEvent>(e => Add($"Acquired|{e.UnitId}|{e.CharId}|{e.Star}"));
                bus.Subscribe<UnitSoldEvent>(e => Add($"Sold|{e.UnitId}|{e.CharId}|{e.Star}|{e.Gold}"));
                bus.Subscribe<UnitMergedEvent>(e => Add(
                    $"Merged|{e.ResultUnitId}|{e.CharId}|{e.NewStar}|" + string.Join(",", e.ConsumedUnitIds)));
                bus.Subscribe<RoundStartedEvent>(e => Add($"RoundStarted|{e.RoundNumber}|{e.Type}"));
                bus.Subscribe<RoundEndedEvent>(e => Add($"RoundEnded|{e.Round}|{e.PlayerWon}|{e.DamageTaken}|{e.GoldGained}"));
                bus.Subscribe<RunFinishedEvent>(e => Add($"RunFinished|{e.Rating}|{e.RoundsCompleted}|{e.FinalHp}"));
                bus.Subscribe<OreDroppedEvent>(e => Add($"OreDropped|{e.OreId}|{e.Tier}|{e.Cell.X},{e.Cell.Y}"));
                bus.Subscribe<OreOpenedEvent>(e => Add(
                    $"OreOpened|{e.OreId}|{e.Tier}|{e.Cell.X},{e.Cell.Y}|{e.OreDefId}|{e.RewardType}|{e.Amount}|{e.EquipmentId}"));
                bus.Subscribe<PolicyOfferedEvent>(e => Add("PolicyOffered|" + string.Join(",", e.CandidatePolicyIds)));
                bus.Subscribe<PolicyPickedEvent>(e => Add($"PolicyPicked|{e.Index}|{e.PolicyId}|{e.InstanceSourceId}"));
                bus.Subscribe<EquipmentEquippedEvent>(e => Add($"Equipped|{e.UnitId}|{e.InstanceId}|{e.DefId}|{e.SlotIndex}"));
                bus.Subscribe<EquipmentUnequippedEvent>(e => Add($"Unequipped|{e.UnitId}|{e.InstanceId}|{e.DefId}|{e.SlotIndex}"));
                bus.Subscribe<EquipmentUpgradedEvent>(e => Add(
                    $"Upgraded|{e.UnitId}|{e.InstanceId}|{e.OldDefId}|{e.NewDefId}|{e.SlotIndex}|{e.GoldSpent}"));
                bus.Subscribe<InventoryChangedEvent>(e => Add($"Inv|{e.EquipmentCount}|{e.UnsmithTools}"));
            }

            /// <summary>首处差异定位（整体比对失败后调用，报行号与两行内容）。</summary>
            public static string FirstDifference(List<string> a, List<string> b)
            {
                int n = Math.Min(a.Count, b.Count);
                for (int i = 0; i < n; i++)
                    if (a[i] != b[i]) return $"第 {i} 行：A[{a[i]}] vs B[{b[i]}]";
                return a.Count != b.Count ? $"条数 {a.Count} vs {b.Count}（公共前缀全同）" : "无差异";
            }
        }

        // ─────────────────────── 场景内容：灼烧钩子（策略 HookFactory 的产物） ───────────────────────

        /// <summary>
        /// 生产接线的演示与验收载体（批次4 预留的"按内容挂"落点）：
        /// 策略钩子工厂每场新造一只——开战给全体敌方挂灼烧（BuffTable.Default id 2：DoT 1 每跳、90 tick），
        /// OnTickEnd 驱动 BuffSystem 结算（DoT → Hot → 计时递减 → 到期摘层）。
        /// 钩子持 BuffSystem 实例（随钩子每场新建，不跨场）——与羁绊钩子同款生命周期纪律。
        /// </summary>
        private sealed class BurnEnemiesHook : IBattleHook
        {
            private readonly BuffSystem _buffs;

            public int SourceId => ModSourceId.TestBase + 2;

            public BurnEnemiesHook(EventBus bus)
                => _buffs = new BuffSystem(BuffTable.Default, bus);

            public void OnBattleStart(BattleContext ctx)
            {
                var enemies = new List<Unit>();
                ctx.Board.CollectUnitsOnBoard(false, enemies);
                for (int i = 0; i < enemies.Count; i++)
                    _buffs.Apply(enemies[i], new BuffApplier(2, SourceId));   // 2 = 灼烧
            }

            public void OnBeforeAttack(BattleContext ctx, ref AttackDraft draft) { }
            public void OnAfterAttack(BattleContext ctx, in AttackResult result) { }
            public void OnDamaged(BattleContext ctx, in DamageResult result) { }
            public void OnDeath(BattleContext ctx, in DeathInfo death) { }
            public void OnTickEnd(BattleContext ctx) => _buffs.TickAll();
        }

        // ─────────────────────── 场景表组 ───────────────────────

        /// <summary>注入包：单角色（验收术士，1 费带技能 3）/ 双回合编成 / 三挡单条池矿池 / 单条复合策略。
        /// 矿池 T1/T2 各一条（抽取必中、断言无随机歧义）；策略四效果 = 装备 + 拆卸器 + 增伤钩子 + 灼烧钩子。</summary>
        private static GameTables BuildTables(EventBus bus)
        {
            var casterStats = new UnitStats
            {
                MaxHp = 400, AttackPower = 40, Armor = 0, MagicResist = 0, AbilityPower = 100,
                AttackRange = 5, AttackInterval = 1f, MoveInterval = 1f,
                CritChance = 0, CritDamage = 50,
                MaxMana = 30, ManaPerHit = 10, ManaRegenPerSecond = 0,
            };
            var chars = new CharacterTable(new[]
            {
                new CharacterDef(1, "验收术士", 1, casterStats, Array.Empty<SynergyId>(), RoleId.Mage, 3),
            });

            var enemies = new EnemyTable(new[]
            {
                new EnemyRoundEntry(1, new[]
                {
                    // 200 血：3 次普攻（120）后剩 80 > 技能 70 → 首跳施法非致死（全值 70 上事件流，
                    // 不吃 TakeDamage 的死亡截断——基准对照行的断言依据）
                    new EnemySpawn("矿鼠甲", FoeStats(200, 0), new Pos(3, 4), 1, 1),   // T1 装备矿
                    new EnemySpawn("矿鼠乙", FoeStats(200, 0), new Pos(5, 4), 2, 1),   // T2 拆卸器矿
                }),
                new EnemyRoundEntry(2, new[]
                {
                    new EnemySpawn("石像", FoeStats(600, 5), new Pos(4, 4), 0, 2),
                }),
            });

            var ores = new OreTable(new[]
            {
                new OreDef(11, OreRewardType.Equipment, "物理散件", 0, 1),
                new OreDef(21, OreRewardType.UnsmithTool, "拆卸器 ×2", 2, 0),
                new OreDef(31, OreRewardType.Gold, "金币占位", 1, 0),   // T3 占位（三挡齐全校验）
            }, new[]
            {
                new OreTierDef(1, new[] { 11 }),
                new OreTierDef(2, new[] { 21 }),
                new OreTierDef(3, new[] { 31 }),
            });

            var policies = new PolicyTable(new[]
            {
                new PolicyDef(1, "魔契", "法术散件 + 拆卸器 + 增伤 + 灼烧", new PolicyEffect[]
                {
                    PolicyEffect.Gear(2),                                   // 法术散件入包
                    PolicyEffect.UnsmithTools(1),                           // 拆卸器 +1（P3-8）
                    new PolicyEffect(PolicyEffectType.Gold, 1, null, 0,     // 增伤 20%（批次5 钩子库）
                        _ => new CombatHookLibrary.DamageAmpHook(20f, ModSourceId.TestBase + 1)),
                    new PolicyEffect(PolicyEffectType.Gold, 1, null, 0,     // 敌方灼烧（批次4 Buff 系统）
                        _ => new BurnEnemiesHook(bus)),
                }),
            });

            return new GameTables
            {
                Characters = chars,
                Odds = AllCostOne(),
                Rounds = RoundTable.Default,
                Enemies = enemies,
                Synergies = SynergyTable.Default,
                Ores = ores,
                Policies = policies,
                Skills = SkillTable.Default,
                Equipment = EquipmentTable.Default,
            };
        }

        private static UnitStats FoeStats(int hp, int atk)
            => new UnitStats
            {
                MaxHp = hp, AttackPower = atk, Armor = 0, MagicResist = 0, AbilityPower = 100,
                AttackRange = 5, AttackInterval = 2f, MoveInterval = 1f,
                CritChance = 0, CritDamage = 50, MaxMana = 0, ManaPerHit = 0, ManaRegenPerSecond = 0,
            };

        private static ShopOdds AllCostOne()
        {
            var t = new int[9, 6];
            for (int level = 2; level <= 8; level++) t[level, 1] = 100;
            return new ShopOdds(t);
        }

        // ─────────────────────── 脚本化场景（固定操作序列） ───────────────────────

        /// <summary>跑完 R1+R2 两回合，停在 R3 备战（对比点）。返回 (会话, 事件记录器)。</summary>
        private static (GameSession s, EventRecorder rec) PlayScenario(uint seed)
        {
            var bus = new EventBus();
            var rec = new EventRecorder();
            rec.SubscribeAll(bus);
            var s = new GameSession(bus, seed, BuildTables(bus));

            // ── R1 备战：买术士上场 ──
            Assert.IsTrue(s.StartRun());
            Assert.IsTrue(s.Buy(0), "1 费棋：开局 3 金买得起（单角色全 1 费表 → 槽 0 恒有货）");
            int casterId = s.Bench[0].Id;
            Assert.AreEqual(3, s.Bench[0].SkillId, "买的棋带技能 3（编表整链路）");
            Assert.IsTrue(s.PlaceOnBoard(casterId, new Pos(4, 2)));
            BattleToEnd(s);   // R1 奖励关：杀双鼠 → 掉 T1/T2 各一矿

            // ── R2 备战：矿 → 策略 → 装备四连 ──
            Assert.IsTrue(s.StartPrep());
            Assert.AreEqual(2, s.OresOnGround.Count, "双鼠各掉一矿");
            int gearOre = s.OresOnGround.First(d => d.Tier == 1).Id;
            int toolOre = s.OresOnGround.First(d => d.Tier == 2).Id;
            Assert.IsTrue(s.PickOre(gearOre), "装备矿：物理散件入包（批次6 接线）");
            Assert.IsTrue(s.PickOre(toolOre), "拆卸器矿：+2（P3-8）");
            Assert.IsTrue(s.HasPolicyChoice, "R1 奖励关胜 → R2 弹选");
            Assert.IsTrue(s.PickPolicy(0), "选魔契（单条表 → 候选必是它）");

            int gearInstance = s.Inventory.Items.Single(x => x.DefId == 1).InstanceId;
            Assert.IsTrue(s.Equip(casterId, 0, gearInstance), "物理散件 → 槽 0");
            Assert.IsTrue(s.UpgradeEquipment(casterId, 0, 0), "升级成分支 0 = 输出（+20 攻，花 5 金）");
            int apGear = s.Inventory.Items.Single(x => x.DefId == 2).InstanceId;
            Assert.IsTrue(s.Equip(casterId, 1, apGear), "法术散件 → 槽 1");
            Assert.IsTrue(s.Unequip(casterId, 1), "卸下（耗 1 拆卸器——#12⑦ 消耗品）");
            Assert.IsTrue(s.Equip(casterId, 1, apGear), "再穿回（拆卸器可换人语义的最小环）");

            // ── R2 普通关：强化术士 vs 石像 ──
            BattleToEnd(s);
            Assert.IsTrue(s.StartPrep(), "进 R3 备战（对比点）");
            return (s, rec);
        }

        /// <summary>开战 → 驱动到分晓 → 排空回调结算（Phase2DeterminismTests 同款链路）。</summary>
        private static void BattleToEnd(GameSession s)
        {
            Assert.IsTrue(s.StartBattle(), "开战（门禁已被脚本消费）");
            int guard = 0;
            while (!s.Combat.IsOver && guard++ < 6000) s.Combat.Tick();
            Assert.IsTrue(s.Combat.IsOver, "MaxTicks 兜底必然分晓");
            Assert.IsTrue(s.SettleBattle(), "排空回调结算");
        }

        // ─────────────────────── 主验收：3 组种子 × 双 Session 逐条一致 ───────────────────────

        [Test]
        public void FullScenario_SameSeedSameScript_IdenticalStreamsAndFinalState()
        {
            for (int run = 0; run < 3; run++)
            {
                uint seed = (uint)(20261030 + run * 17);
                var a = PlayScenario(seed);
                var b = PlayScenario(seed);

                Assert.AreEqual(GamePhase.Prep, a.s.Phase, "脚本停在 R3 备战");
                Assert.AreEqual(3, a.s.Round, "打到 R3");
                Assert.IsTrue(a.s.Combat.PlayerWon, "R2（最后一场）也是胜局");

                // 事件流逐条一致（先整体快速比对，失败再定位首处差异）
                bool same = a.rec.Log.Count == b.rec.Log.Count;
                if (same)
                    for (int i = 0; i < a.rec.Log.Count && same; i++)
                        same = string.Equals(a.rec.Log[i], b.rec.Log[i], StringComparison.Ordinal);
                Assert.IsTrue(same, $"第 {run} 组事件流不一致——{EventRecorder.FirstDifference(a.rec.Log, b.rec.Log)}");

                // 事件流规模下限（防空转假绿：两场真战斗 + 全套装备操作必然有规模）
                Assert.Greater(a.rec.Log.Count, 150, $"第 {run} 组事件流规模下限（实际 {a.rec.Log.Count} 条）");

                // 终局状态逐项一致（含阶段3 全部新状态位）
                Assert.AreEqual(a.s.PlayerHp, b.s.PlayerHp, $"第 {run} 组血量一致");
                Assert.AreEqual(a.s.Eco.Gold, b.s.Eco.Gold, $"第 {run} 组金币一致");
                Assert.AreEqual(a.s.LastBattleSeed, b.s.LastBattleSeed, $"第 {run} 组战斗种子一致（阶段5 快照重演的地基）");
                CollectionAssert.AreEqual(a.s.OwnedPolicyIds, b.s.OwnedPolicyIds, $"第 {run} 组已选策略一致");
                Assert.AreEqual(a.s.Inventory.EquipmentCount, b.s.Inventory.EquipmentCount, $"第 {run} 组背包装备数一致");
                Assert.AreEqual(a.s.UnsmithToolCount, b.s.UnsmithToolCount, $"第 {run} 组拆卸器数一致");

                Unit ua = a.s.PlayerBoard[0], ub = b.s.PlayerBoard[0];
                Assert.AreEqual(ua.EffectiveStats.AttackPower, ub.EffectiveStats.AttackPower, $"第 {run} 组有效攻击一致");
                Assert.AreEqual(ua.EffectiveStats.AbilityPower, ub.EffectiveStats.AbilityPower, $"第 {run} 组有效法强一致");
                for (int slot = 0; slot < Unit.EquipmentSlotCount; slot++)
                {
                    bool aNull = ua.EquipmentSlots[slot] == null, bNull = ub.EquipmentSlots[slot] == null;
                    Assert.AreEqual(aNull, bNull, $"第 {run} 组槽 {slot} 占用一致");
                    if (!aNull)
                        Assert.AreEqual(ua.EquipmentSlots[slot].DefId, ub.EquipmentSlots[slot].DefId, $"第 {run} 组槽 {slot} 装备一致");
                }
            }
        }

        // ─────────────────────── 指纹验收：新系统必须在事件流里留痕 ───────────────────────

        [Test]
        public void ScenarioCoverage_NewSystemsLeaveFingerprints()
        {
            var (s, rec) = PlayScenario(20261030u);
            var log = rec.Log;

            // 技能：蓝 30 / 命中 +10 → 每 3 次出手一施法；两场战斗都有施法
            Assert.GreaterOrEqual(Count(log, "SkillCast|"), 2, "技能施法留痕（施法循环接进真实会话战斗）");
            Assert.IsTrue(log.Any(l => l.StartsWith("SkillCast|", StringComparison.Ordinal) && l.Split('|')[2] == "3"),
                "施的是技能 3 奥术飞弹");

            // 装备四事件 + 背包快照
            Assert.AreEqual(3, Count(log, "Equipped|"), "穿戴三次（物理散件 / 法术散件 / 再穿回）");
            Assert.AreEqual(1, Count(log, "Unequipped|"), "卸下一次");
            Assert.AreEqual(1, Count(log, "Upgraded|"), "升级一次");
            Assert.AreEqual(1, Count(log, l => l.StartsWith("Upgraded|", StringComparison.Ordinal) && l.Split('|')[4] == "11"),
                "升成分支 0 = 输出（DefId 11）");
            Assert.AreEqual(9, Count(log, "Inv|"),
                "背包快照恰 9 条（开局送 2 + 矿 2 + 策 2 + 穿 3 + 卸 1，每笔变动一条）");

            // 矿两类奖励（P3-8 四类的两条新路径）
            Assert.AreEqual(1, Count(log, l => l.StartsWith("OreOpened|", StringComparison.Ordinal) && l.Contains("Equipment")),
                "装备矿开过一次");
            Assert.AreEqual(1, Count(log, l => l.StartsWith("OreOpened|", StringComparison.Ordinal) && l.Contains("UnsmithTool")),
                "拆卸器矿开过一次");

            // 策略
            Assert.AreEqual(1, Count(log, "PolicyPicked|"), "选定一条");

            // Buff：灼烧 DoT 跳伤（SourceUnitId=0 = 无攻击者的持续伤害、1 点/跳）≥ 30 跳
            Assert.GreaterOrEqual(CountDotTicks(log), 30, "灼烧 DoT 留痕（策略钩子驱动 BuffSystem 的生产接线）");

            // 战斗钩子（增伤 20%）实证：普攻 60→72、技能 73→88（R2）；R1 无钩子时技能 70（基准对照）
            Assert.IsTrue(HasDamageValue(log, 72), "增伤乘区实证：普攻 60 × 1.2 = 72");
            Assert.IsTrue(HasDamageValue(log, 88), "增伤乘区实证：技能 (30 + 40×108/100=73) × 1.2 → 88");
            Assert.IsTrue(HasDamageValue(log, 70), "R1 无钩子基准：技能 30 + 40×100/100 = 70（乘区不凭空生效）");

            // 终局精确账目（占位数值锁死——改表必红，逼着同步改测试与文档）
            Unit caster = s.PlayerBoard[0];
            Assert.AreEqual(60, caster.EffectiveStats.AttackPower, "攻 40 + 输出 20");
            Assert.AreEqual(108, caster.EffectiveStats.AbilityPower, "法强 100 + 法术散件 8");
            Assert.AreEqual(11, caster.EquipmentSlots[0].DefId, "槽 0 = 输出（大件）");
            Assert.AreEqual(2, caster.EquipmentSlots[1].DefId, "槽 1 = 法术散件");
            Assert.IsNull(caster.EquipmentSlots[2], "槽 2 空");
            Assert.AreEqual(0, s.Inventory.EquipmentCount, "背包无余货（两件都穿着）");
            Assert.AreEqual(4, s.UnsmithToolCount, "拆卸器 = 开局 2 + 矿 2 + 策 1 - 卸 1");
            Assert.AreEqual(10, s.Eco.Gold, "金币 = 3 - 1买 + 5胜 + 2策 - 5升 + 5胜 + 1连胜");
            Assert.AreEqual(100, s.PlayerHp, "两连胜不掉血");
            CollectionAssert.AreEqual(new[] { 1 }, s.OwnedPolicyIds.ToArray(), "已选策略 = 魔契");
        }

        // ─────────────────────── 统计助手 ───────────────────────

        private static int Count(List<string> log, string prefix)
        {
            int n = 0;
            foreach (string l in log)
                if (l.StartsWith(prefix, StringComparison.Ordinal)) n++;
            return n;
        }

        private static int Count(List<string> log, Func<string, bool> pred)
        {
            int n = 0;
            foreach (string l in log)
                if (pred(l)) n++;
            return n;
        }

        /// <summary>灼烧跳伤行计数：Damaged|{单位}|{来源=0}|{伤害=1}|{血}——无攻击者的每跳 1 点。</summary>
        private static int CountDotTicks(List<string> log)
        {
            int n = 0;
            foreach (string l in log)
            {
                if (!l.StartsWith("Damaged|", StringComparison.Ordinal)) continue;
                string[] p = l.Split('|');
                if (p.Length == 5 && p[2] == "0" && p[3] == "1") n++;
            }
            return n;
        }

        /// <summary>事件流里是否存在某伤害值的受击行（增伤乘幅的实证用）。</summary>
        private static bool HasDamageValue(List<string> log, int damage)
        {
            foreach (string l in log)
            {
                if (!l.StartsWith("Damaged|", StringComparison.Ordinal)) continue;
                string[] p = l.Split('|');
                if (p.Length == 5 && p[3] == damage.ToString()) return true;
            }
            return false;
        }
    }
}
