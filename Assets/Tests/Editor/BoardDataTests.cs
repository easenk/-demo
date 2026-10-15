using System;
using System.Collections.Generic;
using AutoChess.Game;
using NUnit.Framework;

namespace AutoChess.Tests
{
    // 全事件计数器（本文件内使用；各测试文件自带一份，保持文件自包含）
    internal sealed class BoardEventCounter
    {
        public int Placed, Moved, Removed, Damaged, Healed, MaxHpChanged, Died, Restored;
        public void Attach(EventBus bus)
        {
            bus.Subscribe<UnitPlacedEvent>(e => Placed++);
            bus.Subscribe<UnitMovedEvent>(e => Moved++);
            bus.Subscribe<UnitRemovedEvent>(e => Removed++);
            bus.Subscribe<UnitDamagedEvent>(e => Damaged++);
            bus.Subscribe<UnitHealedEvent>(e => Healed++);
            bus.Subscribe<UnitMaxHpChangedEvent>(e => MaxHpChanged++);
            bus.Subscribe<UnitDiedEvent>(e => Died++);
            bus.Subscribe<UnitRestoredEvent>(e => Restored++);
        }
        public int Total => Placed + Moved + Removed + Damaged + Healed + MaxHpChanged + Died + Restored;
    }

    [TestFixture]
    public class BoardDataTests
    {
        private EventBus _bus;
        private BoardEventCounter _events;
        private int _nextId;

        [SetUp]
        public void SetUp()
        {
            _bus = new EventBus();
            _events = new BoardEventCounter();
            _events.Attach(_bus);
            _nextId = 1;
        }

        private Unit NewUnit(bool playerSide)
        {
            return new Unit(_nextId++, 1, playerSide, DefFactory.SampleStats, _bus);
        }

        [Test]
        public void TryPlace_Success_PlacesAndBroadcasts()
        {
            var board = new BoardData(_bus);
            var u = NewUnit(true);

            Assert.IsTrue(board.TryPlace(u, new Pos(0, 0)));
            Assert.AreSame(u, board.GetOccupant(new Pos(0, 0)));
            Assert.AreEqual(new Pos(0, 0), u.Pos);
            Assert.IsTrue(u.IsOnBoard);
            Assert.AreEqual(1, _events.Placed);

            // 事件字段逐项核对
            UnitPlacedEvent last = default;
            int received = 0;
            _bus.Subscribe<UnitPlacedEvent>(e => { last = e; received++; });
            var u2 = NewUnit(true);
            Assert.IsTrue(board.TryPlace(u2, new Pos(1, 0)));
            Assert.AreEqual(1, received);
            Assert.AreEqual(u2.Id, last.UnitId);
            Assert.AreEqual(new Pos(1, 0), last.Cell);
            Assert.IsTrue(last.IsPlayerSide);
        }

        [Test]
        public void TryPlace_Rejections_ArePurelySilent()
        {
            var board = new BoardData(_bus);

            // 越界四向
            Assert.IsFalse(board.TryPlace(NewUnit(true), new Pos(-1, 0)));
            Assert.IsFalse(board.TryPlace(NewUnit(true), new Pos(0, -1)));
            Assert.IsFalse(board.TryPlace(NewUnit(true), new Pos(8, 0)));
            Assert.IsFalse(board.TryPlace(NewUnit(true), new Pos(0, 6)));
            // 玩家单位进敌方半场
            Assert.IsFalse(board.TryPlace(NewUnit(true), new Pos(0, 3)));
            Assert.IsFalse(board.TryPlace(NewUnit(true), new Pos(7, 5)));
            // 格子已占用
            var u1 = NewUnit(true);
            Assert.IsTrue(board.TryPlace(u1, new Pos(0, 0)));
            Assert.IsFalse(board.TryPlace(NewUnit(true), new Pos(0, 0)));
            // 已在板上再放
            Assert.IsFalse(board.TryPlace(u1, new Pos(1, 0)));

            // 纯拒绝：全程只应广播过 1 次（u1 的成功放置）
            Assert.AreEqual(1, _events.Total);
            Assert.AreEqual(1, board.CountOnBoard(true)); // 只有 u1 上板成功；被拒的那些一个都没上板
        }

