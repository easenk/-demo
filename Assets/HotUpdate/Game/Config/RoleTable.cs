using System;
using System.Collections.Generic;

namespace AutoChess.Game
{
    /// <summary>
    /// 职业查表：RoleId → RoleDef。
    /// 为什么是"实例 + 可注入"而不是静态类：正式内容由阶段4 从 Res/Config 读取后
    /// 注入新实例；Default 是代码内置基线，测试与未注入配置时兜底（同 CharacterTable 纪律）。
    ///
    /// 构造即校验（fail fast，同 OreTable/EnemyTable/PolicyTable 纪律）：
    /// id 不能为 None、id 唯一、回蓝两维非负。表里出现负回蓝 = 数据事故，当场炸比运行期静默便宜。
    /// </summary>
    public sealed class RoleTable
    {
        /// <summary>代码内置基线：五职业 v1（数值见 RoleDef 注释）。正式数值阶段3 平衡期随千场压测重定。</summary>
        public static RoleTable Default { get; } = BuildDefault();

        private readonly Dictionary<RoleId, RoleDef> _byId = new Dictionary<RoleId, RoleDef>();
        private readonly List<RoleDef> _all = new List<RoleDef>();

        public RoleTable(IReadOnlyList<RoleDef> defs)
        {
            if (defs == null) throw new ArgumentNullException(nameof(defs));
            foreach (var def in defs) Register(def);
        }

        /// <summary>全部职业定义（保持注册顺序；表现层做职业图鉴/过滤用）。</summary>
        public IReadOnlyList<RoleDef> All => _all;

        /// <summary>按职业查定义；查无（含 None）返回 default（Id=None）。调用方先查 Id 再用。</summary>
        public RoleDef Get(RoleId id) => _byId.TryGetValue(id, out var def) ? def : default;

        public bool TryGet(RoleId id, out RoleDef def)
        {
            if (_byId.TryGetValue(id, out def)) return true;
            def = default;
            return false;
        }

        private void Register(RoleDef def)
        {
            if (def.Id == RoleId.None)
                throw new ArgumentException("职业 id 不能为 None——None 是哨兵不是一条职业");
            if (_byId.ContainsKey(def.Id))
                throw new ArgumentException($"职业 id 重复：{def.Id}");
            if (def.ManaPerHit < 0)
                throw new ArgumentException($"职业 {def.Id}「{def.DisplayName}」命中回蓝不能为负（当前 {def.ManaPerHit}）");
            if (def.ManaRegenPerSecond < 0)
                throw new ArgumentException($"职业 {def.Id}「{def.DisplayName}」每秒回蓝不能为负（当前 {def.ManaRegenPerSecond}）");
            _byId[def.Id] = def;
            _all.Add(def);
        }

        private static RoleTable BuildDefault()
        {
            return new RoleTable(new List<RoleDef>
            {
                new RoleDef(RoleId.Tank,     "坦", 5,  0),
                new RoleDef(RoleId.Warrior,  "战", 10, 0),
                new RoleDef(RoleId.Assassin, "刺", 10, 0),
                new RoleDef(RoleId.Marksman, "射", 10, 0),
                new RoleDef(RoleId.Mage,     "术", 7,  2),
            });
        }
    }
}
