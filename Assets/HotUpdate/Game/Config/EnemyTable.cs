using System;
using System.Collections.Generic;

namespace AutoChess.Game
{
    /// <summary>
    /// 一个敌人的生成定义。敌人不是玩家资产：不入卡池、不可买/卖/合成，
    /// 每场战斗按表生成、EnterPrep 整体退场——精英/Boss 的轮换登场因此成立。
    /// DisplayName 仅显示用（表现层按它取怪种立绘/动画；敌人不进 CharacterTable，无 Id）。
    /// </summary>
    public readonly struct EnemySpawn
    {
        /// <summary>仅显示用（禁止当键）。</summary>
        public readonly string DisplayName;

        /// <summary>十三维整块面板——天然适配本工程伤害公式与修饰器（参考工程 6 维散字段不抄）。</summary>
        public readonly UnitStats Stats;

        /// <summary>敌方半场（row 3~5）摆位。</summary>
        public readonly Pos Cell;

        /// <summary>晶矿挡位：0 = 不掉（数据纪律：非奖励关条目一律 0，掉落判断不查回合类型——决策#17③）；1~3 = 低/中/高档。</summary>
        public readonly int OreTier;

        /// <summary>怪物强度等级（决策 #19③/#20③：战绩页展示字段；普通关填表纪律 = 回合越靠后指标越高）。</summary>
        public readonly int PowerLevel;

        /// <summary>技能 id（批次2：0 = 无技能；需配合 Stats.MaxMana&gt;0 才会施法——
        /// 敌方 CritChance=0、MaxMana=0 仍是表数据纪律 D2-7，想要会施法的怪 = 改表，不写死代码）。</summary>
        public readonly int SkillId;

        public EnemySpawn(string displayName, UnitStats stats, Pos cell, int oreTier, int powerLevel, int skillId = 0)
        {
            DisplayName = displayName ?? "";
            Stats = stats;
            Cell = cell;
            OreTier = oreTier;
            PowerLevel = powerLevel;
            SkillId = skillId;
        }
    }

    /// <summary>一场战斗的敌方编成（Spawns 为空 = 本回合不生成敌人）。</summary>
    public readonly struct Encounter
    {
        public readonly EnemySpawn[] Spawns;

        public Encounter(EnemySpawn[] spawns)
        {
            Spawns = spawns;
        }

        /// <summary>空编成判断：null 或零条都算空（未配置的回合天然拿到 default）。</summary>
        public bool IsEmpty => Spawns == null || Spawns.Length == 0;
    }

    /// <summary>编成表的序列化条目：回合号 + 该回合的敌人（SO 资产持有这个形状，运行期重建索引）。</summary>
    public readonly struct EnemyRoundEntry
    {
        public readonly int Round;
        public readonly EnemySpawn[] Spawns;

        public EnemyRoundEntry(int round, EnemySpawn[] spawns)
        {
            Round = round;
            Spawns = spawns;
        }
    }

    /// <summary>
    /// 敌方编成表（数据驱动关卡，同 RoundTable 定位；实例 + 可注入双源模式）。
    /// 构造即校验（fail fast）：回合号 > 0 且不重复、编成非空、面板合法、挡位/强度合法、格子界内——
    /// 建表事故在构造期炸掉，比战斗期炸便宜。
    /// 半场归属（row 3~5）不在构造期校验：半场规则属 Battle 层（BoardData），
    /// 摆位失败由 Session 的"敌方半场首个空格"兜底接住（阶段2-接口设计 3.2）。
    /// </summary>
    public sealed class EnemyTable
    {
        /// <summary>空表：全回合空编成（测试用——敌人全靠测试自摆）。</summary>
        public static EnemyTable Empty { get; } = new EnemyTable();

        public static EnemyTable Default { get; } = BuildDefault();

        private readonly Dictionary<int, Encounter> _byRound = new Dictionary<int, Encounter>();
        private readonly List<EnemyRoundEntry> _entries = new List<EnemyRoundEntry>();

        private EnemyTable() { }

