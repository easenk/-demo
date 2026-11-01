using System;
using System.Linq;
using AutoChess.Game;
using NUnit.Framework;

namespace AutoChess.Tests
{
    /// <summary>
    /// 批次7a：压测 harness 的冒烟回归（进常规套件；全量基线走菜单，见 BalancePressSim 头注释——
    /// 千局耗时约 1 分钟，不挂进每次测试）。
    /// 锁的契约：
    ///  · 两流派机器人都能把整局跑到终局——剧本规则不死循环（守卫兜底）、门禁全消费、无意外相位；
    ///  · 每局画像自洽：终局回合 1~14、口径①②成本非负、评级有值；
    ///  · 报告可生成且含三标尺节 + 口径节（拍板留痕）；
    ///  · 同种子同剧本 = 同画像（关键字段逐项一致）——harness 无隐藏状态，后台批量跑的确定性前提。
    /// </summary>
    [TestFixture]
    public class BalancePressTests
    {
        [Test]
        public void Smoke_BothFactions_AllGamesComplete_ReportBuilds()
        {
            var low = BalancePressSim.RunGames(BalancePressSim.Faction.LowCost, 10, 424242u);
            var rush = BalancePressSim.RunGames(BalancePressSim.Faction.Rush, 10, 424242u);

            Assert.AreEqual(10, low.Count, "低费流局数");
            Assert.AreEqual(10, rush.Count, "速升流局数");
            foreach (var r in low.Concat(rush))
            {
                Assert.GreaterOrEqual(r.EndRound, 1, "终局回合下界（必须跑到 RunOver，守卫没兜底=剧本死循环）");
                Assert.LessOrEqual(r.EndRound, 14, "终局回合上界");
                Assert.GreaterOrEqual(r.TargetBuyGold, 0, "口径①（买目标的钱）非负");
                Assert.GreaterOrEqual(r.HuntRefreshGold, 0, "口径②（为找它刷的钱）非负");
                Assert.IsNotNull(r.Rating, "评级有值");
            }

            string report = BalancePressSim.BuildReport(low, rush, 10, 424242u, TimeSpan.Zero);
            Assert.Greater(report.Length, 500, "报告规模下限");
            StringAssert.Contains("标尺①", report);
            StringAssert.Contains("标尺②", report);
            StringAssert.Contains("标尺③", report);
            StringAssert.Contains("口径（2026-10-30 拍板）", report);
        }

        [Test]
        public void SameSeedSameScript_IdenticalRecords()
        {
            var lowA = BalancePressSim.RunGames(BalancePressSim.Faction.LowCost, 3, 919191u);
            var lowB = BalancePressSim.RunGames(BalancePressSim.Faction.LowCost, 3, 919191u);
            var rushA = BalancePressSim.RunGames(BalancePressSim.Faction.Rush, 3, 919191u);
            var rushB = BalancePressSim.RunGames(BalancePressSim.Faction.Rush, 3, 919191u);

            for (int i = 0; i < 3; i++)
            {
                Assert.AreEqual(lowA[i].EndRound, lowB[i].EndRound, $"低费流第 {i} 局终局回合一致");
                Assert.AreEqual(lowA[i].FinalHp, lowB[i].FinalHp, $"低费流第 {i} 局血量一致");
                Assert.AreEqual(lowA[i].TargetCompleted, lowB[i].TargetCompleted, $"低费流第 {i} 局达标一致");
                Assert.AreEqual(lowA[i].TargetBuyGold, lowB[i].TargetBuyGold, $"低费流第 {i} 局口径①一致");
                Assert.AreEqual(lowA[i].HuntRefreshGold, lowB[i].HuntRefreshGold, $"低费流第 {i} 局口径②一致");
                Assert.AreEqual(lowA[i].BossTicks, lowB[i].BossTicks, $"低费流第 {i} 局 Boss 步数一致");
                Assert.AreEqual(rushA[i].EndRound, rushB[i].EndRound, $"速升流第 {i} 局终局回合一致");
                Assert.AreEqual(rushA[i].FinalHp, rushB[i].FinalHp, $"速升流第 {i} 局血量一致");
                Assert.AreEqual(rushA[i].TargetBuyGold, rushB[i].TargetBuyGold, $"速升流第 {i} 局口径①一致");
                Assert.AreEqual(rushA[i].HuntRefreshGold, rushB[i].HuntRefreshGold, $"速升流第 {i} 局口径②一致");
            }
        }
    }
}
