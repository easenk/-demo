using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AutoChess.Game;
using UnityEditor;
using UnityEngine;

namespace AutoChess.Tests
{
    /// <summary>
    /// 批次7a：平衡压测 harness——基线测量工具（数值零改动、机制零改动）。
    ///
    /// 定位：把 #21⑧ 的两流派剧本落成可执行的"机器人玩家"，对当前 Default 表跑 N 局，
    /// 产出对 #21⑩ 三条标尺的基线报告；7b/7c 每轮改表后重跑对比，最后一轮输出 = 交卷证据。
    ///
    /// 架构红利兑现：逻辑层纯 C#（零 UnityEngine 依赖）→ 全量模拟跑后台线程（Task.Run），
    /// 编辑器主线程零阻塞；GameTables.Default 构造后只读、跨线程共享安全；同种子 = 同对局。
    ///
    /// 口径（2026-10-30 用户拍板）：追卡成本 = 买目标棋子的钱 + 为找它刷商店的钱，两部分——
    /// 不含买经验/装备升级/无关买入；自然到手的目标棋子计入张数、成本记 0。
    /// 窗口界定：低费流 = 冲上 6 级后起（此前不刷新）；速升流 = R12 采购窗口起（骨架补买同窗口计）。
    ///
    /// 冒烟回归（10 局 × 2 流派 + 确定性复核）在 BalancePressTests，进常规套件；
    /// 全量基线（默认每流派 1000 局）走菜单或 RunBaselineAsync——不进常规套件（耗时约 1 分钟）。
    /// 报告写到工程根 平衡压测报告.md（Assets 之外，不产生 .meta、不入 Unity 导入）。
    /// </summary>
    public static class BalancePressSim
    {
        public enum Faction { LowCost, Rush }

        // ─────────────────────── 每局记录 ───────────────────────

        /// <summary>一局的完整画像（报表的数据单元）。</summary>
        public sealed class GameRecord
        {
            public bool RunWon;
            public int EndRound;                            // 终局回合（败 = 倒下的回合；胜 = 14）
            public int FinalHp;
            public string Rating = "";
            public bool TargetCompleted;                    // 低费流：三星督军；速升流：二星兽王
            public int TargetCompleteRound;                 // 达成回合（未达成 = 0）
            public int TargetBuyGold;                       // 口径①：买目标集合棋子的钱（全程任何时点）
            public int HuntRefreshGold;                     // 口径②：追卡窗口内刷商店的钱
            public int BossTicks = -1;                      // R14 战斗步数（未打到 R14 = -1）
            public readonly int[] GoldAtBattle = new int[15];   // [回合] 开战前金币
            public readonly int[] LevelAtBattle = new int[15];  // [回合] 开战前等级
            public int InterestRounds;                      // 利息 &gt; 0 的结算场数
            public readonly int[] BoughtCounts = new int[19];   // [charId] 买入张数（内容出场率）
            public readonly int[] BestSynergyTier = new int[7]; // [synergyId] 全局最佳档位索引（-1 = 从未激活）
            public uint Seed;
        }

        // ─────────────────────── 机器人基座 ───────────────────────

        private abstract class FactionBot
        {
            // 布阵用固定格位（玩家半场 8×3；近战前排 row 2、远程后排 row 1/0——格子互不重叠）
            private static readonly Pos[] FrontCells =
            {
                new Pos(1, 2), new Pos(2, 2), new Pos(3, 2), new Pos(4, 2), new Pos(5, 2), new Pos(6, 2), new Pos(0, 2), new Pos(7, 2),
            };
            private static readonly Pos[] BackCells =
            {
                new Pos(2, 1), new Pos(3, 1), new Pos(4, 1), new Pos(5, 1), new Pos(2, 0), new Pos(3, 0), new Pos(4, 0), new Pos(5, 0),
            };

            protected GameSession S;
            protected readonly GameRecord Rec = new GameRecord();

            protected abstract int TargetCharId { get; }
            protected abstract int TargetStar { get; }
            protected abstract bool CountsAsTargetBuy(int charId, int round);
            protected abstract void DoPrep(int round);
            protected abstract int PickPolicy(IReadOnlyList<int> candidates);

