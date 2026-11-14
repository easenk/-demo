using System;
using System.Collections.Generic;

namespace AutoChess.Presentation
{
    /// <summary>
    /// 怪种视觉 id（批次4C）：12 怪种是封闭集合 → enum（P3-5 类型形态划线原则）。
    /// 枚举值 = 素材文件夹号 + 1（文件夹 0~11 ↔ 枚举 1~12），视觉键 = EnemyKeyBase + 枚举值。
    /// 怪种名单与 EnemyTable.Default 的 DisplayName 一一对应（测试锁一致性；改名要两头同步改）。
    /// 2026-11-06 拍板的文件夹语义：0=宝箱怪（唯一掉矿怪）、1~8=普通怪、9/10=双精英、11=Boss；
    /// 魔眼妖（文件夹4）与刺毛虫（文件夹5）为新增普通怪种占位名（视觉=独眼触手施法怪 / 蓝色多刺毛虫）。
    /// </summary>
    public enum EnemyVisualId
    {
        None = 0,
        TreasureMimic = 1,   // 宝箱怪（文件夹0，奖励关专用、唯一掉矿怪）
        Wolf = 2,            // 野狼（文件夹1）
        GolemPuppet = 3,     // 石像傀儡（文件夹2）
        Whelp = 4,           // 幼龙（文件夹3）
        EvilEye = 5,         // 魔眼妖（文件夹4，新怪种占位名）
        SpineWorm = 6,       // 刺毛虫（文件夹5，新怪种占位名）
        Ghost = 7,           // 幽灵（文件夹6）
        StoneGiant = 8,      // 石像巨人（文件夹7）
        Ogre = 9,            // 食人魔（文件夹8）
        Chieftain = 10,      // 兽人酋长（文件夹9，精英R6）
        UndeadLord = 11,     // 亡灵领主（文件夹10，精英R11）
        AbyssLord = 12,      // 深渊魔王（文件夹11，Boss R14）
    }

    /// <summary>一个视觉键的外观与动画口径定义（#23⑥ 动画数据契约的「规划口径」那一半）。</summary>
    public sealed class UnitVisualDef
    {
        /// <summary>烘焙 SO 的 YooAsset location（无扩展名路径，Res/Art/UnitActorClips/ 下）。</summary>
        public string ClipSetPath;

        /// <summary>体型倍率：最终缩放 = BaseHeight / 素材原始身高 × 本倍率——费档/怪种梯度（贵贱一眼可辨）。</summary>
        public float SizeMultiplier = 1f;

        /// <summary>素材原画朝向（抽查：角色组朝左、怪物组朝右；v1 批量默认，真台录像逐个核对修正）。
        /// 运行期按阵营换算：玩家侧要朝右、敌方要朝左，素材朝向与所需不符时 flipX。</summary>
        public bool SourceFacesRight;

        /// <summary>命中帧在原始攻击 clip 中的位置 [0,1]（v1 默认 0.5，4F 打磨期逐单位肉眼调）。</summary>
        public float AttackHitPercent = 0.5f;

        /// <summary>死亡窗上限（秒）——超长死亡动画取尾部窗保住倒地末帧，1.2 是让格节奏的默认上限。</summary>
        public float DeathMaxDuration = 1.2f;

        /// <summary>相对格心偏移（世界单位，2026-11-14 用户战台左下角格亲手校准）：单位放到任何格子，
        /// 身体位置 = 该格中心 + 本偏移——所见即所得（飞行悬浮的 y 抬升与脚底贴格微调全在此，x/z 为水平修正；
        /// 取代旧脚底补偿公式与 FlyHeight 字段）。</summary>
        public float OffsetX, OffsetY, OffsetZ;

        /// <summary>一格移动补间时长（秒，战斗走位恒 1 格；#23⑥ 移动补间归本契约）。</summary>
        public float MoveDuration = 0.30f;
    }

