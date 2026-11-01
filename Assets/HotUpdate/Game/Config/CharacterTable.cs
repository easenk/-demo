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
        /// 面板数值 = 7b 重定（2026-11-01，用户给定金铲铲参考区间 + 职业分化）。
        /// 蓝条数据纪律（7c-2 修订，推翻批次5.5"3 费+才有蓝条"旧纪律）：**全员**配 MaxMana>0 与技能 id——
        /// 1 费 30 / 2 费 40 / 3 费 60 / 4 费 70 / 5 费 80；1~2 费技能 = 纯 buff（拍板原文
        /// "1~2 费没有攻击技能、单纯给自己或我方上 buff"）。仍是表数据纪律，不写 if(cost) 进战斗循环。
        /// 职业与技能标注（阶段3 批次1，决策 #22⑥）：每棋子一个职业（enum，回蓝速率轴）
        /// + 技能 id（int——开放内容表；7c-2 起**技能 id 与棋子 id 对齐 1~18**）。族标签 enum 化（SynergyId，P3-5）。
        /// </summary>
        private static CharacterDef[] BuildDefaultDefs()
        {
            return new[]
            {
                //  id  名字        费  族标签                              职业            技能  血   攻   甲  法抗 射程 攻间隔 移间隔 蓝上限
                //  面板 = 7b 重定（2026-11-01，用户给定金铲铲参考区间 + 职业分化：坦血顶攻底/刺血底攻顶/术攻底法抗高）
                Def( 1, "野猪武士",   1, new[] { SynergyId.Brute, SynergyId.Bulwark },  RoleId.Tank,     1,  580, 36, 38, 34, 1, 1.1f, 1.0f, maxMana: 30),
                Def( 2, "兽人投矛手", 1, new[] { SynergyId.Brute, SynergyId.Ranger },   RoleId.Marksman, 2,  460, 44, 30, 30, 2, 1.2f, 1.0f, maxMana: 30),
                Def( 3, "民兵",       1, new[] { SynergyId.Bulwark, SynergyId.Crown },  RoleId.Tank,     3,  540, 38, 40, 34, 1, 1.2f, 1.0f, maxMana: 30),
                Def( 4, "巫术学徒",   1, new[] { SynergyId.Arcane, SynergyId.Shadow },  RoleId.Mage,     4,  460, 37, 30, 36, 2, 1.4f, 1.0f, maxMana: 30),
                Def( 5, "魔弹射手",   1, new[] { SynergyId.Arcane, SynergyId.Ranger },  RoleId.Marksman, 5,  470, 43, 30, 31, 3, 1.3f, 1.1f, maxMana: 30),

                Def( 6, "暗狼刺客",   2, new[] { SynergyId.Brute, SynergyId.Shadow },   RoleId.Assassin, 6,  620, 55, 36, 36, 1, 1.0f, 0.9f, maxMana: 40),
                Def( 7, "符文守卫",   2, new[] { SynergyId.Bulwark, SynergyId.Arcane }, RoleId.Tank,     7,  780, 42, 45, 42, 1, 1.3f, 1.0f, maxMana: 40),
                Def( 8, "夜行狙击手", 2, new[] { SynergyId.Ranger, SynergyId.Shadow },  RoleId.Marksman, 8,  640, 53, 35, 37, 3, 1.4f, 1.0f, maxMana: 40),
                Def( 9, "弓卫侍从",   2, new[] { SynergyId.Crown, SynergyId.Ranger },   RoleId.Marksman, 9,  660, 50, 37, 38, 3, 1.2f, 1.0f, maxMana: 40),
                Def(10, "随军牧师",   2, new[] { SynergyId.Crown, SynergyId.Arcane },  RoleId.Mage,     10, 670, 44, 36, 42, 2, 1.3f, 1.0f, maxMana: 40),

                Def(11, "战团督军",   3, new[] { SynergyId.Brute, SynergyId.Bulwark },  RoleId.Warrior,  11, 950, 65, 48, 44, 1, 1.2f, 1.0f, maxMana: 60),
                Def(12, "王家神射",   3, new[] { SynergyId.Ranger, SynergyId.Crown },   RoleId.Marksman, 12, 820, 70, 40, 41, 4, 1.3f, 1.0f, maxMana: 60),
                Def(13, "战地法师",   3, new[] { SynergyId.Arcane, SynergyId.Shadow },  RoleId.Mage,     13, 840, 55, 40, 48, 2, 1.5f, 1.0f, maxMana: 60),

                Def(14, "影刃",       4, new[] { SynergyId.Shadow, SynergyId.Ranger },  RoleId.Assassin, 14, 1030, 85, 42, 40, 1, 0.9f, 0.8f, maxMana: 70),
                Def(15, "铁甲巨兽",   4, new[] { SynergyId.Bulwark, SynergyId.Brute },  RoleId.Tank,     15, 1280, 62, 55, 52, 1, 1.6f, 1.1f, maxMana: 70),
                Def(16, "宫廷大法师", 4, new[] { SynergyId.Crown, SynergyId.Arcane },  RoleId.Mage,     16, 1060, 64, 42, 52, 3, 1.5f, 1.0f, maxMana: 70),

                Def(17, "圣殿之王",   5, new[] { SynergyId.Crown, SynergyId.Bulwark },  RoleId.Tank,     17, 1580, 72, 60, 58, 1, 1.4f, 1.0f, maxMana: 80),
                Def(18, "魔渊兽王",   5, new[] { SynergyId.Shadow, SynergyId.Brute },   RoleId.Warrior,  18, 1400, 100, 50, 46, 1, 1.1f, 0.9f, maxMana: 80),
            };

            // 速记工厂：族标签/职业/技能 + hp/atk/armor(双抗同值)/range/间隔 + 蓝条（7c-2 起全员必填）；
            // 法强基准 100、暴击出厂基准 5/50（#10/#12 口径）；星级放大只动血/攻（阶段1 规则）。
            // 回蓝两维来自职业表（决策 #22⑥）：职业是回蓝速率轴的唯一真源——
            // Default 18 棋子全员有蓝条（hasMana 恒真）；表外自建棋子仍可 MaxMana=0 = 无蓝条单位（两维挂 0）。
            static CharacterDef Def(int id, string name, int cost, SynergyId[] synergies, RoleId roleId, int skillId,
                int hp, int atk, int armor, int mr, int range, float atkIv, float moveIv, int maxMana = 0)
            {
                RoleDef role = RoleTable.Default.Get(roleId);
                if (roleId != RoleId.None && role.Id == RoleId.None)
                    throw new ArgumentException($"棋子 {id}「{name}」的职业 {roleId} 不在职业表里");

                bool hasMana = maxMana > 0;
                return new CharacterDef(id, name, cost,
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
                        ManaPerHit = hasMana ? role.ManaPerHit : 0,
                        ManaRegenPerSecond = hasMana ? role.ManaRegenPerSecond : 0,
                    },
                    synergies, roleId, skillId);
            }
        }
    }
}