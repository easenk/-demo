using System;
using System.Collections.Generic;

namespace AutoChess.Game
{
    /// <summary>
    /// 棋盘：占用状态的唯一权威。8 列 × 6 行 = 48 格（决策#8，与参考工程战台一致）。
    /// "失败=纯拒绝"：红线（越界 / 非己方半场 / 格子已占用 / 状态不符）任一条不满足
    /// → 不改任何状态、不广播任何事件、返回 false。阶段2 的"拖拽失败要静默"直接复用。
    /// "规则当参数"：备战拖拽与战斗走位是同一个 TryMove 的一个 bool——棋盘保持中立。
    /// </summary>
    public sealed class BoardData
    {
        public const int Width = GameConst.BoardWidth;   // 8
        public const int Height = GameConst.BoardHeight;  // 6

        /// <summary>
        /// D1（前后分）：玩家占低 row 的 3 行（row 0~2），敌方占 row 3~5。
        /// 方向常量集中这一处——阶段4 摆场景核对后若反了，只翻这里和 IsPlayerHalf。
        /// </summary>
        public const int PlayerHalfRows = 3;

        private readonly EventBus _bus;
        private readonly Unit[] _cells = new Unit[Width * Height];

        public BoardData(EventBus bus)
        {
            _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        }

        public bool IsInside(Pos c)
        {
            return c.X >= 0 && c.X < Width && c.Y >= 0 && c.Y < Height;
        }

        /// <summary>玩家半场：row 0~2（D1）。越界坐标一律 false。</summary>
        public bool IsPlayerHalf(Pos c)
        {
            return IsInside(c) && c.Y < PlayerHalfRows;
        }

        /// <summary>空格/越界 → null（调用方不用先判界）。</summary>
        public Unit GetOccupant(Pos c)
        {
            if (!IsInside(c)) return null;
            return _cells[c.Y * Width + c.X];
        }

        /// <summary>D5：存活数靠查棋盘，不维护计数器（无隐藏状态，天然抗复活/召唤）。</summary>
        public int CountOnBoard(bool playerSide)
        {
            int count = 0;
            for (int i = 0; i < _cells.Length; i++)
            {
                Unit u = _cells[i];
                if (u != null && u.IsAlive && u.IsPlayerSide == playerSide)
                    count++;
            }
            return count;
        }

        /// <summary>只允许放进该单位的己方半场（玩家 row 0~2，敌方 row 3~5）。</summary>
        public bool TryPlace(Unit u, Pos c)
        {
            if (u == null) throw new ArgumentNullException(nameof(u));
            if (!IsInside(c)) return false;                       // 红线1：越界
            if (!IsOwnHalf(u, c)) return false;                   // 红线2：非己方半场
            if (_cells[c.Y * Width + c.X] != null) return false;   // 红线3：格子已占用
            if (u.IsOnBoard) return false;                         // 红线4：已在板上

            _cells[c.Y * Width + c.X] = u;
            u.Pos = c;
            u.IsOnBoard = true;
            _bus.Publish(new UnitPlacedEvent(u.Id, c, u.IsPlayerSide));
            return true;
        }

        /// <summary>
        /// 挪动：mustStayInOwnHalf=true（备战拖拽）只能在己方半场内挪，
        /// false（战斗走位）可越境。目标格是自己当前格 → 按"已占用"拒绝。
        /// </summary>
        public bool TryMove(Unit u, Pos to, bool mustStayInOwnHalf = true)
        {
            if (u == null) throw new ArgumentNullException(nameof(u));
            if (!u.IsOnBoard) return false;                       // 不在板上没得挪
            if (!IsInside(to)) return false;                      // 红线1：越界
            if (mustStayInOwnHalf && !IsOwnHalf(u, to)) return false; // 红线2：规则申报越境
            int toIdx = to.Y * Width + to.X;
            if (_cells[toIdx] != null) return false;              // 红线3：已占用（含原地）

            _cells[u.Pos.Y * Width + u.Pos.X] = null;
            _cells[toIdx] = u;
            Pos from = u.Pos;
            u.Pos = to;
            _bus.Publish(new UnitMovedEvent(u.Id, from, to));
            return true;
        }

        /// <summary>移出棋盘（Pos 保留最后位置；尸体清理由 CombatSystem 调用）。</summary>
        public bool Remove(Unit u)
        {
            if (u == null) throw new ArgumentNullException(nameof(u));
            if (!u.IsOnBoard) return false;

            int idx = u.Pos.Y * Width + u.Pos.X;
            if (_cells[idx] != u) return false;   // 理论不可达：占用账本只在三个出口内变更

            _cells[idx] = null;
            u.IsOnBoard = false;                  // Pos 不清零：撤离后保留最后位置（测试焊死）
            _bus.Publish(new UnitRemovedEvent(u.Id, u.Pos));
            return true;
        }

        /// <summary>
        /// M2：零分配收集。先 Clear 再按固定顺序（row 0 从左到右 → row 1 → ……）
        /// 填入指定阵营的存活单位——战斗"拍照"与索敌共用此顺序（确定性的一部分）。
        /// </summary>
        public void CollectUnitsOnBoard(bool playerSide, List<Unit> buffer)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            buffer.Clear();
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    Unit u = _cells[y * Width + x];
                    if (u != null && u.IsAlive && u.IsPlayerSide == playerSide)
                        buffer.Add(u);
                }
            }
        }

        /// <summary>敌我镜像：玩家的己方半场=row 0~2，敌方的=row 3~5。</summary>
        private bool IsOwnHalf(Unit u, Pos c)
        {
            return u.IsPlayerSide ? IsPlayerHalf(c) : IsInside(c) && c.Y >= Height - PlayerHalfRows;
        }
    }
}