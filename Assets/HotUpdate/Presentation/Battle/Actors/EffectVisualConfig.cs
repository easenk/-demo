using System.Collections.Generic;
using UnityEngine;   // 4F 起仅取 Color 值类型（分色映射）——仍是纯数据映射，EditMode 直测不受影响

namespace AutoChess.Presentation
{
    /// <summary>
    /// 一条技能的视觉配置（批次4D）：全部字段可空——按 SkillDelivery 与纯 buff 路由消费，
    /// 空串 = 本技能不用该类演出。路径真源在本表（.prefab 全路径，AssetDatabase 可直测）；
    /// YooAsset 加载侧由 BattleRoot 去扩展名换 location（M_Billboard 同款口径）。
    /// </summary>
    public sealed class SkillVisual
    {
        /// <summary>弹道技能的飞行弹体（SkillDelivery.Projectile）。</summary>
        public string ProjectilePath;

        /// <summary>弹体体型倍率（穿云箭/大法师大招放大——气势与普攻弹区分）。</summary>
        public float ProjectileScale = 1f;

        /// <summary>施法者出手特效（挥砍/斩击类，播在施法者格、施法出手时刻）。</summary>
        public string CasterEffectPath;

        /// <summary>第二道施法者特效（深渊裂击十字的纵刀；Yaw 用 CasterEffect2Yaw）。</summary>
        public string CasterEffect2Path;

        /// <summary>第二道特效的绕 Y 旋转角（度）。</summary>
        public float CasterEffect2Yaw;

        /// <summary>锚点特效：弹道技能=到点爆炸（锚=主目标）；范围技能=中心演出（锚=施法者）。</summary>
        public string AnchorEffectPath;

        /// <summary>施法块内每个受击者格的特效（震荡波；空=受击者走通用命中反馈）。</summary>
        public string VictimEffectPath;

        /// <summary>首个受击者的附加特效（影袭淬毒的毒雾标识）。</summary>
        public string VictimExtraPath;

        /// <summary>纯 buff 技能的目标短爆特效（脚底光环/治疗；2026-11-12 拍板：只做施法瞬间短爆，
        /// 「挂 buff 期间持续光效」需要 Buff 生命周期进事件流，越 4D 范围挂账 4F+）。</summary>
        public string BuffBurstPath;
    }

    /// <summary>
    /// 弹道与技能视觉映射表（批次4D，2026-11-12 拍板口径）：
    ///  · 普攻弹体：远程 9 棋子 → 来源1 五款弹体（三款共款分色挂 4F）；
    ///  · 伤害技能 7 条按 SkillDelivery 路由（挥砍/弹道/范围/淬毒）；
    ///  · 纯 buff 11 条按效果分色（红=攻击系、蓝=防御与回蓝、单体治疗对口）；
    ///  · 通用命中反馈：物理/魔法两路（魔法按施放者棋子分——四术士）。
    /// 仍缺素材的位（箭矢/标枪/地裂专用）按 4C 占位惯例代用先行、到货热插拔（改本表即可）。
    /// 纯 C# 零引擎依赖——EditMode 直测（覆盖完备性 + 磁盘存在性双锁）。
    /// </summary>
    public static class EffectVisualConfig
    {
        // ── 普攻弹体（来源1：Hovl Toon Projectiles 2）──
        private const string Trident = "Assets/Res/Art/特效/普攻弹体/素材来源1/紫色三叉戟弹体.prefab";
        private const string RedBullet = "Assets/Res/Art/特效/普攻弹体/素材来源1/红焰子弹弹体.prefab";
        private const string PlainBullet = "Assets/Res/Art/特效/普攻弹体/素材来源1/普通子弹弹体.prefab";
        private const string GoldOrb = "Assets/Res/Art/特效/普攻弹体/素材来源1/黄光圆球弹体.prefab";
        private const string LightningOrb = "Assets/Res/Art/特效/普攻弹体/素材来源1/闪电黑洞弹体.prefab";

        // ── 通用命中反馈（来源1 受击四件之二）──
        private const string PhysicalHit = "Assets/Res/Art/特效/Hit特效/素材来源1/枪械子弹受击.prefab";
        private const string MagicHit = "Assets/Res/Art/特效/Hit特效/素材来源1/紫色闪电受击.prefab";

