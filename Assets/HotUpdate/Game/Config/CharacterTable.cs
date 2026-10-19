using System;
using System.Collections.Generic;

namespace AutoChess.Game
{
    /// <summary>
    /// 棋子查表：int id → CharacterDef。
    /// 为什么是"实例 + 可注入"而不是静态类：正式内容由阶段4 从 Res/Config 读取后
    /// 注入新实例；Default 是代码内置基线，测试与未注入配置时兜底。
    /// ScriptableObject 永不进逻辑层（引擎类型）；表现层只当"搬运工"：
    /// 读 SO → 填 CharacterDef struct → 喂给本构造器。
    /// </summary>
    public sealed class CharacterTable
    {
        private readonly Dictionary<int, CharacterDef> _byId;
        private readonly List<CharacterDef> _ordered;              // 保持传入顺序（All / GetByCost 都按它走）
        private readonly Dictionary<int, List<CharacterDef>> _byCost; // 阶段2 商店加权抽取用

        public CharacterTable(IReadOnlyList<CharacterDef> defs)
        {
            if (defs == null) throw new ArgumentNullException(nameof(defs));

            _byId = new Dictionary<int, CharacterDef>(defs.Count);
            _ordered = new List<CharacterDef>(defs.Count);
            _byCost = new Dictionary<int, List<CharacterDef>>();

            foreach (var def in defs)
            {
                if (def.Id <= 0)
                    throw new ArgumentException($"棋子 Id 必须为正整数（当前 {def.Id}，显示名 {def.DisplayName}）");
                if (_byId.ContainsKey(def.Id))
                    throw new ArgumentException($"棋子 Id 重复：{def.Id}（{def.DisplayName}）");

                _byId.Add(def.Id, def);
                _ordered.Add(def);

                if (!_byCost.TryGetValue(def.Cost, out var bucket))
                {
                    bucket = new List<CharacterDef>();
                    _byCost.Add(def.Cost, bucket);
                }
                bucket.Add(def);
            }
        }

        /// <summary>代码内置基线：测试与未注入配置时的兜底（正式名单阶段3 平衡期定）。</summary>
        public static CharacterTable Default { get; } = new CharacterTable(BuildDefaultDefs());

        public int Count => _ordered.Count;

        /// <summary>全部定义（保持构造时的传入顺序）。</summary>
        public IReadOnlyList<CharacterDef> All => _ordered;

        /// <summary>查表：未知 id 返回 CharacterDef.None（Id=0）。调用方先查 Id 再用。</summary>
        public CharacterDef Get(int id)
        {
            return _byId.TryGetValue(id, out var def) ? def : CharacterDef.None;
        }

        public bool TryGet(int id, out CharacterDef def)
        {
            if (_byId.TryGetValue(id, out def)) return true;
            def = CharacterDef.None;
            return false;
        }

        /// <summary>按费用取全部棋子（商店第二段加权抽取用，阶段2）。无该费档返回空表而非 null。</summary>
        public IReadOnlyList<CharacterDef> GetByCost(int cost)
        {
            return _byCost.TryGetValue(cost, out var bucket) ? bucket : Array.Empty<CharacterDef>();
        }

        /// <summary>
        /// 代码内置基线：决策 #22② 的 18 棋子矩阵（占位名 + 占位面板 + 双标签）——
        /// 与 SynergyTable.Default（六族 v1）、ShopPool（18 种 229 份）互为咬合的测试基线；
        /// 面板数值 = 第一版锚点，阶段3 平衡期连千场压测统一重定。
        /// 蓝条数据纪律（批次5.5）：3 费+棋子配 MaxMana>0（1~2 费恒 0 = 无蓝条单位）——
        /// 这是表数据纪律，不写 if(cost) 进战斗循环。
        /// </summary>
        private static CharacterDef[] BuildDefaultDefs()
        {
            return new[]
            {
                Def( 1, "野猪武士",   1, new[] { 1, 2 },  60, 12, 35, 25, 1, 1.1f, 1.0f),
                Def( 2, "兽人投矛手", 1, new[] { 1, 4 },  55, 14, 25, 20, 2, 1.2f, 1.0f),
                Def( 3, "民兵",       1, new[] { 2, 6 },  65, 11, 30, 30, 1, 1.2f, 1.0f),
                Def( 4, "巫术学徒",   1, new[] { 3, 5 },  45,  9, 20, 25, 2, 1.4f, 1.0f),
                Def( 5, "魔弹射手",   1, new[] { 3, 4 },  45, 13, 20, 20, 3, 1.3f, 1.1f),

                Def( 6, "暗狼刺客",   2, new[] { 1, 5 },  70, 16, 25, 20, 1, 1.0f, 0.9f),
                Def( 7, "符文守卫",   2, new[] { 2, 3 },  80, 13, 40, 35, 1, 1.3f, 1.0f),
                Def( 8, "夜行狙击手", 2, new[] { 4, 5 },  55, 17, 20, 20, 3, 1.4f, 1.0f),
                Def( 9, "弓卫侍从",   2, new[] { 6, 4 },  60, 15, 25, 25, 3, 1.2f, 1.0f),
                Def(10, "随军牧师",   2, new[] { 6, 3 },  65, 12, 25, 30, 2, 1.3f, 1.0f),

                Def(11, "战团督军",   3, new[] { 1, 2 }, 150, 22, 45, 35, 1, 1.2f, 1.0f, maxMana: 60),
                Def(12, "王家神射",   3, new[] { 4, 6 },  90, 26, 25, 25, 4, 1.3f, 1.0f, maxMana: 60),
                Def(13, "战地法师",   3, new[] { 3, 5 },  85, 14, 25, 35, 2, 1.5f, 1.0f, maxMana: 60),

                Def(14, "影刃",       4, new[] { 5, 4 }, 160, 32, 30, 25, 1, 0.9f, 0.8f, maxMana: 70),
                Def(15, "铁甲巨兽",   4, new[] { 2, 1 }, 320, 24, 60, 50, 1, 1.6f, 1.1f, maxMana: 70),
                Def(16, "宫廷大法师", 4, new[] { 6, 3 }, 150, 18, 30, 40, 3, 1.5f, 1.0f, maxMana: 70),

                Def(17, "圣殿之王",   5, new[] { 6, 2 }, 500, 35, 70, 60, 1, 1.4f, 1.0f, maxMana: 80),
                Def(18, "魔渊兽王",   5, new[] { 5, 1 }, 450, 45, 50, 45, 1, 1.1f, 0.9f, maxMana: 80),
            };

            // 速记工厂：hp/atk/armor(双抗同值)/range/间隔 + 可选蓝条（3 费+）；
            // 法强基准 100、暴击出厂基准 5/50（#10/#12 口径）；星级放大只动血/攻（阶段1 规则）
            static CharacterDef Def(int id, string name, int cost, int[] synergies,
                int hp, int atk, int armor, int mr, int range, float atkIv, float moveIv, int maxMana = 0)
                => new CharacterDef(id, name, cost,
                    new UnitStats
                    {
                        MaxHp = hp,
                        AttackPower = atk,
                        Armor = armor,
                        MagicResist = mr,
                        AbilityPower = 100,
                        AttackRange = range,
                        AttackInterval = atkIv,
                        MoveInterval = moveIv,
                        CritChance = 5,
                        CritDamage = 50,
                        MaxMana = maxMana,
                        ManaPerHit = maxMana > 0 ? 10 : 0,
                        ManaRegenPerSecond = maxMana > 0 ? 2 : 0,
                    },
                    synergies);
        }
    }
}