            public GameRecord Play(uint seed)
            {
                for (int i = 0; i < Rec.BestSynergyTier.Length; i++) Rec.BestSynergyTier[i] = -1;
                Rec.Seed = seed;

                var bus = new EventBus();
                bus.Subscribe<UnitMergedEvent>(e =>
                {
                    if (e.CharId == TargetCharId && e.NewStar >= TargetStar && !Rec.TargetCompleted)
                    { Rec.TargetCompleted = true; Rec.TargetCompleteRound = S.Round; }
                });
                bus.Subscribe<IncomeSettledEvent>(e => { if (e.Bill.InterestGold > 0) Rec.InterestRounds++; });
                bus.Subscribe<SynergiesChangedEvent>(e =>
                {
                    foreach (var info in e.Snapshot)
                        if (info.TierIndex > Rec.BestSynergyTier[(int)info.SynergyId])
                            Rec.BestSynergyTier[(int)info.SynergyId] = info.TierIndex;
                });

                S = new GameSession(bus, seed, GameTables.Default);
                if (!S.StartRun()) throw new InvalidOperationException($"种子 {seed}：StartRun 失败");

                int guard = 0;
                while (!S.RunOver)
                {
                    if (++guard > 300) throw new InvalidOperationException($"种子 {seed}：回合循环异常长");
                    if (S.Phase == GamePhase.Settlement)
                    {
                        if (!S.StartPrep()) throw new InvalidOperationException($"种子 {seed}：StartPrep 被拒");
                        continue;
                    }
                    if (S.Phase != GamePhase.Prep) throw new InvalidOperationException($"种子 {seed}：意外相位 {S.Phase}");

                    DrainOres();
                    if (S.HasPolicyChoice)
                    {
                        int idx = PickPolicy(S.PolicyCandidates);
                        if (!S.PickPolicy(idx)) throw new InvalidOperationException($"种子 {seed}：PickPolicy 被拒");
                    }
                    DoPrep(S.Round);

                    Rec.GoldAtBattle[S.Round] = S.Eco.Gold;
                    Rec.LevelAtBattle[S.Round] = S.Eco.Level;
                    if (!S.StartBattle()) throw new InvalidOperationException($"种子 {seed}：R{S.Round} 开战被拒（门禁未消费干净）");
                    if (S.Phase == GamePhase.Battle)
                    {
                        int t = 0;
                        while (!S.Combat.IsOver && t++ < 6000) S.Combat.Tick();
                        if (S.Round == 14) Rec.BossTicks = S.Combat.TickCount;
                        if (!S.SettleBattle()) throw new InvalidOperationException($"种子 {seed}：SettleBattle 被拒");
                    }
                }

                Rec.RunWon = S.PlayerHp > 0;
                Rec.FinalHp = S.PlayerHp;
                Rec.EndRound = S.Round;
                Rec.Rating = S.FinalRating?.ToString() ?? "";
                if (!Rec.TargetCompleted && HasUnitStar(TargetCharId, TargetStar))
                { Rec.TargetCompleted = true; Rec.TargetCompleteRound = S.Round; }
                return Rec;
            }

            // ── 门禁消费：捡光矿 ──
            private void DrainOres()
            {
                int guard = 0;
                while (S.OresOnGround.Count > 0)
                {
                    if (++guard > 50) throw new InvalidOperationException("捡矿循环异常长");
                    if (!S.PickOre(S.OresOnGround[0].Id)) throw new InvalidOperationException("备战期捡矿被拒");
                }
            }

            // ── 通用买入：按谓词买架上货（拒绝 = 钱不够/备战席满，静默跳过）──
            protected void BuyOffers(Func<int, bool> want)
            {
                CharacterDef[] offers = S.Shop.Offers;   // 买入清空槽位、不挪位——一次快照全轮有效
                for (int slot = 0; slot < ShopSystem.Slots; slot++)
                {
                    CharacterDef offer = offers[slot];
                    if (offer.Id == 0 || !want(offer.Id)) continue;
                    int goldBefore = S.Eco.Gold;
                    if (!S.Buy(slot)) continue;
                    Rec.BoughtCounts[offer.Id]++;
                    if (CountsAsTargetBuy(offer.Id, S.Round)) Rec.TargetBuyGold += goldBefore - S.Eco.Gold;
                }
            }