    /// <summary>
    /// 视觉配置表（批次4C）：视觉键 → 外观定义 + 截窗纯函数。
    /// 视觉键口径：1~18 = 角色 CharId；EnemyKeyBase + (int)EnemyVisualId = 怪种。
    /// 截窗（#23⑥ 守约纪律的兑现手段）：素材攻击动画 1~3.5 秒 vs 攻隔 0.9~1.6 秒大面积违约，
    /// 处置=剪辑而非压缩——攻击取「命中帧为锚的中段窗」（预算=攻隔×帧率，前 70% 后 30%），
    /// 死亡取尾部窗；规划器与播放器读同一份窗口结果（同一份数据、两边各算各的）。
    /// 纯函数、零引擎依赖——EditMode 直接测。
    /// </summary>
    public static class UnitActorConfig
    {
        /// <summary>怪种视觉键基数（角色键 1~18 与之不相交）。</summary>
        public const int EnemyKeyBase = 1000;

        /// <summary>体型归一基准（世界单位）：格距 2 的战台上单位的目标身高（再乘费档/怪种倍率）。
        /// 2026-11-14 用户观感"太小"两轮上调：1.45 → 1.75 → 2.0（身高≈格宽=自走棋主流视觉锚点；
        /// 低费角色 1.7 米、5费 2.5、Boss 3 米）——真台观感定稿由用户拍板。</summary>
        public const float BaseHeight = 2.0f;

        private static readonly Dictionary<int, UnitVisualDef> Defs = BuildDefs();

        /// <summary>查定义：未知视觉键返回 null（调用方 fail fast，宁可炸不可静默错画）。</summary>
        public static UnitVisualDef Get(int visualKey) =>
            Defs.TryGetValue(visualKey, out UnitVisualDef def) ? def : null;

        /// <summary>敌方怪种名 → 视觉键（撮合用：EnemyRoster 的 DisplayName 在这里换算）。
        /// 未知名字返回 0（fail fast 同上）。</summary>
        public static int EnemyKeyFromName(string displayName)
        {
            switch (displayName)
            {
                case "宝箱怪": return EnemyKeyBase + (int)EnemyVisualId.TreasureMimic;
                case "野狼": return EnemyKeyBase + (int)EnemyVisualId.Wolf;
                case "石像傀儡": return EnemyKeyBase + (int)EnemyVisualId.GolemPuppet;
                case "幼龙": return EnemyKeyBase + (int)EnemyVisualId.Whelp;
                case "魔眼妖": return EnemyKeyBase + (int)EnemyVisualId.EvilEye;
                case "刺毛虫": return EnemyKeyBase + (int)EnemyVisualId.SpineWorm;
                case "幽灵": return EnemyKeyBase + (int)EnemyVisualId.Ghost;
                case "石像巨人": return EnemyKeyBase + (int)EnemyVisualId.StoneGiant;
                case "食人魔": return EnemyKeyBase + (int)EnemyVisualId.Ogre;
                case "兽人酋长": return EnemyKeyBase + (int)EnemyVisualId.Chieftain;
                case "亡灵领主": return EnemyKeyBase + (int)EnemyVisualId.UndeadLord;
                case "深渊魔王": return EnemyKeyBase + (int)EnemyVisualId.AbyssLord;
                default: return 0;
            }
        }

        // ─────────────────────── 截窗纯函数（规划器与播放器共用的口径） ───────────────────────

        /// <summary>截窗结果：[Start, Start+Count) 帧区间 + 窗内时长 + 命中帧的窗内百分比。</summary>
        public readonly struct ActionWindow
        {
            public readonly int Start, Count;
            public readonly float Duration;
            public readonly float HitPercent;

            public ActionWindow(int start, int count, float duration, float hitPercent)
            {
                Start = start; Count = count; Duration = duration; HitPercent = hitPercent;
            }
        }

