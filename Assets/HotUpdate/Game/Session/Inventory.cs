using System.Collections.Generic;

namespace AutoChess.Game
{
    /// <summary>
    /// 消耗品 id 常量（开放内容表 → int + 常量类，P3-5 划线原则：内容随时可加，不封 enum）。
    /// 目前只有拆卸器一种；将来加新消耗品 = 加常量 + 编表，不动类型。
    /// </summary>
    public static class ConsumableIds
    {
        /// <summary>拆卸器：卸下一件装备消耗一个（#12⑦ 消耗品；掉落来源 = 晶矿/策略，批次6 接入）。</summary>
        public const int UnsmithTool = 1;
    }

    /// <summary>
    /// 玩家背包：未穿戴的装备实例 + 消耗品计数。
    /// 穿戴中的装备在 Unit 的装备槽里，不在背包（引用唯一归属——同一实例任何时刻只在一处）。
    ///
    /// 写操作全部 internal：只允许 GameSession（同程序集）经门禁方法驱动——
    /// 测试与表现层只读（Items / EquipmentCount / ConsumableCount / Find），与"写操作经 Session 门禁"的既有纪律一致。
    /// 背包容量上限 25（2026-11-19 用户拍板"背包上限定死"——门禁语义见 Capacity 注释）。
    /// </summary>
    public sealed class Inventory
    {
        /// <summary>背包容量上限（2026-11-19 用户拍板"背包上限定死"=25，与 FGUI 备战界面 eqView 的 25 格对齐）。
        /// 门禁只拦"新资产入口"：发放经 GameSession.AddEquipment 满包纯拒绝、拆卸回流经 Unequip 满包先拒；
        /// 卖出返还/合成退料不设门禁——已存在的资产不能凭空消失，满包回流可短暂超 25
        /// （实战数值不可达：14 回合装备收入约 3~4 件；UI 25 格外的条目不显示）。
        /// 消耗品（拆卸器等）不占格——它们走 Disassembler 专用按钮位（2026-11-19 拍板），不进 25 格。</summary>
        public const int Capacity = 25;

        private readonly List<EquipmentInstance> _items = new List<EquipmentInstance>();
        private readonly Dictionary<int, int> _consumables = new Dictionary<int, int>();

        /// <summary>背包内的装备实例（未穿戴；顺序 = 入包顺序，穿戴/卖返还/合成退料都从尾部追加）。</summary>
        public IReadOnlyList<EquipmentInstance> Items => _items;

        /// <summary>背包内装备件数。</summary>
        public int EquipmentCount => _items.Count;

        /// <summary>某消耗品的持有数（没有 = 0）。</summary>
        public int ConsumableCount(int consumableId)
            => _consumables.TryGetValue(consumableId, out int n) ? n : 0;

        // ── 装备实例进出（internal：经 GameSession 门禁） ──

        internal void Add(EquipmentInstance item)
        {
            if (item != null) _items.Add(item);
        }

        /// <summary>按实例号查背包内装备（只读查询：表现层按 InstanceId 找背包条目用）；
        /// 不在背包（未知 id / 已穿在身上）返回 null。</summary>
        public EquipmentInstance Find(int instanceId)
        {
            for (int i = 0; i < _items.Count; i++)
                if (_items[i].InstanceId == instanceId) return _items[i];
            return null;
        }

        /// <summary>按实例号移除（穿戴时调用）；不在背包返回 false。</summary>
        internal bool Remove(int instanceId)
        {
            for (int i = 0; i < _items.Count; i++)
            {
                if (_items[i].InstanceId != instanceId) continue;
                _items.RemoveAt(i);
                return true;
            }
            return false;
        }

        // ── 消耗品（internal：经 GameSession 门禁） ──

        internal void AddConsumable(int consumableId, int count)
        {
            if (count <= 0) return;
            _consumables.TryGetValue(consumableId, out int n);
            _consumables[consumableId] = n + count;
        }

        /// <summary>消耗：数量不足 = 纯拒绝（false，一分不动）——与 Economy.TrySpend 同款纪律。</summary>
        internal bool TryConsume(int consumableId, int count)
        {
            if (count <= 0) return false;
            if (!_consumables.TryGetValue(consumableId, out int n) || n < count) return false;
            _consumables[consumableId] = n - count;
            return true;
        }
    }
}