        // ── buff 与治疗（来源2：Lana Casual RPG VFX）──
        private const string RedAura = "Assets/Res/Art/特效/buff特效/持续脚底光环/素材来源2/红色脚底光环.prefab";
        private const string BlueAura = "Assets/Res/Art/特效/buff特效/持续脚底光环/素材来源2/蓝色脚底光环.prefab";
        private const string HealOne = "Assets/Res/Art/特效/buff特效/素材来源2/单体治疗特效.prefab";

        // ── 挥砍与落地（来源2）──
        private const string SlashRedYellow = "Assets/Res/Art/特效/技能特效/挥砍/素材来源2/红黄色挥砍.prefab";
        private const string SlashDarkPurple = "Assets/Res/Art/特效/技能特效/挥砍/素材来源2/暗紫色挥砍.prefab";
        private const string LightningLand = "Assets/Res/Art/特效/技能特效/垂直落地技能/素材来源2/圆形闪电范围落地.prefab";
        private const string LightBurst = "Assets/Res/Art/特效/技能特效/非落地技能/素材来源2/光元素聚集爆炸.prefab";

        // ── 毒雾（来源2）与刀刃/震荡波（来源3：Piloto Slashes Projectiles）──
        private const string PoisonFog = "Assets/Res/Art/特效/Fog特效/素材来源2/毒雾特效.prefab";
        private const string BladeDarkRed = "Assets/Res/Art/特效/刀刃特效/刀刃/素材来源3/暗红色刀刃.prefab";
        private const string WaveBlood = "Assets/Res/Art/特效/刀刃特效/刀刃受击震荡波/素材来源3/血红色震荡波.prefab";
        private const string WaveDark = "Assets/Res/Art/特效/刀刃特效/刀刃受击震荡波/素材来源3/暗元素震荡波.prefab";
        private const string WaveWood = "Assets/Res/Art/特效/刀刃特效/刀刃受击震荡波/素材来源3/木元素震荡波.prefab";
        private const string WaveDarkRed = "Assets/Res/Art/特效/刀刃特效/刀刃受击震荡波/素材来源3/暗红色震荡波.prefab";

        /// <summary>远程 9 棋子的普攻弹体（melee 查无 = null，查表即路由，不用 CharacterTable）。</summary>
        public static string ProjectilePathForChar(int charId)
        {
            switch (charId)
            {
                case 2: case 9: case 12: return Trident;      // 投矛手/弓卫/神射：箭形代用（缺真箭矢与标枪，4F）
                case 5: case 13: return RedBullet;            // 魔弹射手/战地法师：共款分色挂 4F
                case 8: return PlainBullet;                   // 夜行狙击手
                case 10: return GoldOrb;                      // 随军牧师（圣光弹）
                case 4: case 16: return LightningOrb;         // 巫术学徒/宫廷大法师：共款，4F 换小
                default: return null;
            }
        }

        /// <summary>普攻弹体体型倍率（4F）：闪电黑洞偏大（4D 真台挂账）→ 普攻档缩小 0.65；
        /// 技能档（13=1.0 / 16=1.8）是"大招弹"气势位，不走本表、保持原值。</summary>
        public static float ProjectileScaleForChar(int charId)
            => charId == 4 || charId == 16 ? 0.65f : 1f;

        /// <summary>普攻弹体分色（4F）：共款弹体按棋子给色（Color.black=不染色，用原模板）。
        /// 颜色由素材粒子的 startColor 携带（材质全白无 _TintColor——2026-11-14 侦查实锤），
        /// 染色=克隆变体模板时乘入 startColor，素材本体与共享材质不动。</summary>
        public static Color ProjectileTintForChar(int charId)
        {
            switch (charId)
            {
                case 2: return Color.black;              // 投矛手：三叉戟本色（紫）
                case 9: return new Color(0.35f, 0.75f, 1f);   // 弓卫：青蓝箭
                case 12: return new Color(1f, 0.85f, 0.3f);   // 神射：金箭
                case 5: return Color.black;              // 魔弹射手：红焰本色
                case 13: return new Color(1f, 0.5f, 0.1f);   // 战地法师：橙焰
                case 16: return Color.black;             // 大法师：闪电本色（普攻已缩 0.65 与学徒区分）
                default: return Color.black;
            }
        }

        /// <summary>魔法系施放者的普攻命中走紫色闪电受击（四术士 4/10/13/16——测试与职业表互锁）。</summary>
        public static bool IsMagicChar(int charId)
            => charId == 4 || charId == 10 || charId == 13 || charId == 16;

        public static string PhysicalImpactPath => PhysicalHit;
        public static string MagicImpactPath => MagicHit;