        /// <summary>
        /// 攻击窗：全长 ≤ 攻隔（守约）→ 全窗零剪辑；超长（违约）→ 以命中帧为锚的中段窗——
        /// 预算帧数 = floor(攻隔 × 帧率)，锚前占 70%（蓄力）、锚后占 30%（收招），至少各 1 帧；
        /// 命中帧永远被夹在窗内（钳制），窗内时长恒 ≤ 攻隔（守约由截窗恢复，测试逐单位断言）。
        /// </summary>
        public static ActionWindow ResolveAttackWindow(int totalFrames, float fps, float clipDuration,
            float attackInterval, float hitPercent)
        {
            if (totalFrames < 1) throw new ArgumentException("攻击窗至少需要 1 帧");
            if (fps <= 0f) throw new ArgumentException("帧率必须为正");
            if (attackInterval <= 0f) throw new ArgumentException("攻击间隔必须为正（CombatSystem 的除数）");
            if (hitPercent < 0f || hitPercent > 1f) throw new ArgumentException("命中帧百分比必须在 [0,1]");

            if (clipDuration <= attackInterval)
                return new ActionWindow(0, totalFrames, clipDuration, hitPercent);

            int budget = Math.Max(2, (int)Math.Floor(attackInterval * fps));
            int count = Math.Min(totalFrames, budget);
            int hitFrame = Math.Max(0, Math.Min(totalFrames - 1,
                (int)Math.Round(hitPercent * (totalFrames - 1))));

            int back = Math.Max(1, (int)Math.Round(count * 0.7f));   // 锚前（蓄力）份额
            int start = hitFrame - back;
            start = Math.Max(0, Math.Min(start, totalFrames - count));       // 窗不越出 clip
            // 命中帧钳进窗内（hitFrame 贴边时窗整体平移）
            if (hitFrame < start) start = Math.Max(0, hitFrame - 1);
            if (hitFrame > start + count - 1) start = Math.Min(totalFrames - count, hitFrame - count + 1);
            start = Math.Max(0, start);

            float duration = count / fps;
            float inWindow = count <= 1 ? 1f : (hitFrame - start) / (float)(count - 1);
            return new ActionWindow(start, count, duration, inWindow);
        }

        /// <summary>
        /// 死亡窗：全长 ≤ 上限 → 全窗；超长 → 取尾部（保住「倒地末帧」定格——素材死亡动画中后段
        /// 才是倒下过程，截头留尾；前段挣扎让位给 1.2 秒的让格节奏上限）。
        /// </summary>
        public static ActionWindow ResolveDeathWindow(int totalFrames, float fps, float clipDuration, float maxDuration)
        {
            if (totalFrames < 1) throw new ArgumentException("死亡窗至少需要 1 帧");
            if (fps <= 0f) throw new ArgumentException("帧率必须为正");
            if (maxDuration <= 0f) throw new ArgumentException("死亡窗上限必须为正");

            if (clipDuration <= maxDuration)
                return new ActionWindow(0, totalFrames, clipDuration, 1f);

            // 尾部窗：帧数用 floor——ceil 会把窗时长顶过上限（1.2s×12fps=14.4 帧，ceil 15 帧=1.25s 超窗）
            int count = Math.Max(1, Math.Min(totalFrames, (int)Math.Floor(maxDuration * fps)));
            int start = totalFrames - count;
            return new ActionWindow(start, count, count / fps, 1f);
        }

        // ─────────────────────── 默认表（18 角色 + 12 怪种） ───────────────────────