        public EnemyTable(IReadOnlyList<EnemyRoundEntry> entries)
        {
            if (entries == null) throw new ArgumentNullException(nameof(entries));
            foreach (var entry in entries) Register(entry);
        }

        /// <summary>编成表原始条目（SO 资产桥接用；防御性拷贝——改拷贝不污染表）。</summary>
        public EnemyRoundEntry[] Entries => _entries.ToArray();

        /// <summary>某回合的敌方编成。未配置的回合返回空编成（不生成敌人）。</summary>
        public Encounter Get(int round)
            => _byRound.TryGetValue(round, out var e) ? e : default;

        private void Register(EnemyRoundEntry entry)
        {
            if (entry.Round <= 0)
                throw new ArgumentException($"回合号必须 > 0（当前 {entry.Round}）");
            if (_byRound.ContainsKey(entry.Round))
                throw new ArgumentException($"回合号重复：{entry.Round}");
            if (entry.Spawns == null || entry.Spawns.Length == 0)
                throw new ArgumentException($"回合 {entry.Round} 的编成不能为空（不想生成敌人 = 不登记该回合）");

            foreach (var s in entry.Spawns)
            {
                if (s.Stats.MaxHp < 1)
                    throw new ArgumentException($"回合 {entry.Round}「{s.DisplayName}」MaxHp 必须 ≥ 1");
                if (s.Stats.AttackInterval <= 0f || s.Stats.MoveInterval <= 0f)
                    throw new ArgumentException($"回合 {entry.Round}「{s.DisplayName}」间隔是 CombatSystem 的除数，必须 > 0");
                if (s.OreTier < 0 || s.OreTier > 3)
                    throw new ArgumentException($"回合 {entry.Round}「{s.DisplayName}」OreTier 必须为 0~3");
                if (s.PowerLevel < 1)
                    throw new ArgumentException($"回合 {entry.Round}「{s.DisplayName}」PowerLevel 必须 ≥ 1");
                if (s.SkillId < 0)
                    throw new ArgumentException($"回合 {entry.Round}「{s.DisplayName}」SkillId 不能为负（0 = 无技能）");
                if (s.Cell.X < 0 || s.Cell.X >= GameConst.BoardWidth ||
                    s.Cell.Y < 0 || s.Cell.Y >= GameConst.BoardHeight)
                    throw new ArgumentException($"回合 {entry.Round}「{s.DisplayName}」摆位越界：({s.Cell.X}, {s.Cell.Y})");
            }

            _entries.Add(entry);
            _byRound[entry.Round] = new Encounter((EnemySpawn[])entry.Spawns.Clone());   // 防御性拷贝
        }

        // ── 默认攻速/移速：怪种差异化用 E() 的可选参数覆盖 ──
        private const float TrashAttackInterval = 1.3f;
        private const float TrashMoveInterval = 1.0f;