            /// <summary>追卡窗口内的刷新（口径②记账；窗口外不许调它）。</summary>
            protected bool HuntRefresh()
            {
                int goldBefore = S.Eco.Gold;
                if (!S.RefreshShop()) return false;
                Rec.HuntRefreshGold += goldBefore - S.Eco.Gold;
                return true;
            }

            protected bool HasUnitStar(int charId, int minStar)
            {
                foreach (var u in S.PlayerBoard) if (u.CharId == charId && u.Star >= minStar) return true;
                foreach (var u in S.Bench) if (u.CharId == charId && u.Star >= minStar) return true;
                return false;
            }

            protected bool OwnsChar(int charId) => HasUnitStar(charId, 1);

            protected int RosterCount => S.PlayerBoard.Count + S.Bench.Count;

            // ── 布阵：全下场 → 按价值选等级上限个上场 → 近战（射程≤1）塞前排 ──
            protected void RearrangeBoard(Func<Unit, int> priority)
            {
                foreach (var u in S.PlayerBoard.ToArray()) S.TakeOffBoard(u.Id);
                List<Unit> roster = S.Bench.ToList();
                roster.Sort((a, b) => priority(b).CompareTo(priority(a)));
                List<Unit> chosen = roster.Take(S.Eco.Level)
                    .OrderBy(u => GameTables.Default.Characters.Get(u.CharId).BaseStats.AttackRange)
                    .ToList();
                int front = 0, back = 0;
                foreach (var u in chosen)
                {
                    int range = GameTables.Default.Characters.Get(u.CharId).BaseStats.AttackRange;
                    Pos cell = range <= 1 ? FrontCells[front++] : BackCells[back++];
                    if (!S.PlaceOnBoard(u.Id, cell))
                        throw new InvalidOperationException($"布阵撞格：{u.CharId} → {cell.X},{cell.Y}（格位表Bug）");
                }
            }

            // ── 穿戴：背包货优先给指定佩戴人，其次第一个有空槽的场上单位 ──
            protected void EquipFromInventory(Func<Unit, bool> preferWearer)
            {
                foreach (EquipmentInstance item in S.Inventory.Items.ToArray())
                {
                    Unit wearer = null;
                    for (int i = 0; i < S.PlayerBoard.Count; i++)
                    {
                        Unit u = S.PlayerBoard[i];
                        bool hasFree = false;
                        for (int s = 0; s < Unit.EquipmentSlotCount; s++)
                            if (u.EquipmentSlots[s] == null) { hasFree = true; break; }
                        if (!hasFree) continue;
                        if (preferWearer(u)) { wearer = u; break; }
                        if (wearer == null) wearer = u;
                    }
                    if (wearer == null) break;
                    for (int slot = 0; slot < Unit.EquipmentSlotCount; slot++)
                        if (S.Equip(wearer.Id, slot, item.InstanceId)) break;
                }
            }

            // ── 升大件：找指定棋子槽内第一件散件升成分支 0（低费流的"输出"分支）──
            protected void UpgradeCarryComponents(int charId)
            {
                Unit carry = null;
                foreach (var u in S.PlayerBoard) if (u.CharId == charId) { carry = u; break; }
                if (carry == null) return;
                for (int slot = 0; slot < Unit.EquipmentSlotCount; slot++)
                {
                    EquipmentInstance item = carry.EquipmentSlots[slot];
                    if (item == null) continue;
                    if (GameTables.Default.Equipment.Get(item.DefId).Kind == EquipmentKind.Component)
                    { S.UpgradeEquipment(carry.Id, slot, 0); return; }
                }
            }
        }

        // ─────────────────────── 低费流（#21⑧） ───────────────────────

        /// <summary>剧本：R1~6 买白名单凑二星过精英 → R7 冲 6 级 → 全钱追三星战团督军 → 双 4 档通关。</summary>
        private sealed class LowCostBot : FactionBot
        {
            private static readonly int[] Whitelist = { 1, 2, 3, 6, 7, 11 };   // 蛮兽/铁壁低费骨架 + 督军

            protected override int TargetCharId => 11;
            protected override int TargetStar => 3;
            protected override bool CountsAsTargetBuy(int charId, int round) => charId == 11;

