using System;
using AutoChess.Game;
using NUnit.Framework;

namespace AutoChess.Tests
{
    // 测试专用事件类型（不引用生产事件，隔离干净；A/B 两个类型用来验证"发 A 不吵 B"）
    internal struct TestEventA { public int Value; public TestEventA(int value) { Value = value; } }
    internal struct TestEventB { public int Value; public TestEventB(int value) { Value = value; } }

    [TestFixture]
    public class EventBusTests
    {
        [Test]
        public void Subscribe_Publish_HandlerReceivesEvent()
        {
            var bus = new EventBus();
            int received = 0;
            bus.Subscribe<TestEventA>(e => received = e.Value);
            bus.Publish(new TestEventA(42));
            Assert.AreEqual(42, received);
        }

        [Test]
        public void MultipleSubscribers_AllReceive()
        {
            var bus = new EventBus();
            int count = 0;
            int sum = 0;
            bus.Subscribe<TestEventA>(e => count++);
            bus.Subscribe<TestEventA>(e => sum += e.Value);
            bus.Publish(new TestEventA(10));
            Assert.AreEqual(1, count);
            Assert.AreEqual(10, sum);
        }

        [Test]
        public void SameHandlerSubscribedTwice_InvokedTwice()
        {
            var bus = new EventBus();
            int count = 0;
            Action<TestEventA> handler = e => count++;
            bus.Subscribe(handler);
            bus.Subscribe(handler);
            bus.Publish(default(TestEventA));
            Assert.AreEqual(2, count); // 订阅是"登记"：同一人登记两次就要被叫两次

            bus.Unsubscribe(handler); // 按值删一条
            bus.Publish(default(TestEventA));
            Assert.AreEqual(3, count); // 名单里还剩一条登记

            bus.Unsubscribe(handler);
            bus.Publish(default(TestEventA));
            Assert.AreEqual(3, count); // 名单清空，不再被叫
        }

        [Test]
        public void Unsubscribed_HandlerNoLongerInvoked()
        {
            var bus = new EventBus();
            int count = 0;
            Action<TestEventA> handler = e => count++;
            bus.Subscribe(handler);
            bus.Unsubscribe(handler);
            bus.Publish(default(TestEventA));
            Assert.AreEqual(0, count);
        }

        [Test]
        public void Publish_NoSubscribers_SilentAndSafe()
        {
            var bus = new EventBus();
            Assert.DoesNotThrow(() => bus.Publish(new TestEventA(1))); // 无订阅者广播 = 合法且静默
        }

        [Test]
        public void Publish_DuringSubscribe_NewHandlerWaitsForNextPublish()
        {
            var bus = new EventBus();
            int count = 0;
            bool subscribed = false;
            bus.Subscribe<TestEventA>(e =>
            {
                if (!subscribed)
                {
                    subscribed = true;
                    bus.Subscribe<TestEventA>(e2 => count++); // 广播进行中新增订阅
                }
            });
            bus.Publish(default(TestEventA));
            Assert.AreEqual(0, count); // 快照遍历：本轮名单已定，新订阅者本轮不被叫
            bus.Publish(default(TestEventA));
            Assert.AreEqual(1, count); // 下一轮生效
        }

        [Test]
        public void Publish_DuringSelfUnsubscribe_SurvivesCurrentPublishOnly()
        {
            var bus = new EventBus();
            int received = 0;
            Action<TestEventA> handler = null;
            handler = e =>
            {
                received++;
                bus.Unsubscribe(handler); // 广播进行中把自己退订：不许炸
            };
            bus.Subscribe(handler);
            bus.Publish(default(TestEventA));
            Assert.AreEqual(1, received); // 本轮快照里有它 → 本轮仍被叫
            bus.Publish(default(TestEventA));
            Assert.AreEqual(1, received); // 之后不再被叫
        }

        [Test]
        public void EventTypes_AreIsolated()
        {
            var bus = new EventBus();
            int aCount = 0, bCount = 0;
            bus.Subscribe<TestEventA>(e => aCount++);
            bus.Subscribe<TestEventB>(e => bCount++);
            bus.Publish(new TestEventA(1));
            Assert.AreEqual(1, aCount);
            Assert.AreEqual(0, bCount); // 发 A 不许吵到 B 的订阅者
            bus.Publish(new TestEventB(1));
            Assert.AreEqual(1, aCount);
            Assert.AreEqual(1, bCount);
        }

        [Test]
        public void Clear_RemovesAllSubscriptions()
        {
            var bus = new EventBus();
            int count = 0;
            bus.Subscribe<TestEventA>(e => count++);
            bus.Subscribe<TestEventB>(e => count++);
            bus.Clear();
            bus.Publish(new TestEventA(1));
            bus.Publish(new TestEventB(1));
            Assert.AreEqual(0, count);
        }
    }
}