        /// <summary>
        /// 占位编成（结构 = 决策 #20；数值 = 第一版锚点，阶段3 压测期精调）：
        ///  · R1/5/10 奖励关配**特殊怪物**（晶矿鼠/晶矿魔偶/晶矿巨兽——专用怪种、战力较低、100% 掉矿，#20①）；
        ///  · R6/11 精英关 = 战力检验门槛（#20②）；
        ///  · 普通关 PowerLevel 填表纪律：回合越靠后指标越高（#20③，测试焊死不降）；
        ///  · R14 最终 Boss（#20④）；R15 隐藏 Boss 不进表（#20⑤）。
        /// 敌方 CritChance=0、MaxMana=0 是表数据纪律（D2-7），不写死代码——将来想要会暴击/施法的怪 = 改表。
        /// 抗性占位 20~60 区间（百分比公式口径；挂起#6 正式区间 20~120 阶段3 定）。
        /// </summary>
        private static EnemyTable BuildDefault()
        {
            var t = new EnemyTable();

            // ── R1 奖励关：晶矿鼠 ×2（特殊怪：战力低、挡位 1、必掉）──
            t.Register(new EnemyRoundEntry(1, new EnemySpawn[]
            {
                E("晶矿鼠", 30, 5, 20, new Pos(3, 4), oreTier: 1),
                E("晶矿鼠", 30, 5, 20, new Pos(4, 4), oreTier: 1),
            }));

            // ── R2-4：普通关第一梯段（PowerLevel 1→2）──
            t.Register(new EnemyRoundEntry(2, new EnemySpawn[]
            {
                E("野狼", 40, 8, 20, new Pos(3, 3)),
                E("野狼", 40, 8, 20, new Pos(5, 3)),
            }));
            t.Register(new EnemyRoundEntry(3, new EnemySpawn[]
            {
                E("石像傀儡", 55, 10, 25, new Pos(3, 3), powerLevel: 2),
                E("石像傀儡", 55, 10, 25, new Pos(4, 4), powerLevel: 2),
                E("石像傀儡", 55, 10, 25, new Pos(5, 3), powerLevel: 2),
            }));
            t.Register(new EnemyRoundEntry(4, new EnemySpawn[]
            {
                E("幼龙", 60, 11, 25, new Pos(3, 3), moveInterval: 0.9f, powerLevel: 2),   // 幼龙会飞：走得快
                E("幼龙", 60, 11, 25, new Pos(5, 3), moveInterval: 0.9f, powerLevel: 2),
                E("石像傀儡", 60, 11, 25, new Pos(4, 4), powerLevel: 2),
            }));

            // ── R5 奖励关：晶矿魔偶 ×2（特殊怪：挡位 2）──
            t.Register(new EnemyRoundEntry(5, new EnemySpawn[]
            {
                E("晶矿魔偶", 40, 6, 25, new Pos(3, 4), oreTier: 2),
                E("晶矿魔偶", 40, 6, 25, new Pos(4, 4), oreTier: 2),
            }));

            // ── R6 精英关：兽人酋长压阵 + 两个石像傀儡护卫（战力检验门槛）──
            t.Register(new EnemyRoundEntry(6, new EnemySpawn[]
            {
                E("兽人酋长", 320, 18, 40, new Pos(4, 5), powerLevel: 4),
                E("石像傀儡", 70, 12, 25, new Pos(3, 3), powerLevel: 2),
                E("石像傀儡", 70, 12, 25, new Pos(5, 3), powerLevel: 2),
            }));

            // ── R7-9：普通关第二梯段（PowerLevel 3→4）──
            t.Register(new EnemyRoundEntry(7, new EnemySpawn[]
            {
                E("幽灵", 75, 13, 25, new Pos(3, 3), attackInterval: 1.1f, powerLevel: 3),   // 幽灵出手快
                E("幽灵", 75, 13, 25, new Pos(4, 4), attackInterval: 1.1f, powerLevel: 3),
                E("幽灵", 75, 13, 25, new Pos(5, 3), attackInterval: 1.1f, powerLevel: 3),
            }));
            t.Register(new EnemyRoundEntry(8, new EnemySpawn[]
            {
                E("幼龙", 85, 14, 30, new Pos(3, 3), moveInterval: 0.9f, powerLevel: 3),
                E("幼龙", 85, 14, 30, new Pos(5, 3), moveInterval: 0.9f, powerLevel: 3),
                E("石像巨人", 90, 14, 35, new Pos(4, 5), attackInterval: 1.6f, powerLevel: 3),   // 巨人重拳慢
            }));
            t.Register(new EnemyRoundEntry(9, new EnemySpawn[]
            {
                E("食人魔", 170, 16, 35, new Pos(4, 4), attackInterval: 1.4f, powerLevel: 4),   // 准精英体格
                E("幽灵", 85, 14, 30, new Pos(3, 3), attackInterval: 1.1f, powerLevel: 3),
                E("幽灵", 85, 14, 30, new Pos(5, 3), attackInterval: 1.1f, powerLevel: 3),
            }));

            // ── R10 奖励关：晶矿魔偶 + 晶矿巨兽（特殊怪：混挡位示例 2/3——挡位按怪写死，#17③）──
            t.Register(new EnemyRoundEntry(10, new EnemySpawn[]
            {
                E("晶矿魔偶", 50, 7, 30, new Pos(4, 4), oreTier: 2),
                E("晶矿巨兽", 60, 8, 30, new Pos(5, 3), oreTier: 3),
            }));

            // ── R11 精英关：亡灵领主 + 幽灵护卫 ──
            t.Register(new EnemyRoundEntry(11, new EnemySpawn[]
            {
                E("亡灵领主", 400, 22, 50, new Pos(4, 5), powerLevel: 5),
                E("幽灵", 95, 15, 30, new Pos(3, 3), attackInterval: 1.1f, powerLevel: 3),
                E("幽灵", 95, 15, 30, new Pos(5, 3), attackInterval: 1.1f, powerLevel: 3),
            }));

            // ── R12-13：普通关第三梯段（PowerLevel 5）──
            t.Register(new EnemyRoundEntry(12, new EnemySpawn[]
            {
                E("石像巨人", 110, 17, 40, new Pos(3, 3), attackInterval: 1.6f, powerLevel: 5),
                E("石像巨人", 110, 17, 40, new Pos(5, 3), attackInterval: 1.6f, powerLevel: 5),
                E("幽灵", 110, 17, 35, new Pos(2, 4), attackInterval: 1.1f, powerLevel: 4),
                E("幽灵", 110, 17, 35, new Pos(6, 4), attackInterval: 1.1f, powerLevel: 4),
            }));
            t.Register(new EnemyRoundEntry(13, new EnemySpawn[]
            {
                E("石像傀儡", 120, 18, 40, new Pos(3, 3), powerLevel: 5),
                E("石像巨人", 120, 18, 45, new Pos(4, 5), attackInterval: 1.6f, powerLevel: 5),
                E("幽灵", 120, 18, 40, new Pos(5, 3), attackInterval: 1.1f, powerLevel: 5),
                E("幼龙", 120, 18, 40, new Pos(6, 4), moveInterval: 0.9f, powerLevel: 5),
            }));

            // ── R14 最终 Boss：深渊魔王 + 幽灵仪仗 ──
            t.Register(new EnemyRoundEntry(14, new EnemySpawn[]
            {
                E("深渊魔王", 650, 30, 60, new Pos(4, 5), attackInterval: 1.5f, powerLevel: 6),
                E("幽灵", 110, 16, 35, new Pos(3, 4), attackInterval: 1.1f, powerLevel: 4),
                E("幽灵", 110, 16, 35, new Pos(5, 4), attackInterval: 1.1f, powerLevel: 4),
            }));

            return t;
        }

