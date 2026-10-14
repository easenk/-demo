namespace AutoChess.Game
{
    /// <summary>
    /// 逻辑层数学工具，代替 UnityEngine.Mathf（铁律1：不能引引擎）。
    /// 也不用 System.Math：它只有 double 重载，float→double 的隐式提升会改变
    /// 数值语义——而"同配置同结果"是回放/压测/复现的地基，运算必须自己掌握。
    /// 全部函数体只做 int/float 运算。
    /// </summary>
    public static class GameMath
    {
        public static int Max(int a, int b) => a >= b ? a : b;
        public static int Min(int a, int b) => a <= b ? a : b;
        public static float Max(float a, float b) => a >= b ? a : b;
        public static float Min(float a, float b) => a <= b ? a : b;

        /// <summary>整数绝对值。</summary>
        public static int Abs(int v) => v < 0 ? -v : v;

        public static int Clamp(int v, int min, int max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }

        public static float Clamp(float v, float min, float max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }

        /// <summary>
        /// 四舍五入远离零（D2）：0.5→1、−0.5→−1、2.5→3。
        /// 为什么不用 System.Math.Round：那是银行家舍入（0.5→0、2.5→2），
        /// 对玩家反直觉；且它走 double。
        /// </summary>
        public static int RoundToInt(float v)
        {
            // 正数：+0.5 后截断；负数：对称处理（−0.5 → −1），全程不出现 double
            return v >= 0f ? (int)(v + 0.5f) : -(int)(0.5f - v);
        }
    }
}