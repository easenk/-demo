using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AutoChess.Game;
using AutoChess.Presentation;
using UnityEditor;
using UnityEngine;

namespace AutoChess.Tests
{
    /// <summary>
    /// 阶段4 批次A：表现层预演基准（#23① 点名的第一交付物）。
    ///
    /// 定位：量"整场预演"的本机耗时与内存画像——每局一个会话（种子基递增），L8 满级 +
    /// 注资 300 金买满 9 人阵容（内容随种子随机，含合成升星），走 R1→R14 完整 14 场
    /// （奖励关掉矿/精英/Boss 全内容形态），每场用 BattlePreviewer 整场预演。
    /// 量测窗口 = RunToEnd 预演循环本身（开战的会话操作/结算不入样本——那是玩法开销不是预演开销）。
    ///
    /// 架构红利：逻辑层纯 C# → 全部模拟可跑 Task.Run 后台线程，编辑器主线程零阻塞。
    /// 估算口径：条目 StreamItem = 36B/条 + 快照帧 = 24B + 20B/单位（结构体含对齐的估算，
    /// 与 #23②"单场峰值 0.3MB"预算对照）。
    ///
    /// 手机推演（#23①）：真机数字等设备实测；先按保守系数 ×5 / ×10 推演。
    /// 冒烟（2 局）在 PreviewTests；全量默认 71 局 × 14 场 ≈ 千场走菜单——不进常规套件。
    /// 报告写到工程根 预演基准报告.md（Assets 之外，不产生 .meta、不入 Unity 导入；
    /// 重跑覆盖前先存档旧版——7a 惯例）。
    /// </summary>
    public static class PreviewBenchmark
    {
        /// <summary>一场预演的画像样本。</summary>
        public sealed class BattleSample
        {
            public int Round;
            public double Ms;        // RunToEnd 耗时（毫秒）
            public int Ticks;        // 逻辑步数
            public int Events;       // 事件流条数
            public int Frames;       // 快照帧数
            public int EstBytes;     // 估算内存峰值（事件 + 快照）
        }

        // 布阵固定格位（玩家半场 8x3；近战 row 2、远程 row 1/0——与平衡压测同款格表）
        private static readonly Pos[] FrontCells =
        {
            new Pos(1, 2), new Pos(2, 2), new Pos(3, 2), new Pos(4, 2), new Pos(5, 2), new Pos(6, 2), new Pos(0, 2), new Pos(7, 2),
        };
        private static readonly Pos[] BackCells =
        {
            new Pos(2, 1), new Pos(3, 1), new Pos(4, 1), new Pos(5, 1), new Pos(2, 0), new Pos(3, 0), new Pos(4, 0), new Pos(5, 0),
        };

        // ─────────────────────── 一局（会话）：L8 阵容 × R1~R14 逐场预演 ───────────────────────

        private static List<BattleSample> PlayRun(uint seed)
        {
            var samples = new List<BattleSample>(14);
            var s = new GameSession(new EventBus(), seed, GameTables.Default);
            if (!s.StartRun()) throw new InvalidOperationException($"种子 {seed}：StartRun 失败");

            // 满级 + 注资 + 买满备战席（架上任意有货就买；合成升星自然发生——内容随种子随机）
            s.Eco.AddGold(300);
            while (s.Eco.Level < 8)
                if (!s.BuyXp()) break;
            int buyGuard = 0;
            while (s.Bench.Count < GameSession.BenchCapacity && buyGuard++ < 100)
            {
                int slot = -1;
                for (int i = 0; i < ShopSystem.Slots; i++)
                    if (s.Shop.Offers[i].Id != 0) { slot = i; break; }
                if (slot < 0) { if (!s.RefreshShop()) break; continue; }
                if (!s.Buy(slot)) break;
            }

            int guard = 0;
            while (!s.RunOver)
            {
                if (++guard > 300) throw new InvalidOperationException($"种子 {seed}：回合循环异常长");
                if (s.Phase == GamePhase.Settlement)
                {
                    if (!s.StartPrep()) break;
                    continue;
                }
                if (s.Phase != GamePhase.Prep) break;

                // 门禁消费：捡光矿 + 选策略（基准只测预演成本，策略固定选第一格）
                int oreGuard = 0;
                while (s.OresOnGround.Count > 0)
                {
                    if (++oreGuard > 50) throw new InvalidOperationException("捡矿循环异常长");
                    if (!s.PickOre(s.OresOnGround[0].Id)) throw new InvalidOperationException("备战期捡矿被拒");
                }
                if (s.HasPolicyChoice && !s.PickPolicy(0)) throw new InvalidOperationException("选策略被拒");

                FillBoard(s);

                // 整场预演（量测窗口 = RunToEnd 本身）
                var p = new BattlePreviewer();
                if (!p.Start(s)) throw new InvalidOperationException($"种子 {seed}：R{s.Round} 预演开战被拒（门禁未消费干净）");
                var sw = Stopwatch.StartNew();
                BattlePreviewResult r = p.RunToEnd();
                sw.Stop();

                samples.Add(new BattleSample
                {
                    Round = s.Round,
                    Ms = sw.Elapsed.TotalMilliseconds,
                    Ticks = r.TickCount,
                    Events = r.Stream.Count,
                    Frames = r.Snapshots.Count,
                    EstBytes = r.Stream.Count * 36 + EstimateSnapshotBytes(r.Snapshots),
                });

                if (s.Phase == GamePhase.Battle && !s.SettleBattle())
                    throw new InvalidOperationException($"种子 {seed}：R{s.Round} 结算被拒");
                p.Release();
            }
            return samples;
        }

