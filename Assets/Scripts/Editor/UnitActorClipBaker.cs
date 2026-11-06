using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AutoChess.Game;
using AutoChess.Presentation;
using UnityEditor;
using UnityEngine;

namespace AutoChess.EditorTools
{
    /// <summary>
    /// 序列帧烘焙器（批次4C）：把素材 .anim 的 m_Sprite 对象曲线提取成 UnitActorClipSet SO。
    /// · 素材不动：只读 .anim/预制体文件夹，SO 落在 Res/Art/UnitActorClips/（素材目录、不入 git）；
    /// · 素材热插拔：换素材/修帧序后重跑本菜单即更新（路径与映射真源 = UnitActorConfig）；
    /// · 动作分类按名字关键词打分（Attack1 优先于 Attack2、Death 优先于 DeadAir/DeadGround、
    ///   Run 优先于 Walk 优先于 Fly；Hit/Breathing/Appear/Blink/Alert/Jump/TakeOff/Landing/Fall/
    ///   Projectile/FX 全部排除——受击走程序闪色是 #23 已定口径，其余无战斗语义）；
    /// · 素材缺口代用（2026-11-06 拍板 v1 凑合）：缺攻击 → Hit（次选 Walk/Idle）、缺移动 → Idle、
    ///   缺待机 → Walk——代用一律在烘焙报告里标记，4F 打磨期换真素材后重烘即可；
    /// · 烘焙报告 = 工程根「素材烘焙报告.md」：逐单位动作选型/帧数/帧率 + 守约纪律核查表
    ///   （原始攻击时长 vs 攻隔、截窗预览——#23⑥ 的验收数据）。
    /// </summary>
    public static class UnitActorClipBaker
    {
        private const string ClipSetDir = "Assets/Res/Art/UnitActorClips";
        private const string MainRoleDir = "Assets/Res/Art/MainRole";
        private const string MonsterDir = "Assets/Res/Art/Monster";
        private const string ReportFile = "素材烘焙报告.md";

        [MenuItem("工具/表现层序列帧烘焙")]
        public static void Bake()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# 素材烘焙报告（UnitActorClipSet）");
            sb.AppendLine();
            sb.AppendLine($"> 烘焙时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss} · 来源：Res/Art/MainRole(0~17) + Res/Art/Monster(0~11)");
            sb.AppendLine("> 代用与截窗口径见 阶段4-接口设计.md 4C 节；重跑本菜单覆盖本文件（要留档先改名）。");
            sb.AppendLine();

            EnsureFolder(ClipSetDir);
            EnsureBillboardMaterial();      // 立牌材质随烘焙再生（Res 内才进 YooAsset 收集范围——Assets/Shaders 放不进包）
            int baked = 0;
            var problems = new List<string>();

            // 18 角色：roleN ↔ 文件夹 N-1
            for (int charId = 1; charId <= 18; charId++)
            {
                string folder = $"{MainRoleDir}/{charId - 1}";
                string name = GameTables.Default.Characters.Get(charId).DisplayName;
                float interval = GameTables.Default.Characters.Get(charId).BaseStats.AttackInterval;
                BakeUnit(sb, problems, folder, $"Role_{charId}", $"角色{charId}「{name}」", interval, ref baked);
            }

            // 12 怪种：文件夹号 ↔ 枚举-1；攻隔取该怪种在编成表里的最小攻隔（最紧约束）
            var enemyIntervals = new Dictionary<string, float>();
            foreach (var entry in EnemyTable.Default.Entries)
                foreach (var spawn in entry.Spawns)
                {
                    float iv = spawn.Stats.AttackInterval;
                    if (!enemyIntervals.TryGetValue(spawn.DisplayName, out float cur) || iv < cur)
                        enemyIntervals[spawn.DisplayName] = iv;
                }
            for (int folder = 0; folder <= 11; folder++)
            {
                var id = (EnemyVisualId)(folder + 1);
                string species = EnemyNameOf(id);
                string key = $"Enemy_{folder}";
                if (!enemyIntervals.TryGetValue(species, out float interval))
                {
                    problems.Add($"怪种「{species}」不在编成表里——攻隔取不到");
                    interval = 1.3f;
                }
                BakeUnit(sb, problems, $"{MonsterDir}/{folder}", key, $"怪{folder}「{species}」", interval, ref baked);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            sb.AppendLine();
            sb.AppendLine("## 问题清单");
            sb.AppendLine(problems.Count == 0 ? "无" : string.Join(Environment.NewLine, problems));

            string reportPath = Path.Combine(Directory.GetParent(Application.dataPath).FullName, ReportFile);
            File.WriteAllText(reportPath, sb.ToString(), Encoding.UTF8);
            Debug.Log($"[烘焙] 完成：{baked} 个 SO 已落盘 {ClipSetDir}/，报告 → {ReportFile}");
        }

        // ─────────────────────── 单位烘焙 ───────────────────────