        private static Dictionary<int, UnitVisualDef> BuildDefs()
        {
            var map = new Dictionary<int, UnitVisualDef>(30);

            // 角色：roleN ↔ 文件夹 N-1 ↔ CharId N（2026-11-06 拍板 0 基映射）。
            // 体型倍率与相对格心偏移（2026-11-14 用户战台亲手校准定稿：第一轮统一拖 3 后第二轮微调
            // ——N02=4/N03=4.5/N14=3.5/N16=4、怪组 E01/02/06/07/08=4/E04=2.7/E05=5/E09=2/E10/E11=5；
            // 偏移=用户把各单位逐个挪到左下角格对准后的"相对格心偏移"读回值，所见即所得；
            // 倍率=用户值×素材身高/2.0 换算）。素材本体朝右（2026-11-14 真台实测翻正）。
            void Role(int charId, float size, float ox, float oy, float oz)
            {
                map[charId] = new UnitVisualDef
                {
                    ClipSetPath = $"Assets/Res/Art/UnitActorClips/Role_{charId}",
                    SizeMultiplier = size,
                    SourceFacesRight = true,    // 角色组素材本体朝右（2026-11-14 真台实测）
                    OffsetX = ox, OffsetY = oy, OffsetZ = oz,
                };
            }
            Role(1, 1.215f, 0f, -0.256f, -0.300f);
            Role(2, 2.02f, 0f, -0.340f, -0.598f);
            Role(3, 2.2725f, 0f, -0.295f, -0.542f);
            Role(4, 1.965f, 0.233f, 0.023f, -0.542f);     // 飞行（用户点名）
            Role(5, 1.215f, 0f, -0.202f, -0.542f);
            Role(6, 1.215f, 0.140f, -0.040f, -0.542f);    // 飞行
            Role(7, 1.215f, 0f, -0.286f, -0.542f);
            Role(8, 1.515f, 0f, -0.248f, -0.542f);        // 飞行
            Role(9, 1.215f, 0f, -0.204f, -0.542f);
            Role(10, 1.515f, 0f, -0.223f, -0.542f);       // 飞行
            Role(11, 2.115f, 0f, -0.097f, -0.542f);
            Role(12, 2.115f, 0f, -0.174f, -0.542f);
            Role(13, 1.515f, 0f, -0.137f, -0.542f);
            Role(14, 2.1175f, 0f, -0.158f, -0.542f);
            Role(15, 1.215f, 0f, -0.244f, -0.542f);
            Role(16, 1.62f, 0f, 0.005f, -0.542f);         // 飞行
            Role(17, 1.965f, 0f, -0.187f, -0.542f);
            Role(18, 1.815f, 0f, -0.210f, -0.542f);

            // 怪物素材本体朝向（2026-11-14 核对合影+真台二次翻正——合影模拟玩家侧、flipX=!src，
            // 怪组在合影中未翻面=显示即本体朝向）：八怪本体朝左（野狼/石像傀儡/幼龙/魔眼妖/
            // 刺毛虫/幽灵/石像巨人/兽人酋长）=false；四怪本体朝右（宝箱怪/食人魔/亡灵领主/
            // 深渊魔王）=true——首次翻正误把合影当翻转结果反推，十二怪全部背对玩家，用户抓回。
            void Enemy(EnemyVisualId id, float size, float ox, float oy, float oz, bool facesRight)
            {
                map[EnemyKeyBase + (int)id] = new UnitVisualDef
                {
                    ClipSetPath = $"Assets/Res/Art/UnitActorClips/Enemy_{(int)id - 1}",
                    SizeMultiplier = size,
                    SourceFacesRight = facesRight,
                    OffsetX = ox, OffsetY = oy, OffsetZ = oz,
                };
            }
            Enemy(EnemyVisualId.TreasureMimic, 1.68f, 0f, 0.560f, -0.016f, true);
            Enemy(EnemyVisualId.Wolf, 2.00f, 0f, 0.358f, -0.001f, false);
            Enemy(EnemyVisualId.GolemPuppet, 1.48f, 0f, 0.511f, 0.124f, false);
            Enemy(EnemyVisualId.Whelp, 1.92f, 0f, 0.511f, 0.124f, false);          // 飞行（用户点名）
            Enemy(EnemyVisualId.EvilEye, 2.295f, 0f, 1.432f, 0.506f, false);       // 飞行
            Enemy(EnemyVisualId.SpineWorm, 2.50f, 0f, 0.364f, 0.042f, false);
            Enemy(EnemyVisualId.Ghost, 1.92f, 0.199f, 0.511f, -0.043f, false);      // 飞行
            Enemy(EnemyVisualId.StoneGiant, 2.56f, 0f, 0.511f, -0.006f, false);
            Enemy(EnemyVisualId.Ogre, 1.92f, -0.103f, 0.604f, 0.172f, true);
            Enemy(EnemyVisualId.Chieftain, 2.00f, 0f, 0.786f, 0.124f, false);
            Enemy(EnemyVisualId.UndeadLord, 2.525f, 0f, -0.352f, -0.539f, true);
            Enemy(EnemyVisualId.AbyssLord, 3.025f, 0f, -0.337f, -0.524f, true);

            return map;
        }
    }
}