        [Test]
        public void TryPlace_EnemyUnit_UsesEnemyHalf()
        {
            var board = new BoardData(_bus);

            var enemy = NewUnit(false);
            Assert.IsTrue(board.TryPlace(enemy, new Pos(0, 5)));  // 敌方半场 = row 3~5（镜像）
            Assert.IsFalse(board.TryPlace(NewUnit(false), new Pos(0, 0))); // 敌方不许进玩家半场
            Assert.AreEqual(1, board.CountOnBoard(false));
            Assert.AreEqual(1, _events.Placed);
        }

        [Test]
        public void TryMove_Success_BroadcastsAndFreesOldCell()
        {
            var board = new BoardData(_bus);
            var u = NewUnit(true);
            Assert.IsTrue(board.TryPlace(u, new Pos(0, 0)));

            Assert.IsTrue(board.TryMove(u, new Pos(1, 0)));
            Assert.IsNull(board.GetOccupant(new Pos(0, 0)));   // 旧格让出
            Assert.AreSame(u, board.GetOccupant(new Pos(1, 0)));
            Assert.AreEqual(new Pos(1, 0), u.Pos);
            Assert.AreEqual(1, _events.Moved);
        }

        [Test]
        public void TryMove_OwnHalfRule_IsAParameter()
        {
            var board = new BoardData(_bus);
            var player = NewUnit(true);
            Assert.IsTrue(board.TryPlace(player, new Pos(0, 2)));

            // 备战规则（默认）：不许越出自己的半场
            Assert.IsFalse(board.TryMove(player, new Pos(0, 3), mustStayInOwnHalf: true));
            Assert.AreEqual(0, _events.Moved); // 失败不广播

            // 战斗走位（false）：可以越境
            Assert.IsTrue(board.TryMove(player, new Pos(0, 3), mustStayInOwnHalf: false));
            Assert.AreEqual(1, _events.Moved);

            // 敌方同理：镜像的己方半场
            var enemy = NewUnit(false);
            Assert.IsTrue(board.TryPlace(enemy, new Pos(7, 5)));
            Assert.IsFalse(board.TryMove(enemy, new Pos(7, 2)));       // 玩家半场，默认规则拒绝
            Assert.IsTrue(board.TryMove(enemy, new Pos(6, 5), mustStayInOwnHalf: true)); // 自己半场内自由挪
        }

        [Test]
        public void TryMove_Rejections_ArePurelySilent()
        {
            var board = new BoardData(_bus);
            var u1 = NewUnit(true);
            Assert.IsTrue(board.TryPlace(u1, new Pos(0, 0)));

            Assert.IsFalse(board.TryMove(u1, new Pos(8, 0)));    // 越界
            Assert.IsFalse(board.TryMove(NewUnit(true), new Pos(2, 0))); // 不在板上的单位没得挪

            var u2 = NewUnit(true);
            Assert.IsTrue(board.TryPlace(u2, new Pos(1, 0)));
            Assert.IsFalse(board.TryMove(u1, new Pos(1, 0)));    // 目标格被 u2 占用

            Assert.AreEqual(0, _events.Moved); // 全部纯拒绝
        }

        [Test]
        public void TryMove_SameCell_IsRejectedAsOccupied()
        {
            var board = new BoardData(_bus);
            var u = NewUnit(true);
            Assert.IsTrue(board.TryPlace(u, new Pos(2, 1)));

            Assert.IsFalse(board.TryMove(u, new Pos(2, 1))); // 自己占着的格子 = "已占用"
            Assert.AreEqual(0, _events.Moved);
        }

        [Test]
        public void Remove_Success_FreesCell_KeepsLastPos()
        {
            var board = new BoardData(_bus);
            var u = NewUnit(true);
            Assert.IsTrue(board.TryPlace(u, new Pos(3, 2)));

            Assert.IsTrue(board.Remove(u));
            Assert.IsNull(board.GetOccupant(new Pos(3, 2)));
            Assert.IsFalse(u.IsOnBoard);
            Assert.AreEqual(new Pos(3, 2), u.Pos); // 撤离后 Pos 保留最后位置
            Assert.AreEqual(1, _events.Removed);

            Assert.IsFalse(board.Remove(u)); // 二次移除：纯拒绝
            Assert.AreEqual(1, _events.Removed);
        }

