using System;

namespace AutoChess.Game
{
    /// <summary>
    /// 逻辑层棋盘坐标（X=列，Y=行）。
    /// 为什么自写而不用 Vector2Int：它是 UnityEngine 类型，AutoChess.Game 的
    /// noEngineReferences 会让任何引擎引用直接编译失败——铁律1 的编译期站岗。
    /// readonly struct + IEquatable：可当字典 key，塞进事件广播零装箱。
    /// 注意：Pos 不判断"越界"——它只是个数值，合法性由 BoardData.IsInside 负责。
    /// </summary>
    public readonly struct Pos : IEquatable<Pos>
    {
        public readonly int X;
        public readonly int Y;

        public Pos(int x, int y)
        {
            X = x;
            Y = y;
        }

        /// <summary>(0,0)。</summary>
        public static Pos Zero => default;

        /// <summary>
        /// 切比雪夫距离 max(|dx|,|dy|)：方格棋盘"距离几格"的官方口径
        /// （8 方向都能一步走时，周围一圈 8 格全是"距离 1"）。
        /// </summary>
        public int ChebyshevTo(Pos other)
        {
            int dx = X - other.X;
            int dy = Y - other.Y;
            int ax = dx < 0 ? -dx : dx;
            int ay = dy < 0 ? -dy : dy;
            return ax >= ay ? ax : ay;
        }

        /// <summary>曼哈顿距离 |dx|+|dy|：备用口径（将来技能范围/寻路按 4 方向算时用）。</summary>
        public int ManhattanTo(Pos other)
        {
            int dx = X - other.X;
            int dy = Y - other.Y;
            return (dx < 0 ? -dx : dx) + (dy < 0 ? -dy : dy);
        }

        public bool Equals(Pos other) => X == other.X && Y == other.Y;

        public override bool Equals(object obj) => obj is Pos other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(X, Y);

        public override string ToString() => $"({X}, {Y})";

        public static bool operator ==(Pos a, Pos b) => a.X == b.X && a.Y == b.Y;
        public static bool operator !=(Pos a, Pos b) => a.X != b.X || a.Y != b.Y;
    }
}