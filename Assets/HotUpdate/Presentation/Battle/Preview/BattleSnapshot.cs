using System;
using System.Collections.Generic;

namespace AutoChess.Presentation
{
    /// <summary>
    /// 快照条目（#23②，批次A）：一个单位的表现相关状态——单位 id/格/血/蓝/存活/阵营。
    /// 手写 Equals：逐条比对断言走它（避免 ValueType 反射比较的慢路径）。
    /// </summary>
    public struct SnapshotUnit : IEquatable<SnapshotUnit>
    {
        public int UnitId;
        public byte X;
        public byte Y;
        public int Hp;
        public int Mana;
        /// <summary>恒 true（扫描口径只收存活在场者，见 BattleSnapshot 注释）——
        /// 字段保留是对齐 #23② 字段清单，消费侧不用判它。</summary>
        public bool Alive;
        /// <summary>4B2 调试视图按阵营着色用；规划器不消费。</summary>
        public bool IsPlayerSide;

        public SnapshotUnit(int unitId, byte x, byte y, int hp, int mana, bool alive, bool isPlayerSide)
        {
            UnitId = unitId;
            X = x;
            Y = y;
            Hp = hp;
            Mana = mana;
            Alive = alive;
            IsPlayerSide = isPlayerSide;
        }

        public bool Equals(SnapshotUnit other) =>
            UnitId == other.UnitId && X == other.X && Y == other.Y
            && Hp == other.Hp && Mana == other.Mana
            && Alive == other.Alive && IsPlayerSide == other.IsPlayerSide;
    }

    /// <summary>
    /// 单帧快照：Tick + 该 tick 末棋盘上全部存活单位（先玩家后敌方、各自行主序——确定性序）。
    /// 扫描口径 = 只含存活在场者：死者由事件流表达（Died/Removed），"缺席即可判死"——
    /// 快照不收尸体（含 DoT 尸——逻辑层"DoT 致死不清格"的现状如实镜像，见批次A 交付报告挂账）。
    /// 帧内容 = 该 tick 末状态；tick 0 = 开战时刻（首帧）。
    /// </summary>
    public struct BattleSnapshot
    {
        public int Tick;
        public SnapshotUnit[] Units;

        public BattleSnapshot(int tick, SnapshotUnit[] units)
        {
            Tick = tick;
            Units = units;
        }
    }

    /// <summary>
    /// 快照序列（#23②）：关键帧 = 开战时刻(tick 0) + 每 30 tick（1 模拟秒）+ 死亡 tick + 终局帧，
    /// 同 tick 至多一帧（预演器侧去重）。
    /// 每帧按实际人数开小数组——帧频率低（约每秒 1 帧）、量小，不与事件条目的预分配纪律同级。
    /// Clear = "播完即释放"（事件流不落盘，长期留存仅战前快照——那是 #19 存档结构，阶段5 的事）。
    /// </summary>
    public sealed class BattleSnapshotSequence
    {
        private readonly List<BattleSnapshot> _frames = new List<BattleSnapshot>(200);

        public int Count => _frames.Count;

        public BattleSnapshot this[int index] => _frames[index];

        internal void Add(BattleSnapshot frame)
        {
            _frames.Add(frame);
        }

        public void Clear()
        {
            _frames.Clear();
        }
    }
}