        [Test]
        public void Remove_OffBoardUnit_IsSilentFalse()
        {
            var board = new BoardData(_bus);
            var u = NewUnit(true); // 从未上板
            Assert.IsFalse(board.Remove(u));
            Assert.AreEqual(0, _events.Total);
        }

        [Test]
        public void GetOccupant_OutOfBounds_ReturnsNullSafely()
        {
            var board = new BoardData(_bus);
            Assert.IsNull(board.GetOccupant(new Pos(-1, -1)));
            Assert.IsNull(board.GetOccupant(new Pos(8, 6)));
            Assert.IsNull(board.GetOccupant(new Pos(0, 0))); // 空格也是 null
        }

        [Test]
        public void CountOnBoard_CountsAlivePerSide()
        {
            var board = new BoardData(_bus);
            var p1 = NewUnit(true);
            var p2 = NewUnit(true);
            var e1 = NewUnit(false);
            Assert.IsTrue(board.TryPlace(p1, new Pos(0, 0)));
            Assert.IsTrue(board.TryPlace(p2, new Pos(1, 1)));
            Assert.IsTrue(board.TryPlace(e1, new Pos(0, 5)));

            Assert.AreEqual(2, board.CountOnBoard(true));
            Assert.AreEqual(1, board.CountOnBoard(false));

            p2.TakeDamage(9999, null); // 击杀但不移除（模拟结算中途）
            Assert.AreEqual(1, board.CountOnBoard(true)); // 存活数只数活的

            Assert.IsTrue(board.Remove(p2)); // 清尸后
            Assert.AreEqual(1, board.CountOnBoard(true));
        }

        [Test]
        public void IsPlayerHalf_Rows0To2Only()
        {
            var board = new BoardData(_bus);
            Assert.IsTrue(board.IsPlayerHalf(new Pos(0, 0)));
            Assert.IsTrue(board.IsPlayerHalf(new Pos(7, 2)));
            Assert.IsFalse(board.IsPlayerHalf(new Pos(0, 3))); // 敌方半场从 row 3 开始
            Assert.IsFalse(board.IsPlayerHalf(new Pos(7, 5)));
            Assert.IsFalse(board.IsPlayerHalf(new Pos(-1, 1))); // 越界一律 false
            Assert.IsFalse(board.IsPlayerHalf(new Pos(8, 1)));
        }

        [Test]
        public void CollectUnitsOnBoard_RowMajorOrder_SideFilter_BufferReuse()
        {
            var board = new BoardData(_bus);
            var pLow = NewUnit(true);   // 放 (0,0)——row 0
            var pMid = NewUnit(true);   // 放 (3,1)——row 1
            var pTop = NewUnit(true);   // 放 (7,2)——row 2
            var e1 = NewUnit(false);    // 放 (0,5)
            Assert.IsTrue(board.TryPlace(pMid, new Pos(3, 1)));
            Assert.IsTrue(board.TryPlace(pLow, new Pos(0, 0)));
            Assert.IsTrue(board.TryPlace(pTop, new Pos(7, 2)));
            Assert.IsTrue(board.TryPlace(e1, new Pos(0, 5)));

            var buffer = new List<Unit>();
            board.CollectUnitsOnBoard(true, buffer);

            // 固定顺序：row 0 从左到右 → row 1 → ……（确定性的一部分）
            Assert.AreEqual(3, buffer.Count);
            Assert.AreSame(pLow, buffer[0]);
            Assert.AreSame(pMid, buffer[1]);
            Assert.AreSame(pTop, buffer[2]);

            board.CollectUnitsOnBoard(true, buffer); // 再次收集=清空重填，不追加
            Assert.AreEqual(3, buffer.Count);

            board.CollectUnitsOnBoard(false, buffer); // 换阵营：只剩敌方
            Assert.AreEqual(1, buffer.Count);
            Assert.AreSame(e1, buffer[0]);
        }

        [Test]
        public void NullUnit_ThrowsFailFast()
        {
            var board = new BoardData(_bus);
            Assert.Throws<ArgumentNullException>(() => board.TryPlace(null, new Pos(0, 0)));
            Assert.Throws<ArgumentNullException>(() => board.TryMove(null, new Pos(0, 0)));
            Assert.Throws<ArgumentNullException>(() => board.Remove(null));
            Assert.AreEqual(0, _events.Total);
        }
    }
}