        /// <summary>技能视觉表（id 与棋子 id 对齐 1~18；测试锁全表覆盖）。</summary>
        public static IReadOnlyDictionary<int, SkillVisual> Skills => _skills;

        private static readonly Dictionary<int, SkillVisual> _skills = BuildSkills();

        private static Dictionary<int, SkillVisual> BuildSkills()
        {
            var map = new Dictionary<int, SkillVisual>(18);

            // ── 纯 buff（1~10 + 17）：施法瞬间短爆，按效果分色 ──
            SkillVisual Buff(int id, string burst) { var v = new SkillVisual { BuffBurstPath = burst }; map[id] = v; return v; }
            Buff(1, BlueAura);                       // 坚鬃：防御（蓝）
            Buff(2, RedAura);                        // 磨矛：攻击（红）
            Buff(3, BlueAura);                       // 列阵：防御
            Buff(4, BlueAura);                       // 法力汲取：回蓝（蓝）
            Buff(5, RedAura);                        // 魔弹蓄能：攻击
            Buff(6, RedAura);                        // 狼影疾袭：攻速（红色系共款，4F 分色）
            Buff(7, BlueAura);                       // 符文壁垒：防御强档
            Buff(8, RedAura);                        // 锁定弱点：攻击
            Buff(9, RedAura);                        // 侍从号令：给攻最高队友（红）
            Buff(10, HealOne);                       // 圣愈术：单体治疗（对口）
            Buff(17, BlueAura);                      // 王权裁决：全队防御（全员蓝光环短爆）

            // ── 伤害技能（11~16、18）：按 SkillDelivery 路由 ──
            map[11] = new SkillVisual
            {
                CasterEffectPath = SlashRedYellow,    // 冲阵号令：整行剑刃（红黄挥砍）
                VictimEffectPath = WaveBlood,         // 行内受击敌格血红震荡波
            };
            map[12] = new SkillVisual
            {
                ProjectilePath = Trident,             // 穿云箭：三叉戟放大代用（缺真大箭）
                ProjectileScale = 1.6f,               // 受击走通用物理命中（枪械子弹受击）
            };
            map[13] = new SkillVisual
            {
                ProjectilePath = LightningOrb,        // 奥术飞弹
                AnchorEffectPath = LightningLand,     // 锚点 3x3 圆形闪电落地
            };
            map[14] = new SkillVisual
            {
                CasterEffectPath = SlashDarkPurple,   // 影袭：暗紫挥砍
                VictimEffectPath = WaveDark,          // 受击者暗元素震荡波
                VictimExtraPath = PoisonFog,          // 首个受击者毒雾（淬毒标识）
            };
            map[15] = new SkillVisual
            {
                AnchorEffectPath = WaveWood,          // 地裂践踏：自身中心木元素震荡波（缺专用地裂，代用）
            };
            map[16] = new SkillVisual
            {
                ProjectilePath = LightningOrb,        // 奥术共鸣
                ProjectileScale = 1.8f,
                AnchorEffectPath = LightBurst,        // 锚点 6x6 光元素聚集爆炸
            };
            map[18] = new SkillVisual
            {
                CasterEffectPath = BladeDarkRed,      // 深渊裂击：横刀
                CasterEffect2Path = BladeDarkRed,     // 纵刀（十字交叉）
                CasterEffect2Yaw = 90f,
                AnchorEffectPath = WaveDarkRed,       // 中心暗红震荡波
            };

            return map;
        }

        /// <summary>全部被引用的预制体路径（去重；BattleRoot 预载与存在性测试共用）。</summary>
        public static IEnumerable<string> AllPaths()
        {
            var list = new List<string>();
            var seen = new HashSet<string>();
            void TryAdd(string p) { if (p != null && seen.Add(p)) list.Add(p); }

            for (int charId = 1; charId <= 18; charId++)
                TryAdd(ProjectilePathForChar(charId));
            TryAdd(PhysicalHit);
            TryAdd(MagicHit);
            foreach (var kv in _skills)
            {
                var v = kv.Value;
                TryAdd(v.ProjectilePath); TryAdd(v.CasterEffectPath); TryAdd(v.CasterEffect2Path);
                TryAdd(v.AnchorEffectPath); TryAdd(v.VictimEffectPath); TryAdd(v.VictimExtraPath);
                TryAdd(v.BuffBurstPath);
            }
            return list;
        }
    }
}
