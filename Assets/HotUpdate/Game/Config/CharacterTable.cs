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

        private static CharacterDef[] BuildDefaultDefs()
        {
            // 基线三人组：结构占位（覆盖三档费用、近战/远程/法师三种身位），数值阶段3 平衡期再定
            return new[]
            {
                new CharacterDef(
                    id: 1, displayName: "见习剑士", cost: 1,
                    baseStats: new UnitStats { MaxHp = 50, AttackPower = 14, Armor = 30, MagicResist = 30, AbilityPower = 100, AttackRange = 1, AttackInterval = 1.0f, MoveInterval = 1.0f, CritChance = 5, CritDamage = 50 },
                    synergies: new int[0]),
                new CharacterDef(
                    id: 2, displayName: "林间弩手", cost: 2,
                    baseStats: new UnitStats { MaxHp = 45, AttackPower = 16, Armor = 20, MagicResist = 20, AbilityPower = 100, AttackRange = 3, AttackInterval = 1.2f, MoveInterval = 1.1f, CritChance = 5, CritDamage = 50 },
                    synergies: new int[0]),
                new CharacterDef(
                    id: 3, displayName: "学徒法师", cost: 3,
                    baseStats: new UnitStats { MaxHp = 40, AttackPower = 10, Armor = 20, MagicResist = 40, AbilityPower = 120, AttackRange = 2, AttackInterval = 1.5f, MoveInterval = 1.0f, CritChance = 5, CritDamage = 50 },
                    synergies: new int[0]),
            };
        }
    }
}