        /// <summary>补上场：板上有空位就从备战席按前后排格表补（复活/合成后板位一般已满，本步多数回合为空操作）。</summary>
        private static void FillBoard(GameSession s)
        {
            while (s.PlayerBoard.Count < s.Eco.Level && s.Bench.Count > 0)
            {
                Unit u = s.Bench[0];
                bool ranged = GameTables.Default.Characters.Get(u.CharId).BaseStats.AttackRange > 1;
                bool placed = false;
                Pos[] cells = ranged ? BackCells : FrontCells;
                foreach (Pos cell in cells)
                    if (s.PlaceOnBoard(u.Id, cell)) { placed = true; break; }
                if (!placed)
                {
                    foreach (Pos cell in (ranged ? FrontCells : BackCells))
                        if (s.PlaceOnBoard(u.Id, cell)) { placed = true; break; }
                }
                if (!placed) break;   // 16 格都满 = 理论不可达，防御性退出
            }
        }

        private static int EstimateSnapshotBytes(BattleSnapshotSequence snaps)
        {
            int bytes = 0;
            for (int i = 0; i < snaps.Count; i++)
                bytes += 24 + 20 * snaps[i].Units.Length;
            return bytes;
        }

        // ─────────────────────── 批量执行（后台线程安全：纯 C#，无 Unity API） ───────────────────────

        /// <summary>跑 sessions 局并生成报告文本（冒烟测试直调本方法——不落盘）。</summary>
        public static string RunBenchmark(int sessions, uint seedBase)
        {
            var sw = Stopwatch.StartNew();
            var all = new List<BattleSample>(sessions * 14);
            for (int i = 0; i < sessions; i++)
                all.AddRange(PlayRun(checked(seedBase + (uint)i)));
            sw.Stop();
            return BuildReport(all, sessions, seedBase, sw.Elapsed);
        }

        [MenuItem("工具/表现层预演基准（千场）")]
        public static void MenuRun() { _ = RunAsync(71, 20261102u); }   // 71 局 × 14 场 = 994 场 ≈ 千场

        /// <summary>全量基准：后台跑 sessions 局 → 报告写到工程根。返回报告文件路径。</summary>
        public static async Task<string> RunAsync(int sessions, uint seedBase)
        {
            var sw = Stopwatch.StartNew();
            List<BattleSample> all = await Task.Run(() =>
            {
                var list = new List<BattleSample>(sessions * 14);
                for (int i = 0; i < sessions; i++)
                    list.AddRange(PlayRun(checked(seedBase + (uint)i)));
                return list;
            });
            sw.Stop();

            string report = BuildReport(all, sessions, seedBase, sw.Elapsed);
            string path = Path.Combine(Application.dataPath, "..", "预演基准报告.md");
            File.WriteAllText(path, report, new UTF8Encoding(false));
            UnityEngine.Debug.Log($"[预演基准] 报告已写出：{path}（{sw.Elapsed.TotalSeconds:F1}s）");
            return path;
        }

        // ─────────────────────── 统计与报告 ───────────────────────

        private static string Dist(List<double> values)
        {
            if (values.Count == 0) return "（无样本）";
            var s = values.OrderBy(v => v).ToList();
            return $"min {s[0]:F2} / P50 {P(s, .5):F2} / P90 {P(s, .9):F2} / P99 {P(s, .99):F2} / max {s[s.Count - 1]:F2}";
        }

        private static string DistInt(List<int> values)
        {
            if (values.Count == 0) return "（无样本）";
            var s = values.OrderBy(v => v).ToList();
            return $"min {s[0]} / P50 {P(s, .5)} / P90 {P(s, .9)} / P99 {P(s, .99)} / max {s[s.Count - 1]}";
        }

        private static double P(List<double> sorted, double p)
        {
            int idx = (int)Math.Ceiling(p * sorted.Count) - 1;
            if (idx < 0) idx = 0;
            return sorted[Math.Min(idx, sorted.Count - 1)];
        }

