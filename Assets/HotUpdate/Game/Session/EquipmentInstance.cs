namespace AutoChess.Game
{
    /// <summary>
    /// 装备实例：背包与装备槽共用的运行时实体。class（引用语义）——升级要原地改写 DefId。
    ///
    /// 归属纪律（引用唯一归属）：任何时刻一个实例只在一个容器——
    /// 背包 Inventory 的列表里，或某单位的某装备槽里；移动权在 GameSession（门禁方法），
    /// 测试与表现层只读（InstanceId / DefId / 所在槽）。
    /// InstanceId 全局唯一：GameSession 的 IdAllocator 发号（与 Unit / OreDrop 同源，永不撞车）。
    /// </summary>
    public sealed class EquipmentInstance
    {
        /// <summary>实例号（全局唯一；升级/拆卸/卖返还都按它指代"这一件"）。</summary>
        public int InstanceId { get; }

        /// <summary>当前定义 id（升级时原地改写：散件 id → 大件 id；DefId 指向大件 = 已定型）。
        /// internal set：只允许同程序集的 GameSession 升级路径改写。</summary>
        public int DefId { get; internal set; }

        public EquipmentInstance(int instanceId, int defId)
        {
            InstanceId = instanceId;
            DefId = defId;
        }
    }
}