        private static void BakeUnit(StringBuilder sb, List<string> problems, string folder, string key,
            string label, float attackInterval, ref int baked)
        {
            sb.AppendLine($"## {label}");
            if (!AssetDatabase.IsValidFolder(folder))
            {
                sb.AppendLine($"  [X] 素材文件夹不存在：{folder}");
                problems.Add($"{label}：文件夹缺失 {folder}");
                sb.AppendLine();
                return;
            }

            var clips = LoadClips(folder);

            // 帧预提取：0 帧的"空壳 clip"（实锤坑：Solfist 的 Idle=1s/0 帧）视同缺失，选型即过滤
            var framesOf = new Dictionary<string, Sprite[]>(clips.Count);
            foreach (var kv in clips)
                framesOf[kv.Key] = ExtractFrames(kv.Value);

            AnimationClip Pick(Func<string, int> rank, out Sprite[] chosenFrames, out int chosenRank)
            {
                AnimationClip best = null;
                int bestRank = int.MaxValue;
                foreach (var kv in clips)
                {
                    if (IsExcluded(kv.Key)) continue;
                    if (framesOf[kv.Key].Length == 0) continue;   // 空壳弃用
                    int r = rank(kv.Key);
                    if (r < bestRank) { bestRank = r; best = kv.Value; }
                }
                chosenFrames = best != null ? framesOf[best.name] : null;
                chosenRank = bestRank;
                return best;
            }

            var idle = Pick(IdleRank, out var idleFrames, out int idleRank);
            var walk = Pick(WalkRank, out var walkFrames, out int walkRank);
            var attack = Pick(AttackRank, out var attackFrames, out int attackRank);
            var death = Pick(DeathRank, out var deathFrames, out int deathRank);

            // 代用（缺口的 v1 凑合——2026-11-06 拍板；代用必须进报告）
            string idleNote = "", walkNote = "", attackNote = "";
            if (idle == null && walk != null) { idle = walk; idleFrames = walkFrames; idleRank = walkRank; idleNote = "←代用Walk"; }
            if (walk == null && idle != null) { walk = idle; walkFrames = idleFrames; walkRank = idleRank; walkNote = "←代用Idle"; }
            if (attack == null)
            {
                string hitKey = FirstKeyContaining(clips, "hit");
                if (hitKey != null && framesOf[hitKey].Length > 0)
                { attack = clips[hitKey]; attackFrames = framesOf[hitKey]; attackNote = "←代用Hit"; }
                else if (walk != null) { attack = walk; attackFrames = walkFrames; attackNote = "←代用Walk"; }
            }
            if (idle == null || walk == null || attack == null || death == null)
            {
                sb.AppendLine($"  [X] 四动作凑不齐（idle={idle != null}/walk={walk != null}/attack={attack != null}/death={death != null}）");
                problems.Add($"{label}：动作缺失无法凑齐");
                sb.AppendLine();
                return;
            }

            var set = ScriptableObject.CreateInstance<UnitActorClipSet>();
            set.Idle = BuildClip(idle, idleFrames);
            set.Walk = BuildClip(walk, walkFrames);
            set.Attack = BuildClip(attack, attackFrames);
            set.Death = BuildClip(death, deathFrames);

            Sprite first = set.Idle.Frames.Length > 0 ? set.Idle.Frames[0] : null;
            if (first != null)
            {
                set.FeetOffset = first.pivot.y / first.pixelsPerUnit;
                set.BodyHeight = first.bounds.size.y;
            }
            else problems.Add($"{label}：idle 首帧缺失，FeetOffset/BodyHeight 用默认值");

            string assetPath = $"{ClipSetDir}/{key}.asset";
            if (AssetDatabase.LoadAssetAtPath<UnitActorClipSet>(assetPath) != null)
                AssetDatabase.DeleteAsset(assetPath);
            AssetDatabase.CreateAsset(set, assetPath);
            baked++;

            // 逐动作报告行
            void Row(string tag, AnimationClip clip, Sprite[] frames, int rankText, string note)
            {
                sb.AppendLine($"  {tag}：{clip.name}（评分{(rankText == int.MaxValue ? "缺" : rankText.ToString())}）{frames.Length} 帧 · {clip.length:0.##}s · {frames.Length / Math.Max(clip.length, 0.001f):0.#} 帧/秒 {note}");
            }
            Row("待机", idle, idleFrames, idleRank, idleNote);
            Row("移动", walk, walkFrames, walkRank, walkNote);
            Row("攻击", attack, attackFrames, attackRank, attackNote);
            Row("死亡", death, deathFrames, deathRank, "");

            // 守约纪律核查（#23⑥）：原始攻击时长 vs 攻隔 + 截窗预览（与运行期同一条纯函数）
            float fps = set.Attack.FramesPerSecond;
            var win = UnitActorConfig.ResolveAttackWindow(set.Attack.Frames.Length, fps, set.Attack.Duration,
                attackInterval, 0.5f);
            string verdict = set.Attack.Duration <= attackInterval
                ? "守约（全窗直用）"
                : $"违约 {set.Attack.Duration - attackInterval:0.##}s → 截窗 [{win.Start},{win.Start + win.Count}) {win.Count} 帧 / {win.Duration:0.##}s（≤ 攻隔 {attackInterval:0.##}s ✓）";
            sb.AppendLine($"  守约核查：攻隔 {attackInterval:0.##}s · 原始攻击 {set.Attack.Duration:0.##}s —— {verdict}");
            sb.AppendLine();
        }

