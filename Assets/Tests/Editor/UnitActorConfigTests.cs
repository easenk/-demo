using System;
using System.Collections.Generic;
using System.Linq;
using AutoChess.Game;
using AutoChess.Presentation;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace AutoChess.Tests
{
    /// <summary>
    /// 批次4C：视觉配置表 + 截窗算法 + 烘焙产物 + 撮合顺序契约。
    /// 分节契约：
    ///  · 截窗纯函数：守约全窗直用 / 违约中段保命中（命中帧永远在窗内、窗内时长恒 ≤ 攻隔——
    ///    #23⑥ 守约纪律由截窗恢复的构造性证明）/ 命中帧贴边钳制 / 死亡尾部窗保末帧 / 参数校验 fail fast；
    ///  · 配置表：30 键齐（18 角色 CharId + 12 怪）、EnemyVisualId 与 EnemyTable.Default 怪种名
    ///    一一对应（改名两头同步的锁）、定义字段合法；
    ///  · 烘焙产物（EditMode+AssetDatabase）：30 个 SO 存在、四动作帧非空、帧率为正；
    ///  · 守约纪律全量：18 角色 + 12 怪逐单位断言 截窗后攻击时长 ≤ 各自攻隔（角色=CharacterTable、
    ///    怪=编成表该怪种最小攻隔——最紧约束）；
    ///  · 撮合顺序契约：EnemyRoster[i] ↔ 编成表 Spawns[i]（BattleRoot 视觉撮合的依赖，
    ///    GameSession.SpawnEncounter 表序生成保证——摆位全成功时格位对齐可作外部探针）。
    /// </summary>
    [TestFixture]
    public class UnitActorConfigTests
    {
        // ─────────────────────── 截窗纯函数 ───────────────────────

        [Test]
        public void ResolveAttackWindow_CompliantClip_FullWindow()
        {
            var w = UnitActorConfig.ResolveAttackWindow(12, 12f, 1f, 1.1f, 0.5f);
            Assert.AreEqual(0, w.Start, "守约：全窗从 0 起");
            Assert.AreEqual(12, w.Count, "守约：全窗 12 帧");
            Assert.AreEqual(1f, w.Duration, 1e-6f, "全窗时长=clip 时长");
            Assert.AreEqual(0.5f, w.HitPercent, 1e-6f, "全窗命中百分比原样");
        }

        [Test]
        public void ResolveAttackWindow_ViolatingClip_MidWindowKeepsHitInWindow()
        {
            // 兽人投矛手实数据：42 帧 / 12fps / 3.5s vs 攻隔 1.2s → 预算 14 帧、命中帧约 20
            var w = UnitActorConfig.ResolveAttackWindow(42, 12f, 3.5f, 1.2f, 0.5f);
            Assert.LessOrEqual(w.Count, 14, "窗帧数 ≤ 预算（floor(攻隔×帧率)）");
            Assert.LessOrEqual(w.Duration, 1.2f + 1e-4f, "窗内时长 ≤ 攻隔（守约由截窗恢复）");
            int hitFrame = w.Start + (int)Math.Round(w.HitPercent * (w.Count - 1));
            Assert.GreaterOrEqual(hitFrame, w.Start, "命中帧在窗内（下界）");
            Assert.LessOrEqual(hitFrame, w.Start + w.Count - 1, "命中帧在窗内（上界）");
            Assert.AreEqual(hitFrame, 20, "命中帧=round(0.5×41)=20（钳制不丢锚）");
        }

        [Test]
        public void ResolveAttackWindow_HitAtEdges_WindowClampsToContainIt()
        {
            // 命中帧在第 0 帧（起手即命中）→ 窗必须从 0 起装住它
            var wHead = UnitActorConfig.ResolveAttackWindow(42, 12f, 3.5f, 1.2f, 0f);
            Assert.AreEqual(0, wHead.Start, "命中帧贴头：窗从头起");
            int head = wHead.Start + (int)Math.Round(wHead.HitPercent * (wHead.Count - 1));
            Assert.AreEqual(0, head, "头帧命中仍在窗内");

            // 命中帧在末帧 → 窗贴尾装住它
            var wTail = UnitActorConfig.ResolveAttackWindow(42, 12f, 3.5f, 1.2f, 1f);
            int tail = wTail.Start + (int)Math.Round(wTail.HitPercent * (wTail.Count - 1));
            Assert.AreEqual(41, tail, "尾帧命中仍在窗内");
        }

        [Test]
        public void ResolveAttackWindow_InvalidArgs_Throw()
        {
            Assert.Throws<ArgumentException>(
                () => UnitActorConfig.ResolveAttackWindow(0, 12f, 1f, 1f, 0.5f), "零帧拒");
            Assert.Throws<ArgumentException>(
                () => UnitActorConfig.ResolveAttackWindow(12, 0f, 1f, 1f, 0.5f), "零帧率拒");
            Assert.Throws<ArgumentException>(
                () => UnitActorConfig.ResolveAttackWindow(12, 12f, 1f, 0f, 0.5f), "零攻隔拒");
            Assert.Throws<ArgumentException>(
                () => UnitActorConfig.ResolveAttackWindow(12, 12f, 1f, 1f, 1.5f), "越界命中百分比拒");
        }

        [Test]
        public void ResolveDeathWindow_LongClip_TailWindowKeepsLastFrame()
        {
            // Gol 实数据：27 帧 / 2.25s vs 上限 1.2s → 尾部 14 帧，末帧=原末帧
            var w = UnitActorConfig.ResolveDeathWindow(27, 12f, 2.25f, 1.2f);
            Assert.AreEqual(27 - w.Count, w.Start, "尾部窗：起点+帧数=总帧数");
            Assert.AreEqual(14, w.Count, "floor(1.2×12)=14 帧（ceil 会把 14.4 顶成 15 帧=1.25s 超窗）");
            Assert.LessOrEqual(w.Duration, 1.2f + 1e-4f, "死亡窗时长 ≤ 上限");

            // 短死亡直用全窗
            var full = UnitActorConfig.ResolveDeathWindow(9, 12f, 0.75f, 1.2f);
            Assert.AreEqual(0, full.Start, "全窗从 0 起");
            Assert.AreEqual(9, full.Count, "全窗 9 帧");
        }

        // ─────────────────────── 配置表完整性 ───────────────────────

        [Test]
        public void Config_ThirtyKeys_FilledAndValid()
        {
            for (int charId = 1; charId <= 18; charId++)
                AssertValidDef(UnitActorConfig.Get(charId), $"角色键 {charId}（=CharId 直通）");
            for (int e = 1; e <= 12; e++)
                AssertValidDef(UnitActorConfig.Get(UnitActorConfig.EnemyKeyBase + e), $"怪种键 {(EnemyVisualId)e}");

            void AssertValidDef(UnitVisualDef def, string label)
            {
                Assert.IsNotNull(def, $"{label} 必须有视觉定义");
                Assert.IsTrue(def.ClipSetPath.StartsWith("Assets/Res/Art/UnitActorClips/", StringComparison.Ordinal),
                    $"{label} SO 路径必须落在烘焙目录");
                Assert.Greater(def.SizeMultiplier, 0f, $"{label} 体型倍率必须为正");
                Assert.GreaterOrEqual(def.AttackHitPercent, 0f, $"{label} 命中帧百分比下界");
                Assert.LessOrEqual(def.AttackHitPercent, 1f, $"{label} 命中帧百分比上界");
                Assert.Greater(def.DeathMaxDuration, 0f, $"{label} 死亡窗上限为正");
                Assert.Greater(def.MoveDuration, 0f, $"{label} 移动补间为正");
            }
        }

        [Test]
        public void Config_EnemyNameMap_CoversEverySpeciesInEnemyTable()
        {
            // 编成表全部怪种名 → 视觉键：EnemyTable 改名必须同步 EnemyVisualId 映射（这条锁住它）
            var species = new HashSet<string>();
            foreach (var entry in EnemyTable.Default.Entries)
                foreach (var spawn in entry.Spawns)
                    species.Add(spawn.DisplayName);

            Assert.AreEqual(12, species.Count, "12 怪种（宝箱怪/双精英/Boss/普通怪——2026-11-06 拍板 roster）");
            foreach (string name in species)
            {
                int key = UnitActorConfig.EnemyKeyFromName(name);
                Assert.AreNotEqual(0, key, $"编成表怪种「{name}」必须有视觉映射");
                Assert.IsNotNull(UnitActorConfig.Get(key), $"「{name}」的视觉键 {key} 必须有定义");
            }
            Assert.AreEqual(0, UnitActorConfig.EnemyKeyFromName("不存在的怪"), "未知名字返 0（fail fast 口径）");
        }

        // ─────────────────────── 烘焙产物校验（EditMode） ───────────────────────

        [Test]
        public void BakedClipSets_AllThirtyExist_FourActionsFilled()
        {
            int checkedCount = 0;
            for (int charId = 1; charId <= 18; charId++)
                CheckOne(UnitActorConfig.Get(charId).ClipSetPath, $"角色{charId}");
            for (int e = 1; e <= 12; e++)
                CheckOne(UnitActorConfig.Get(UnitActorConfig.EnemyKeyBase + e).ClipSetPath, $"怪{(EnemyVisualId)e}");

            Assert.AreEqual(30, checkedCount, "30 个 SO 全部存在且过校验");

            void CheckOne(string path, string label)
            {
                var set = AssetDatabase.LoadAssetAtPath<UnitActorClipSet>(path + ".asset");
                Assert.IsNotNull(set, $"{label} 烘焙 SO 缺失：{path}——跑菜单「工具/表现层序列帧烘焙」");
                Assert.Greater(set.Idle.Frames.Length, 0, $"{label} 待机帧非空");
                Assert.Greater(set.Walk.Frames.Length, 0, $"{label} 移动帧非空");
                Assert.Greater(set.Attack.Frames.Length, 0, $"{label} 攻击帧非空（含代用填充）");
                Assert.Greater(set.Death.Frames.Length, 0, $"{label} 死亡帧非空");
                Assert.Greater(set.Attack.FramesPerSecond, 0f, $"{label} 攻击帧率为正");
                Assert.Greater(set.BodyHeight, 0f, $"{label} 身高记录为正（缩放归一的分母）");
                checkedCount++;
            }
        }

        [Test]
        public void BakedClipSets_Discipline_AllWindowsFitAttackIntervals()
        {
            // #23⑥ 守约纪律全量断言：截窗后攻击时长 ≤ 各自最紧攻隔
            // 角色：攻隔=CharacterTable；怪：该怪种在编成表里的最小攻隔（最紧约束）
            var enemyIntervals = new Dictionary<string, float>();
            foreach (var entry in EnemyTable.Default.Entries)
                foreach (var spawn in entry.Spawns)
                    if (!enemyIntervals.TryGetValue(spawn.DisplayName, out float cur) ||
                        spawn.Stats.AttackInterval < cur)
                        enemyIntervals[spawn.DisplayName] = spawn.Stats.AttackInterval;

            for (int charId = 1; charId <= 18; charId++)
                CheckOne(UnitActorConfig.Get(charId).ClipSetPath,
                    GameTables.Default.Characters.Get(charId).BaseStats.AttackInterval, $"角色{charId}");
            for (int e = 1; e <= 12; e++)
            {
                var id = (EnemyVisualId)e;
                string name = EnemyNameOf(id);
                Assert.IsTrue(enemyIntervals.TryGetValue(name, out float interval), $"怪种「{name}」应出现在编成表");
                CheckOne(UnitActorConfig.Get(UnitActorConfig.EnemyKeyBase + e).ClipSetPath, interval, $"怪{e}「{name}」");
            }

            void CheckOne(string path, float interval, string label)
            {
                var set = AssetDatabase.LoadAssetAtPath<UnitActorClipSet>(path + ".asset");
                Assert.IsNotNull(set, $"{label} SO 缺失");
                var w = UnitActorConfig.ResolveAttackWindow(set.Attack.Frames.Length,
                    set.Attack.FramesPerSecond, set.Attack.Duration, interval, 0.5f);
                Assert.LessOrEqual(w.Duration, interval + 1e-4f,
                    $"{label} 截窗后攻击时长 {w.Duration:0.###}s 必须装进攻隔 {interval:0.##}s（#23⑥ 守约）");
            }
        }

        // ─────────────────────── 撮合顺序契约（BattleRoot 视觉撮合的依赖） ───────────────────────

        [Test]
        public void SpawnEncounter_RosterOrderMatchesSpawnTable()
        {
            // GameSession.SpawnEncounter 按 foreach(编成表) 顺序生成、无条件入名册——
            // BattleRoot 据此做 EnemyRoster[i]↔Spawns[i] 撮合拿怪种名。
            // 外部探针：默认编成表 R1 两格摆位无冲突 → TryPlace 全成功 → 表序=名册序可由格位对齐断言
            //（摆位兜底改格不破坏顺序本身，只破坏这个探针——若未来表内格位重叠，此测试改用别的探针）。
            var s = new GameSession(new EventBus(), 0u, GameTables.Default);
            Assert.IsTrue(s.StartRun());
            Assert.AreEqual(GamePhase.Prep, s.Phase, "R1 备战就绪");
            Assert.IsTrue(s.Buy(0), "开局 3 金买得起 1 费");
            Assert.AreEqual(0, s.EnemyRoster.Count, "开战前名册为空");

            Assert.IsTrue(s.StartBattle(), "R1 开战（无矿无策略，门禁放行）");
            Encounter encounter = GameTables.Default.Enemies.Get(1);
            Assert.AreEqual(encounter.Spawns.Length, s.EnemyRoster.Count,
                "开战后名册数量 = 编成表数量（顺序契约的前提）");
            for (int i = 0; i < encounter.Spawns.Length; i++)
                Assert.AreEqual(encounter.Spawns[i].Cell, s.EnemyRoster[i].Pos,
                    $"名册第 {i} 位 = 编成表第 {i} 条（表序生成、TryPlace 全成功格位对齐）");
        }

        private static string EnemyNameOf(EnemyVisualId id) => id switch
        {
            EnemyVisualId.TreasureMimic => "宝箱怪",
            EnemyVisualId.Wolf => "野狼",
            EnemyVisualId.GolemPuppet => "石像傀儡",
            EnemyVisualId.Whelp => "幼龙",
            EnemyVisualId.EvilEye => "魔眼妖",
            EnemyVisualId.SpineWorm => "刺毛虫",
            EnemyVisualId.Ghost => "幽灵",
            EnemyVisualId.StoneGiant => "石像巨人",
            EnemyVisualId.Ogre => "食人魔",
            EnemyVisualId.Chieftain => "兽人酋长",
            EnemyVisualId.UndeadLord => "亡灵领主",
            EnemyVisualId.AbyssLord => "深渊魔王",
            _ => "未知",
        };
    }
}