            protected override int PickPolicy(IReadOnlyList<int> candidates)
            {
                for (int i = 0; i < candidates.Count; i++)                    // 战力向：优先全队修饰器词条
                    foreach (var e in GameTables.Default.Policies.Get(candidates[i]).Effects)
                        if (e.Type == PolicyEffectType.TeamModifier) return i;
                return 0;
            }

            protected override void DoPrep(int round)
            {
                // 冲级段（R7 起直到 6 级；#21⑤：低费流 R7 花约 24 金速升）
                if (round >= 7 && S.Eco.Level < 6)
                    while (S.Eco.Level < 6 && S.Eco.Gold >= 4) S.BuyXp();

                if (round >= 7 && S.Eco.Level >= 6)
                {
                    // 追卡段：见督军就买（含自然上架，成本照记）→ 钱够就刷（保留"刷一次 + 买一张"）
                    int guard = 0;
                    while (!HasUnitStar(11, 3) && guard++ < 300)
                    {
                        BuyOffers(id => id == 11 && !HasUnitStar(11, 3));
                        if (HasUnitStar(11, 3) || S.Eco.Gold < 5) break;
                        if (!HuntRefresh()) break;
                    }
                    // 三星到手，余钱升大件——追卡之外的开销，不进 75 金口径
                    if (HasUnitStar(11, 3) && S.Eco.Gold >= 8) UpgradeCarryComponents(11);
                }
                else
                {
                    // 过渡段（R1~6）：白名单买入（重复张自动合成凑二星过精英），不刷新
                    BuyOffers(id => Array.IndexOf(Whitelist, id) >= 0 && RosterCount < 7);
                }

                RearrangeBoard(u => u.Star * 10 + (u.CharId == 11 ? 5 : 0));
                EquipFromInventory(u => u.CharId == 11);
            }
        }

        // ─────────────────────── 速升流（#21⑧） ───────────────────────

        /// <summary>剧本：一星铺场吃利息 → 溢出买经验 → R11 砸满 8 级 → R12~14 采购窗口找二星魔渊兽王 → 反超。</summary>
        private sealed class RushBot : FactionBot
        {
            private static readonly int[] EarlySkeleton = { 1, 2, 6, 4, 8 };        // 蛮兽/暗影低费骨架
            private static readonly int[] WindowSkeleton = { 1, 2, 6, 4, 8, 14 };   // 窗口可补影刃（暗影 4 档）

            protected override int TargetCharId => 18;
            protected override int TargetStar => 2;
            protected override bool CountsAsTargetBuy(int charId, int round)
                => charId == 18 || (round >= 12 && Array.IndexOf(WindowSkeleton, charId) >= 0);

            protected override int PickPolicy(IReadOnlyList<int> candidates)
            {
                for (int i = 0; i < candidates.Count; i++)                    // 经济向：优先金币/经验词条
                    foreach (var e in GameTables.Default.Policies.Get(candidates[i]).Effects)
                        if (e.Type == PolicyEffectType.Gold || e.Type == PolicyEffectType.Xp) return i;
                return 0;
            }

            protected override void DoPrep(int round)
            {
                if (round <= 10)
                {
                    // 攒钱段：铺场到 6 人（骨架白名单，重复张允许）；银行 50 封顶，溢出买经验（满级即停）
                    BuyOffers(id => Array.IndexOf(EarlySkeleton, id) >= 0 && RosterCount < 6);
                    while (S.Eco.Level < 8 && S.Eco.Gold > 50) S.BuyXp();
                }
                else
                {
                    // 冲顶段（R11 起；钱不够滑到 R12 也继续）：买经验直到 8 级
                    while (S.Eco.Level < 8 && S.Eco.Gold >= 4) S.BuyXp();

                    // 采购窗口（R12 起）：找魔渊兽王（二星收手）+ 补窗口骨架（每种一张）
                    if (round >= 12)
                    {
                        int guard = 0;
                        while (guard++ < 300)
                        {
                            BuyOffers(id => (id == 18 && !HasUnitStar(18, 2))
                                             || (Array.IndexOf(WindowSkeleton, id) >= 0 && !OwnsChar(id)));
                            if (HasUnitStar(18, 2) || S.Eco.Gold < 7) break;   // 2 刷 + 5 买
                            if (!HuntRefresh()) break;
                        }
                    }
                }

                RearrangeBoard(u => u.Star * 10 + (u.CharId == 18 ? 5 : 0));
                EquipFromInventory(u => u.CharId == 18);
            }
        }