        private static int P(List<int> sorted, double p)
        {
            int idx = (int)Math.Ceiling(p * sorted.Count) - 1;
            if (idx < 0) idx = 0;
            return sorted[Math.Min(idx, sorted.Count - 1)];
        }

        private const int MemoryBudgetBytes = 300 * 1024;   // #23② 单场峰值预算 0.3MB

        public static string BuildReport(List<BattleSample> samples, int sessions, uint seedBase, TimeSpan elapsed)
        {
            var sb = new StringBuilder(8192);
            sb.AppendLine("# 表现层预演 · 基准报告（批次A）");
            sb.AppendLine();
            sb.AppendLine($"> 参数：{sessions} 局 × 每局 R1~R14（样本 {samples.Count} 场） · 种子基 {seedBase} · 数值版本 = 当前 Default 表");
            sb.AppendLine($"> 场景：L8 满级 + 注资 300 金 + 备战席 9 人（内容随种子随机、含合成升星），无装备升级干预");
            sb.AppendLine($"> 量测窗口 = RunToEnd 预演循环本身（开战会话操作/结算不入样本）；总耗时 {elapsed.TotalSeconds:F1}s（含会话开销）");
            sb.AppendLine($"> 生成时间 {DateTime.Now:yyyy-MM-dd HH:mm} · 重跑：菜单「工具/表现层预演基准」或 PreviewBenchmark.RunAsync（重跑覆盖前先存档旧版）");
            sb.AppendLine();

            if (samples.Count == 0)
            {
                sb.AppendLine("（无样本——场景异常）");
                return sb.ToString();
            }

            // ── 总览 ──
            sb.AppendLine("## 每场预演耗时（毫秒）");
            sb.AppendLine();
            sb.AppendLine($"- **{Dist(samples.Select(x => x.Ms).ToList())}**");
            double p50 = P(samples.Select(x => x.Ms).OrderBy(v => v).ToList(), .5);
            double p99 = P(samples.Select(x => x.Ms).OrderBy(v => v).ToList(), .99);
            sb.AppendLine($"- #23① 保守推演（最低端机型系数）：×5 → P50 {p50 * 5:F2}ms / P99 {p99 * 5:F2}ms；×10 → P50 {p50 * 10:F2}ms / P99 {p99 * 10:F2}ms");
            sb.AppendLine("  - 结论对照：预演藏进开场演出（0.5~1.5 秒）是否装得下、需不需要降配优化——看推演数字");
            sb.AppendLine();

            // ── 按回合拆解 ──
            sb.AppendLine("## 按回合拆解（P50 耗时 / 样本数）");
            sb.AppendLine();
            sb.AppendLine("| 回合 | P50 耗时(ms) | 样本数 | P50 步数 |");
            sb.AppendLine("|---|---|---|---|");
            for (int round = 1; round <= 14; round++)
            {
                List<BattleSample> rs = samples.Where(x => x.Round == round).ToList();
                if (rs.Count == 0) { sb.AppendLine($"| R{round} | - | 0 | - |"); continue; }
                sb.AppendLine($"| R{round} | {P(rs.Select(x => x.Ms).OrderBy(v => v).ToList(), .5):F2} | {rs.Count}"
                    + $" | {P(rs.Select(x => x.Ticks).OrderBy(v => v).ToList(), .5)} |");
            }
            sb.AppendLine();

            // ── 规模画像 ──
            sb.AppendLine("## 规模画像");
            sb.AppendLine();
            sb.AppendLine($"- 逻辑步数：{DistInt(samples.Select(x => x.Ticks).ToList())}");
            sb.AppendLine($"- 事件流条数：{DistInt(samples.Select(x => x.Events).ToList())}");
            sb.AppendLine($"- 快照帧数：{DistInt(samples.Select(x => x.Frames).ToList())}");
            sb.AppendLine();

            // ── 内存 ──
            sb.AppendLine("## 估算内存峰值（条目 36B/事件 + 帧 24B + 20B/单位）");
            sb.AppendLine();
            List<int> mem = samples.Select(x => x.EstBytes).ToList();
            var memSorted = mem.OrderBy(v => v).ToList();
            sb.AppendLine($"- {DistInt(mem)}");
            int overBudget = mem.Count(x => x > MemoryBudgetBytes);
            sb.AppendLine($"- 对照 #23② 预算 0.3MB：**{overBudget}/{mem.Count} 场超预算**（P99 = {P(memSorted, .99)}B = {P(memSorted, .99) / 1024.0:F0}KB）");
            sb.AppendLine("  - 超预算 = 病态长战（多单位 + 近 4500 步）；正常内容形态远低于预算。瘦身余地：格子坐标 byte 化已做、Tick/Seq 可压缩、白名单不可裁（保真纪律）");
            sb.AppendLine();

            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine("*本报告由预演基准工具自动生成；改数值/内容后重跑即可对比。结论请连同口径一起阅读。*");
            return sb.ToString();
        }
    }
}
