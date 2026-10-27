namespace AutoChess.Game
{
    /// <summary>
    /// 场景掉落物实体（决策 #17⑤）：记录"哪颗矿、几挡、落在死亡格"。
    /// 为什么不占棋盘格：晶矿是场景件不是棋子——不参与站位/索敌/占用账本，
    /// BoardData 根本不知道它存在（同格多矿天然合法，散开摆放是表现层的事，D2-10）。
    /// Id 由 IdAllocator 发（与 Unit 共用号池：事件里各走各的字段名，不混淆）；
    /// 列表只增不减，唯一出口是 GameSession.PickOre（战败已掉的矿照样留，#17⑧）。
    /// </summary>
    public readonly struct OreDrop
    {
        /// <summary>掉落物实例 id（IdAllocator 发）——区别于 OreDef 的定义 id。</summary>
        public readonly int Id;

        /// <summary>挡位 1~3（编成表按怪写死，#17③）。</summary>
        public readonly int Tier;

        /// <summary>死亡格：表现层拿它摆矿体位置。</summary>
        public readonly Pos Cell;

        public OreDrop(int id, int tier, Pos cell)
        {
            Id = id;
            Tier = tier;
            Cell = cell;
        }
    }
}
