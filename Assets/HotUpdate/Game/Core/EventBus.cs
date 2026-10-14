using System;
using System.Collections.Generic;

namespace AutoChess.Game
{
    /// <summary>
    /// 类型化事件总线：逻辑层只广播，表现层只订阅（单向数据流的中枢）。
    /// 为什么 sealed + 非 static：每局/每个测试 new 一个自己的实例，互不污染；
    /// 谁持有总线一目了然（不玩全局单例）。
    /// 事件必须是 struct（where T : struct）：广播零装箱，约束由编译器强制。
    /// 订阅者在广播中抛异常会直接炸出去（D4：不包 try/catch，fail fast）。
    /// </summary>
    public sealed class EventBus
    {
        // Type → 订阅者名单。不同 T 的 Action<T> 是不同类型，只能装成 object 统一存
        private readonly Dictionary<Type, List<object>> _handlers = new Dictionary<Type, List<object>>();

        public void Subscribe<T>(Action<T> handler) where T : struct
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            if (!_handlers.TryGetValue(typeof(T), out var list))
            {
                list = new List<object>();
                _handlers.Add(typeof(T), list);
            }
            list.Add(handler);
        }

        /// <summary>按值删第一条匹配的订阅（委托相等 = 方法+目标一致）。没登记过就静默返回。</summary>
        public void Unsubscribe<T>(Action<T> handler) where T : struct
        {
            if (handler == null) return;
            if (_handlers.TryGetValue(typeof(T), out var list))
                list.Remove(handler);
        }

        /// <summary>
        /// 广播：无订阅者时合法且静默。
        /// 先对订阅名单拍快照再遍历——广播进行中的新增订阅本轮不生效，
        /// 广播中的退订本轮仍会被叫到（快照语义，M1 暂留 ToArray）。
        /// </summary>
        public void Publish<T>(T evt) where T : struct
        {
            if (!_handlers.TryGetValue(typeof(T), out var list) || list.Count == 0)
                return;

            object[] snapshot = list.ToArray();
            for (int i = 0; i < snapshot.Length; i++)
                ((Action<T>)snapshot[i]).Invoke(evt);
        }

        /// <summary>清空全部订阅（一场战斗结束/一个测试收尾时用）。</summary>
        public void Clear()
        {
            _handlers.Clear();
        }
    }
}