        // ─────────────────────── 动作分类（名字打分，越小越优先） ───────────────────────

        private static bool IsExcluded(string clipName)
        {
            string n = clipName.ToLowerInvariant();
            return n.Contains("hit") || n.Contains("breathing") || n.Contains("appear") || n.Contains("blink")
                || n.Contains("alert") || n.Contains("jump") || n.Contains("takeoff") || n.Contains("landing")
                || n.Contains("fall") || n.Contains("projectile") || n.Contains("fx");
        }

        private static int IdleRank(string n)
        {
            n = n.ToLowerInvariant();
            return n.Contains("idle") || n.Contains("ilde") ? 0 : int.MaxValue;   // Ilde = 素材自带拼写错误
        }

        private static int WalkRank(string n)
        {
            n = n.ToLowerInvariant();
            if (n.Contains("run")) return 0;
            if (n.Contains("walk")) return 1;
            if (n.Contains("fly")) return 2;   // 飞行怪的移动
            return int.MaxValue;
        }

        private static int AttackRank(string n)
        {
            n = n.ToLowerInvariant();
            if (n.Contains("attack1")) return 0;
            if (n.Contains("attack2")) return 2;
            if (n.Contains("attack")) return 1;
            return int.MaxValue;
        }

        private static int DeathRank(string n)
        {
            n = n.ToLowerInvariant();
            if (n.Contains("death")) return 0;
            if (n.Contains("dead_") || n == "dead") return 1;
            if (n.Contains("deadground")) return 2;
            if (n.Contains("deadimpact")) return 3;
            if (n.Contains("deadair")) return 4;
            return int.MaxValue;
        }

        // ─────────────────────── 基建 ───────────────────────

        private static Dictionary<string, AnimationClip> LoadClips(string folder)
        {
            var map = new Dictionary<string, AnimationClip>();
            foreach (var guid in AssetDatabase.FindAssets("t:AnimationClip", new[] { folder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
                if (clip != null) map[clip.name] = clip;
            }
            return map;
        }

        private static string FirstKeyContaining(Dictionary<string, AnimationClip> clips, string token)
        {
            foreach (var kv in clips)
                if (kv.Key.ToLowerInvariant().Contains(token)) return kv.Key;
            return null;
        }

        /// <summary>提取 .anim 的 m_Sprite 对象曲线（按时间序）——空曲线返回空数组（空壳判定用）。</summary>
        private static Sprite[] ExtractFrames(AnimationClip clip)
        {
            var frames = new List<Sprite>();
            foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            {
                if (b.propertyName != "m_Sprite") continue;
                var curve = AnimationUtility.GetObjectReferenceCurve(clip, b);
                var keys = curve.OrderBy(k => k.time).ToArray();
                foreach (var k in keys)
                    if (k.value is Sprite s) frames.Add(s);
            }
            return frames.ToArray();
        }

        private static UnitActorClipSet.Clip BuildClip(AnimationClip clip, Sprite[] frames)
            => new UnitActorClipSet.Clip
            {
                Frames = frames,
                Duration = clip.length,
                FramesPerSecond = frames.Length / Math.Max(clip.length, 0.001f),
            };

        /// <summary>立牌材质：缺失则自动补建（引用 Assets/Shaders/HD2D_LayeredLitSprite——shader 源文件
        /// 留在 Assets 入 git；材质必须落在 Res/Art/UnitActorClips/ 才进 YooAsset 收集范围）。
        /// 已存在则不动（保留手工调参）。真机打包时 shader 需另行收集（阶段6 Host 模式一并处理）。</summary>
        private static void EnsureBillboardMaterial()
        {
            string path = $"{ClipSetDir}/M_Billboard.mat";
            if (AssetDatabase.LoadAssetAtPath<Material>(path) != null) return;
            Shader shader = Shader.Find("HD2D/LayeredLitSprite");
            if (shader == null)
            {
                Debug.LogError("[烘焙] 找不到 HD2D/LayeredLitSprite shader——材质未建");
                return;
            }
            var mat = new Material(shader);
            AssetDatabase.CreateAsset(mat, path);
            Debug.Log($"[烘焙] 立牌材质已补建：{path}");
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            string leaf = Path.GetFileName(path);
            if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
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