        // ─────────────────────── 批量执行（后台线程安全：纯 C#，无 Unity API） ───────────────────────

        public static List<GameRecord> RunGames(Faction faction, int count, uint seedBase)
        {
            var results = new List<GameRecord>(count);
            for (int i = 0; i < count; i++)
            {
                FactionBot bot = faction == Faction.LowCost ? new LowCostBot() : new RushBot();
                results.Add(bot.Play(checked(seedBase + (uint)i)));
            }
            return results;
        }

        // ─────────────────────── 全量基线入口（菜单 / 异步） ───────────────────────

        [MenuItem("工具/平衡压测·基线报告（每流派 1000 局）")]
        public static void MenuRunBaseline() { _ = RunBaselineAsync(1000, 20261101u); }

        /// <summary>全量基线：两流派后台各跑 perFaction 局 → 汇总报告写到工程根。返回报告文件路径。</summary>
        public static async Task<string> RunBaselineAsync(int perFaction, uint seedBase)
        {
            var sw = Stopwatch.StartNew();
            List<GameRecord> low = await Task.Run(() => RunGames(Faction.LowCost, perFaction, seedBase));
            List<GameRecord> rush = await Task.Run(() => RunGames(Faction.Rush, perFaction, seedBase));
            sw.Stop();

            string report = BuildReport(low, rush, perFaction, seedBase, sw.Elapsed);
            string path = Path.Combine(Application.dataPath, "..", "平衡压测报告.md");
            File.WriteAllText(path, report, new UTF8Encoding(false));
            UnityEngine.Debug.Log($"[平衡压测] 基线报告已写出：{path}（{sw.Elapsed.TotalSeconds:F1}s）");
            return path;
        }

        // ─────────────────────── 统计与报告 ───────────────────────

        private static double Pct(int part, int whole) => whole == 0 ? 0 : 100.0 * part / whole;

        private static int P(List<int> sorted, double p)
        {
            int idx = (int)Math.Ceiling(p * sorted.Count) - 1;
            if (idx < 0) idx = 0;
            return sorted[Math.Min(idx, sorted.Count - 1)];
        }

        private static string Dist(List<int> values)
        {
            if (values.Count == 0) return "（无样本）";
            var s = values.OrderBy(v => v).ToList();
            return $"min {s[0]} / P25 {P(s, .25)} / P50 {P(s, .5)} / P75 {P(s, .75)} / P90 {P(s, .9)} / max {s[s.Count - 1]}";
        }

        private static int FirstRoundAtLevel(GameRecord r, int level)
        {
            for (int round = 1; round <= 14; round++)
                if (r.LevelAtBattle[round] >= level) return round;
            return 0;
        }