        /// <summary>占位怪速记工厂：hp/atk/armor（armor 同时填双抗），攻速/移速/挡位/强度用可选参数覆盖。</summary>
        private static EnemySpawn E(string name, int hp, int atk, int armor, Pos cell,
            float attackInterval = TrashAttackInterval, float moveInterval = TrashMoveInterval,
            int oreTier = 0, int powerLevel = 1, int skillId = 0)
            => new EnemySpawn(name, Stats(hp, atk, armor, attackInterval, moveInterval), cell, oreTier, powerLevel, skillId);

        /// <summary>敌方默认面板：CritChance=0、MaxMana=0 是表数据纪律（D2-7）——不写死代码，改表即改行为。</summary>
        private static UnitStats Stats(int hp, int atk, int armor, float attackInterval, float moveInterval)
            => new UnitStats
            {
                MaxHp = hp,
                AttackPower = atk,
                Armor = armor,
                MagicResist = armor,     // 占位：双抗同值，等魔法伤害消费者出现再分化
                AbilityPower = 0,
                AttackRange = 1,
                AttackInterval = attackInterval,
                MoveInterval = moveInterval,
                CritChance = 0,
                CritDamage = 0,
                MaxMana = 0,
                ManaPerHit = 0,
                ManaRegenPerSecond = 0,
            };
    }
}