        public static string BuildReport(List<GameRecord> low, List<GameRecord> rush, int perFaction, uint seedBase, TimeSpan elapsed)
        {
            var sb = new StringBuilder(8192);
            sb.AppendLine("# 平衡压测 · 报告");
            sb.AppendLine();
            sb.AppendLine($"> 参数：每流派 {perFaction} 局 · 种子基 {seedBase}（两流派共用同一组会话种子——同运气、不同策略，可比性最大化）");
            sb.AppendLine($"> 耗时 {elapsed.TotalSeconds:F1}s · 数值版本 = 当前 Default 表（改表后重跑即得新版）");
            sb.AppendLine($"> 生成时间 {DateTime.Now:yyyy-MM-dd HH:mm} · 重跑：菜单「工具/平衡压测」或 BalancePressSim.RunBaselineAsync");
            sb.AppendLine();
            sb.AppendLine("## 口径（2026-10-30 拍板）");
            sb.AppendLine();
            sb.AppendLine("- 追卡成本 = **买目标棋子的钱 + 为找它刷商店的钱**，两部分（不含经验/装备升级/无关买入）");
            sb.AppendLine("- 低费流：目标 = 战团督军（id 11，9 张合三星）；刷新窗口 = 冲上 6 级后（此前只买自然上架、不刷新）");
            sb.AppendLine("- 速升流：目标集合 = 魔渊兽王（id 18，3 张合二星）+ R12 起补入的窗口骨架（含影刃 id 14）；刷新窗口 = R12 起");
            sb.AppendLine("- 自然到手的目标棋子计入张数、成本记 0（\"含自然到手\"）");
            sb.AppendLine();

            // ── 标尺① ──
            sb.AppendLine("## 标尺①（#21⑩）：低费流 三星三费全追 ≤ 75 金");
            sb.AppendLine();
            AppendTargetSection(sb, low, 75, "三星督军");
            sb.AppendLine();

            // ── 标尺② ──
            sb.AppendLine("## 标尺②（#21⑩）：速升流 二星五星 + 骨架 ≤ 55 金");
            sb.AppendLine();
            AppendTargetSection(sb, rush, 55, "二星兽王 + 窗口骨架");
            sb.AppendLine();

            // ── 标尺③ ──
            sb.AppendLine("## 标尺③（#21⑨）：Boss 限时（90 秒 = 2700 tick）击杀难度偏紧");
            sb.AppendLine();
            AppendBossSection(sb, "低费流", low);
            AppendBossSection(sb, "速升流", rush);
            sb.AppendLine();

            // ── 通关与评价 ──
            sb.AppendLine("## 通关与评价");
            sb.AppendLine();
            AppendFactionSummary(sb, "低费流", low);
            AppendFactionSummary(sb, "速升流", rush);
            sb.AppendLine();

            // ── 终局回合分布 ──
            sb.AppendLine("## 终局回合分布（败局定位 = 难度曲线）");
            sb.AppendLine();
            AppendEndRoundSection(sb, "低费流", low);
            AppendEndRoundSection(sb, "速升流", rush);
            sb.AppendLine();

            // ── 经济与等级节奏 ──
            sb.AppendLine("## 经济与等级节奏");
            sb.AppendLine();
            sb.AppendLine("| 流派 | R2 | R4 | R6 | R8 | R10 | R12 | R14 | 利息场数 | 冲级达成 |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
            AppendEconomyRow(sb, "低费流", low, 6);
            AppendEconomyRow(sb, "速升流", rush, 8);
            sb.AppendLine();

            // ── 内容侧 ──
            sb.AppendLine("## 内容侧（机器人买入视角的出场率）");
            sb.AppendLine();
            AppendContentSection(sb, "低费流", low);
            AppendContentSection(sb, "速升流", rush);
            sb.AppendLine();

            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine("*本报告由压测 harness 自动生成；改表后重跑即可对比。数值结论请连同口径一起阅读。*");
            return sb.ToString();
        }

        private static void AppendTargetSection(StringBuilder sb, List<GameRecord> rs, int bar, string label)
        {
            int n = rs.Count;
            List<GameRecord> done = rs.Where(r => r.TargetCompleted).ToList();
            sb.AppendLine($"- {label}达成率：**{Pct(done.Count, n):F1}%**（{done.Count}/{n}）");
            List<int> costs = done.Select(r => r.TargetBuyGold + r.HuntRefreshGold).ToList();
            sb.AppendLine($"- 成本分布（仅达成局，买 + 刷合计）：{Dist(costs)}");
            if (costs.Count > 0)
            {
                sb.AppendLine($"- 对照标尺 ≤{bar} 金：**{Pct(costs.Count(c => c <= bar), costs.Count):F1}% 达标**"
                    + $"（买 P50 {P(done.Select(r => r.TargetBuyGold).OrderBy(v => v).ToList(), .5)}"
                    + $" / 刷 P50 {P(done.Select(r => r.HuntRefreshGold).OrderBy(v => v).ToList(), .5)}）");
                sb.AppendLine($"- 达成回合分布：{Dist(done.Select(r => r.TargetCompleteRound).ToList())}");
            }
        }

        private static void AppendBossSection(StringBuilder sb, string name, List<GameRecord> rs)
        {
            int n = rs.Count;
            List<GameRecord> reached = rs.Where(r => r.BossTicks >= 0).ToList();
            List<GameRecord> won = reached.Where(r => r.RunWon).ToList();
            List<GameRecord> inTime = won.Where(r => r.BossTicks <= GameSession.BossTimeLimitTicks).ToList();
            sb.AppendLine($"- {name}：R14 到达率 {Pct(reached.Count, n):F1}% → R14 胜率 {Pct(won.Count, reached.Count):F1}%"
                + $" → **限时内胜率 {Pct(inTime.Count, reached.Count):F1}%**（R14 步数 {Dist(reached.Select(r => r.BossTicks).ToList())}）");
        }

        private static void AppendFactionSummary(StringBuilder sb, string name, List<GameRecord> rs)
        {
            int n = rs.Count;
            int wins = rs.Count(r => r.RunWon);
            sb.AppendLine($"- **{name}**：通关率 {Pct(wins, n):F1}%（{wins}/{n}）· 终局血量 {Dist(rs.Select(r => r.FinalHp).ToList())}");
            var ratings = rs.GroupBy(r => string.IsNullOrEmpty(r.Rating) ? "?" : r.Rating)
                .Select(g => $"{g.Key} {Pct(g.Count(), n):F0}%")
                .OrderBy(s => s);
            sb.AppendLine($"  - 评级分布：{string.Join(" / ", ratings)}");
        }

        private static void AppendEndRoundSection(StringBuilder sb, string name, List<GameRecord> rs)
        {
            var parts = new List<string>();
            for (int round = 1; round <= 14; round++)
            {
                int lost = rs.Count(x => x.EndRound == round && !x.RunWon);
                if (lost > 0) parts.Add($"R{round} ×{lost}");
            }
            int win14 = rs.Count(x => x.RunWon);
            parts.Add($"R14 通关 ×{win14}");
            sb.AppendLine($"- {name}：{string.Join(" ｜ ", parts)}");
        }

        private static void AppendEconomyRow(StringBuilder sb, string name, List<GameRecord> rs, int levelGoal)
        {
            var cells = new List<string>();
            foreach (int round in new[] { 2, 4, 6, 8, 10, 12, 14 })
            {
                List<int> golds = rs.Where(x => x.RunWon || x.EndRound >= round).Select(x => x.GoldAtBattle[round]).ToList();
                cells.Add(golds.Count == 0 ? "-" : P(golds.OrderBy(v => v).ToList(), .5).ToString());
            }
            string interest = Dist(rs.Select(r => r.InterestRounds).ToList());
            List<int> levelRounds = rs.Select(r => FirstRoundAtLevel(r, levelGoal)).Where(r => r > 0).ToList();
            string levelCell = levelRounds.Count == 0
                ? $"从未到 L{levelGoal}"
                : $"L{levelGoal} P50 {P(levelRounds.OrderBy(v => v).ToList(), .5)} 回合（{Pct(levelRounds.Count, rs.Count):F0}% 达成）";
            sb.AppendLine($"| {name} | {string.Join(" | ", cells)} | {interest} | {levelCell} |");
        }

        private static void AppendContentSection(StringBuilder sb, string name, List<GameRecord> rs)
        {
            int n = rs.Count;
            var lines = new List<(double Avg, string Line)>();
            for (int charId = 1; charId <= 18; charId++)
            {
                double avg = rs.Sum(r => r.BoughtCounts[charId]) / (double)n;
                if (avg <= 0.01) continue;
                CharacterDef def = GameTables.Default.Characters.Get(charId);
                lines.Add((avg, $"  - {def.DisplayName}（{def.Cost}费，id {charId}）×{avg:F1}"));
            }
            sb.AppendLine($"- {name} 平均买入：");
            foreach (var entry in lines.OrderByDescending(e => e.Avg)) sb.AppendLine(entry.Line);
            var syn = new List<string>();
            for (int s = 1; s <= 6; s++)
                syn.Add($"{(SynergyId)s} 4档 {Pct(rs.Count(r => r.BestSynergyTier[s] >= 1), n):F0}%"
                    + $"/6档 {Pct(rs.Count(r => r.BestSynergyTier[s] >= 2), n):F0}%");
            sb.AppendLine($"- 羁绊档位达成率（4 档 / 6 档）：{string.Join(" ｜ ", syn)}");
        }
    }
}
