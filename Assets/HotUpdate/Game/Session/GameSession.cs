using System;
using System.Collections.Generic;

namespace AutoChess.Game
{
    /// <summary>一局游戏的流程相位——状态机的全部状态。非法转移一律纯拒绝（返回 false、四不动）。</summary>
    public enum GamePhase
    {
        /// <summary>构造后、StartRun 前：一切玩法操作拒绝。</summary>
        PreRun,
        /// <summary>备战期：买/卖/刷/买经验/上下场/摆位/捡矿开放（批次6 已加捡矿；批次7 再加选策略）。</summary>
        Prep,
        /// <summary>战斗中：全部备战期操作拒绝；结算等表现层排空闸门回调（#23）。</summary>
        Battle,
        /// <summary>战斗已分晓、结算已入账，等 StartPrep 进下一回合（终局时被 RunOver 直接跳过）。</summary>
        Settlement,
        /// <summary>终局：血尽或赢下 R14；一切推进与操作拒绝。</summary>
        RunOver,
    }

    /// <summary>终局评级（9.3：胜 ≥50 血 S / ≥25 A / 其余 B / 未通关 C，纯函数 RateRun 判定）。</summary>
    public enum RunRating
    {
        /// <summary>未通关（血尽，含 Boss 败清零）。</summary>
        C,
        /// <summary>通关但血量 &lt; 25。</summary>
        B,
        /// <summary>通关且血量 25~49。</summary>
        A,
        /// <summary>通关且血量 ≥ 50。</summary>
        S,
    }

    /// <summary>
    /// GameSession：一局自走棋的组合根（回合状态机 + 全系统接线）。
    /// 名册分三份：Bench（备战席）/ PlayerBoard（我方战场人员）/ EnemyRoster（敌方名册）。
    /// BoardData 仍是格子占用的唯一权威；三份名单只负责成员归属，不另立占位规则。
    ///
    /// 状态机（9.2）：
    ///   PreRun --StartRun--> Prep(R1) --StartBattle--> Battle --SettleBattle--> Settlement --StartPrep--> Prep(R+1)
    ///   Settle 终局判定（血尽 / 赢下 R14）→ RunOver（终态）
    ///
    /// #23（2026-10-19 决策，批次5 落实）：结算触发点不是"订阅 BattleEndedEvent"——
    /// 预演器一口气跑完整场、结果压住不放，结算/进备战不许早于画面见玩家。
    /// SettleBattle() 就是那个"排空闸门回调"：阶段4 表现层排空后调它；批次5 由测试/驱动器调。
    /// 战斗结果不另记账——结算瞬间直接读 Combat.PlayerWon / TickCount（查棋盘现算，无隐藏状态）。
    ///
    /// 拒绝矩阵纪律：每个公开方法先枚举全部拒绝条件再动手；失败 = 纯拒绝
    /// （状态不动、钱不扣、池不动、事件不发）。驱动与实现分离的"失败必须沉默"在这里兑现。
    /// </summary>
    public sealed class GameSession
    {
        // ── 常量（9.2；数值锚点全部引用各系统的单一真源）──

        /// <summary>回合总数（#20：14 回合闭环；R15 隐藏 Boss 仅设计预留不进排期）。</summary>
        public const int TotalRounds = 14;

        /// <summary>开局金币 = 3（#21④；单一真源在 Economy）。</summary>
        public const int StartGold = Economy.DefaultStartGold;

        /// <summary>开局血量 = 100（也是血量上限：Boss 限时回血封顶 100）。</summary>
        public const int StartHp = 100;

        /// <summary>备战席容量（D2-8，2026-10-27 拍板 9，金铲铲口径；满员买入纯拒绝、先于扣钱）。</summary>
        public const int BenchCapacity = 9;

        /// <summary>Boss 限时击杀线（#21⑨：90 秒 × 30 步 = 2700 步，超时获胜只回 10 血）。</summary>
        public const int BossTimeLimitTicks = 90 * GameConst.TicksPerSecond;

        /// <summary>Boss 限时击杀的回血量（#21⑨）。</summary>
        public const int BossHealFast = 35;
        public const int BossHealSlow = 10;

        private readonly EventBus _bus;
        private readonly GameRandom _random;      // 会话级随机源（商店/战斗种子/晶矿/策略共用一条流）
        private readonly GameTables _tables;
        private readonly BoardData _board;
        private readonly Economy _eco;
        private readonly ShopPool _pool;
        private readonly ShopSystem _shop;
        private readonly SynergySystem _synergy;
        private readonly IdAllocator _ids;

        // ── 三份名册（2026-10-27 拍板：不再混存双方单位）──
        private readonly List<Unit> _bench = new List<Unit>();         // 备战席：玩家侧且不在场
        private readonly List<Unit> _playerBoard = new List<Unit>();   // 我方战场人员
        private readonly List<Unit> _enemyRoster = new List<Unit>();   // 敌方名册：每场生成、EnterPrep 整体退场

        // 羁绊重算的临时输入（PlayerBoard + Bench）：只用于"按源摘账"，不是第四份名册；
        // SynergySystem 挂账时只取棋盘上的玩家单位（备战席不享受，2026-10-27 拍板）。
        private readonly List<Unit> _synergyInput = new List<Unit>();

        private List<Unit> _lastBattleUnits;                       // 上一场参战者快照（EnterPrep 只复活/恢复他们）
        private CombatSystem _combat;                             // 每场一个（StartBattle 重建；Settlement 后仍可读，供战绩/断言）

        private int _eliteClears;                                  // 已通关精英关数（#21① 段位：0~2）
        private bool _policyDue;                                   // 奖励关胜利记账（批次7 策略弹选消费）

        // ── 晶矿（批次6）：掉落记账 + 场上掉落物 ──
        private readonly Dictionary<int, int> _oreTierByUnitId = new Dictionary<int, int>(); // 本场记账：敌方 unitId → 挡位（只记 OreTier>0；每场 SpawnEncounter 重建）
        private readonly List<OreDrop> _oresOnGround = new List<OreDrop>();   // 场上掉落物：只增不减，唯一出口 PickOre（战败已掉的矿照样留，#17⑧）

        // ── 状态（private set：只有本类能改）──

        public GamePhase Phase { get; private set; }
        public int Round { get; private set; }
        public int PlayerHp { get; private set; } = StartHp;

        /// <summary>终局评级（null = 未终局；Settle 终局判定时写入，RunOver 的副产物）。</summary>
        public RunRating? FinalRating { get; private set; }

        /// <summary>本局是否已终局。</summary>
        public bool RunOver => Phase == GamePhase.RunOver;

        /// <summary>最近一场战斗的种子（D2-6：会话随机开战瞬间抽 [1, int.MaxValue)，每场一个）。
        /// 阶段5 快照重演存"战前快照 + 原种子"——这个种子就是那把钥匙的出口。</summary>
        public uint LastBattleSeed { get; private set; }

        // ── 只读透传（表现层拿系统句柄做只读访问；写操作全部经本类的门禁方法）──

        public BoardData Board => _board;
        public Economy Eco => _eco;
        public ShopSystem Shop => _shop;
        public IReadOnlyList<Unit> Bench => _bench;
        public IReadOnlyList<Unit> PlayerBoard => _playerBoard;
        public IReadOnlyList<Unit> EnemyRoster => _enemyRoster;
        public CombatSystem Combat => _combat;                                  // 未开过战为 null
        public IReadOnlyList<SynergyInfo> Synergies => _synergy.LastSnapshot;   // 未重算过为 null
        public IReadOnlyList<OreDrop> OresOnGround => _oresOnGround;           // 批次6：场上晶矿（StartBattle 门禁源；表现层做"捡完才能开战"引导与点击仲裁）

        public GameSession(EventBus bus, uint seed = 0u, GameTables tables = null)
        {
            _bus = bus ?? throw new ArgumentNullException(nameof(bus));
            _random = new GameRandom(seed);
            _tables = tables ?? GameTables.Default;
            _board = new BoardData(bus);
            _eco = new Economy(bus, StartGold);
            _pool = new ShopPool(_random, _tables.Characters, _tables.Odds);
            _shop = new ShopSystem(_pool, _eco, bus);
            _synergy = new SynergySystem(_tables.Characters, _tables.Synergies, bus);
            _ids = new IdAllocator();
            _bus.Subscribe<UnitDiedEvent>(OnUnitDied);   // 晶矿掉落记账（批次6）：纯事件驱动——CombatSystem 不知道晶矿存在
            Phase = GamePhase.PreRun;
        }

        // ─────────────────────── 回合推进（非法转移一律纯拒绝） ───────────────────────

        /// <summary>开局：PreRun → R1 备战（走 EnterPrep 六步；第 1~3 步对 R1 是天然空操作）。</summary>
        public bool StartRun()
        {
            if (Phase != GamePhase.PreRun) return false;
            Round = 1;
            EnterPrep();
            return true;
        }

        /// <summary>进下一回合备战：Settlement → Prep(Round+1)。R14 打完已 RunOver，到不了这里。</summary>
        public bool StartPrep()
        {
            if (Phase != GamePhase.Settlement) return false;
            if (Round >= TotalRounds) return false;   // 理论不可达（R14 的 Settle 必判 RunOver），防御性双保险
            Round++;
            EnterPrep();
            return true;
        }

        /// <summary>
        /// 开战：Prep → Battle（9.2 七步时间线）。门禁 = 非备战期 + 场上有未拾取晶矿（批次6，#17⑥：
        /// 必须捡完才能开战，纯拒绝四不动）；批次7 再加"有待选策略"一个条件，门禁形态不变。
        /// </summary>
        public bool StartBattle()
        {
            // 1. 门禁：非备战期纯拒绝；场上有未拾取晶矿纯拒绝（#17⑥——相位/敌方/种子流/钱包全不动）
            if (Phase != GamePhase.Prep) return false;
            if (_oresOnGround.Count > 0) return false;

            // 2. 遭遇生成：按编成表生成敌方（charId=0、IsPlayerSide=false、star=1），摆敌方半场
            SpawnEncounter();

            // 3. 参战者快照：我方战场名单——EnterPrep 只拉回/复活/恢复他们
            _lastBattleUnits = new List<Unit>(_playerBoard);

            // 4. 战斗种子：会话随机源开战瞬间抽 [1, int.MaxValue)（D2-6——战斗随机与会话随机分家）
            LastBattleSeed = (uint)_random.NextInt(1, int.MaxValue);

            // 5. 每场一个新 CombatSystem（目标锁定/进度条账本随构造新生，绝不跨场残留）
            _combat = new CombatSystem(_board, _bus, LastBattleSeed);

            // 6. 羁绊钩子开战注册（#22④ 唯一入口；快照激活档 + 开战瞬间冻结的成员名单）
            _synergy.RegisterBattleHooks(_combat, _board);

            // 7. 构造即终局（一方空场）→ 战斗根本没发生，直接结算（钩子永不触发）
            Phase = GamePhase.Battle;
            if (_combat.IsOver)
                SettleBattle();
            return true;
        }

        /// <summary>
        /// 结算（9.3 时间线；#23 排空闸门回调的落点——表现层整场预演+演绎排空后调它，
        /// 结算/进备战不许早于画面见玩家）。校验：Phase=Battle 且战斗已见分晓（IsOver 查棋盘现算）。
        /// 结算瞬间直接读 Combat 的胜负与步数，不订阅事件、不另记账——无隐藏状态。
        /// </summary>
        public bool SettleBattle()
        {
            if (Phase != GamePhase.Battle || _combat == null || !_combat.IsOver) return false;

            RoundDef roundDef = _tables.Rounds.Get(Round);
            bool won = _combat.PlayerWon;
            int enemySurvivors = _board.CountOnBoard(false);   // 战败扣血的"存活敌人数"（战斗结束、敌尸已清，现算即真值）

            // 1. Phase=Settlement（先占位：后续步骤全部属于"战后结算"这一段）
            Phase = GamePhase.Settlement;

            // 2. 战败扣血（2026-10-27 拍板：败 = DamageBase + 存活敌人数 × 1，败得越惨扣越多；
            //    奖励关豁免恒 0——"奖励关败不掉血"；Boss 败 = 血量清零判负，不走数值公式）
            int damageTaken = 0;
            if (!won)
            {
                if (roundDef.Type == RoundType.Boss)
                    damageTaken = PlayerHp;
                else if (roundDef.Type != RoundType.Reward)
                    damageTaken = roundDef.DamageBase + enemySurvivors;
                if (damageTaken > 0) ApplyHpChange(-damageTaken);
            }

            // 3. 结算收入（#21①⑤）：先按"本场之前已通关精英数"入账（R6 场本身还是 5 金段位），
            //    精英胜才涨段（下一场起 +1；精英败档位不涨）
            IncomeBill bill = _eco.SettleBattleIncome(won, _eliteClears);
            if (won && roundDef.Type == RoundType.Elite) _eliteClears++;

            // 4. Boss 战胜利限时回血（#21⑨：≤2700 步 +35、超时只 +10；封顶 100 在 ApplyHpChange 统一钳）
            if (won && roundDef.Type == RoundType.Boss)
            {
                int heal = _combat.TickCount <= BossTimeLimitTicks ? BossHealFast : BossHealSlow;
                if (heal > 0) ApplyHpChange(heal);
            }

            // 5. 奖励关胜利 → 策略记账（批次7 的 EnterPrep 弹选消费此旗标）
            if (won && roundDef.Type == RoundType.Reward) _policyDue = true;

            // 6. 广播回合结束的事实快照
            _bus.Publish(new RoundEndedEvent(Round, won, damageTaken, bill.TotalGold));

            // 7. 终局判定：血尽 或 赢下 R14
            if (PlayerHp <= 0 || (won && Round == TotalRounds))
            {
                bool runWon = PlayerHp > 0;
                FinalRating = RateRun(runWon, PlayerHp);
                _bus.Publish(new RunFinishedEvent(FinalRating.Value,
                    runWon ? TotalRounds : Round - 1, PlayerHp));
                Phase = GamePhase.RunOver;
            }
            return true;
        }

        // ─────────────────────── 备战期操作（全部仅 Prep，非法相位纯拒绝） ───────────────────────

        /// <summary>买经验（#21⑤：4 金 = 4 经验，不限购；满级/没钱由 Economy 内部纯拒绝）。
        /// 2026-10-27 拍板补的 Session 入口——"备战期操作全部仅 Prep"的拒绝矩阵不留缺口。</summary>
        public bool BuyXp()
        {
            if (Phase != GamePhase.Prep) return false;
            return _eco.BuyXp();
        }

        /// <summary>手动刷新商店（2 金/次；钱不够由 ShopSystem 内部纯拒绝）。</summary>
        public bool RefreshShop()
        {
            if (Phase != GamePhase.Prep) return false;
            return _shop.Refresh();
        }

        /// <summary>
        /// 买一格（9.4）：备战席余位检查先于扣钱（满员纯拒绝）→ 商店结算（内部扣钱+ShopBoughtEvent）
        /// → 建单位入备战席 → 名册事实广播 → 合成检查。
        /// 购买本身不重算羁绊（2026-10-27 拍板）；只有合成改变了场上成员，才在连锁结束后统一重算一次。
        /// "角色落地"全链路归 Session——商店只返回 CharacterDef（职责边界，批次2 定案）。
        /// </summary>
        public bool Buy(int slotIndex)
        {
            if (Phase != GamePhase.Prep) return false;
            if (_bench.Count >= BenchCapacity) return false;   // 满员：先于扣钱纯拒绝

            CharacterDef def = _shop.Buy(slotIndex);
            if (def.Id == 0) return false;                     // 格号越界/空格/钱不够：商店已纯拒绝

            var u = new Unit(_ids.Next(), def.Id, true, def.StatsForStar(1), _bus, 1);
            _bench.Add(u);
            _bus.Publish(new UnitAcquiredEvent(u.Id, u.CharId, u.Star));

            bool boardChanged = TryMerge(u.CharId);
            if (boardChanged) RecalculateSynergies();
            return true;
        }

        /// <summary>
        /// 卖出（9.4）：退金 = SellPrice(star)（总投入 - (星级-1)）；按 CopiesForStar 折份数回池
        /// （1星回 1 份 / 2星回 3 份 / 3星回 9 份——回池份数恒 ≤ 当初买入份数，永不超池封顶）；
        /// 场上卖先移除；表外单位（charId 查无）不可卖。
        /// 备战席卖出不改变场上成员，不重算；场上卖出才重算。
        /// </summary>
        public bool Sell(int unitId)
        {
            if (Phase != GamePhase.Prep) return false;
            Unit u = FindPlayerUnit(unitId);
            if (u == null) return false;

            CharacterDef def = _tables.Characters.Get(u.CharId);
            if (def.Id == 0) return false;                     // 表外单位不可卖

            bool boardUnit = _playerBoard.Contains(u);
            if (boardUnit)
            {
                if (!_board.Remove(u)) return false;            // 理论不可达（占用账本只在三个出口内变更）
                _playerBoard.Remove(u);
            }
            else
            {
                _bench.Remove(u);
            }

            int copies = CharacterDef.CopiesForStar(u.Star);
            for (int i = 0; i < copies; i++) _pool.Return(u.CharId);

            int gold = def.SellPrice(u.Star);
            _eco.AddGold(gold);
            _bus.Publish(new UnitSoldEvent(u.Id, u.CharId, u.Star, gold));
            if (boardUnit) RecalculateSynergies();
            return true;
        }

        /// <summary>上场：板位 = 等级（我方战场人数 &lt; Level 才放行）；格子合法性（己方半场/未占用）由棋盘拒。
        /// 上场后重算羁绊——场上成员变了，新上场单位才会获得加成（备战席不享受）。</summary>
        public bool PlaceOnBoard(int unitId, Pos cell)
        {
            if (Phase != GamePhase.Prep) return false;
            Unit u = FindBenchUnit(unitId);
            if (u == null) return false;                        // 不在备战席：未知 id / 已在场上 / 敌方
            if (_playerBoard.Count >= _eco.Level) return false; // 板位=等级（等级=上场位，#21⑤）
            if (!_board.TryPlace(u, cell)) return false;

            _bench.Remove(u);
            _playerBoard.Add(u);
            RecalculateSynergies();
            return true;
        }

        /// <summary>板上挪位（备战拖拽；只能己方半场内挪）。移动不改变场上成员集——不触发羁绊重算。</summary>
        public bool MoveOnBoard(int unitId, Pos to)
        {
            if (Phase != GamePhase.Prep) return false;
            Unit u = FindBoardUnit(unitId);
            if (u == null) return false;
            return _board.TryMove(u, to, mustStayInOwnHalf: true);
        }

        /// <summary>下场：回备战席。板位腾出；重算时下场单位会被按源摘干净（备战席只摘不挂）。</summary>
        public bool TakeOffBoard(int unitId)
        {
            if (Phase != GamePhase.Prep) return false;
            Unit u = FindBoardUnit(unitId);
            if (u == null) return false;
            if (!_board.Remove(u)) return false;

            _playerBoard.Remove(u);
            _bench.Add(u);
            RecalculateSynergies();
            return true;
        }

        // ─────────────────────── EnterPrep 六步（9.3；StartRun/StartPrep 共用） ───────────────────────

        private void EnterPrep()
        {
            // 0. Phase=Prep → 广播回合开始
            Phase = GamePhase.Prep;
            RoundDef roundDef = _tables.Rounds.Get(Round);
            _bus.Publish(new RoundStartedEvent(Round, roundDef.Type));

            // 1. 敌方退场：上一场敌方移出棋盘 + 敌方名册（每场按编成表重新生成——精英/Boss 轮换因此成立）
            for (int i = 0; i < _enemyRoster.Count; i++)
            {
                Unit u = _enemyRoster[i];
                if (u.IsOnBoard) _board.Remove(u);
            }
            _enemyRoster.Clear();

            if (_lastBattleUnits != null)
            {
                // 2. 幸存者拉回己方半场（战斗走位可越境；切比雪夫最近空格、并列取扫描序先到——与索敌同套规则）
                foreach (Unit u in _lastBattleUnits)
                {
                    if (!u.IsAlive || !u.IsOnBoard) continue;
                    if (_board.IsPlayerHalf(u.Pos)) continue;
                    Pos home = NearestFreePlayerCell(u.Pos);
                    if (home.X >= 0) _board.TryMove(u, home, mustStayInOwnHalf: false);
                }

                // 3. 上一场参战者满血归位（RestoreFullHp 含清蓝——蓝不跨战斗）；
                //    死者优先原位复活（被占让位）——复活放回棋盘，玩家上下场自由随后自理
                foreach (Unit u in _lastBattleUnits)
                {
                    if (!u.IsAlive && !TryReviveOnPrep(u)) continue;   // 半场满复活失败=理论不可达，跳过防御
                    u.RestoreFullHp();
                }
            }

            // 4. 商店免费上架（收入与被动经验已改在上一场结算发放——#21①⑤；R1 首备无结算，钱包=起始 3 金）
            _shop.RollOffer();

            // 5. 策略弹选：_policyDue → 抽 3 候选 + 广播（批次7 接线）

            // 6. 羁绊兜底自愈重算（清战斗内动态修饰器；复活归位后阵容应与冻结时一致，不一致 = 有遗漏路径）
            RecalculateSynergies();
        }

        /// <summary>死者复活：优先原位（玩家半场内才合法）；被占或死于敌方半场 → 让位到最近己方空格。</summary>
        private bool TryReviveOnPrep(Unit u)
        {
            if (_board.IsPlayerHalf(u.Pos) && _board.GetOccupant(u.Pos) == null)
                return _board.TryPlace(u, u.Pos);
            Pos to = NearestFreePlayerCell(u.Pos);
            return to.X >= 0 && _board.TryPlace(u, to);
        }

        /// <summary>玩家半场内找"离 from 切比雪夫最近"的空格；行主序扫描 + 严格小于才替换 → 并列取扫描序先到。</summary>
        private Pos NearestFreePlayerCell(Pos from)
        {
            Pos best = new Pos(-1, -1);
            int bestDist = int.MaxValue;
            for (int y = 0; y < BoardData.PlayerHalfRows; y++)
            {
                for (int x = 0; x < BoardData.Width; x++)
                {
                    var c = new Pos(x, y);
                    if (_board.GetOccupant(c) != null) continue;
                    int d = from.ChebyshevTo(c);
                    if (d < bestDist) { bestDist = d; best = c; }
                }
            }
            return best;
        }

        // ─────────────────────── 遭遇生成（9.2 第 2 步） ───────────────────────

        /// <summary>
        /// 按编成表生成敌方并摆敌方半场。摆位冲突（同格编成/残留占位）由"敌方半场首个空格"兜底接住
        /// （表构造期不校验半场归属——半场规则属 Battle 层，阶段2-接口设计 3.2）。
        /// 敌方单位不入卡池、不可买/卖——每场生成、EnterPrep 整体退场。
        /// </summary>
        private void SpawnEncounter()
        {
            _oreTierByUnitId.Clear();   // 每场重建：上一场未死怪的账目随退场作废（没死就没事件，#17⑧）

            Encounter encounter = _tables.Enemies.Get(Round);
            if (encounter.IsEmpty) return;   // 未配置回合：空编成（构造即终局 → 玩家胜）

            foreach (EnemySpawn spawn in encounter.Spawns)
            {
                var u = new Unit(_ids.Next(), 0, false, spawn.Stats, _bus, 1);
                if (_board.TryPlace(u, spawn.Cell))
                {
                    _enemyRoster.Add(u);
                }
                else
                {
                    Pos fallback = FirstFreeEnemyCell();
                    if (fallback.X < 0 || !_board.TryPlace(u, fallback))
                        throw new InvalidOperationException(
                            $"回合 {Round}「{spawn.DisplayName}」摆位失败且敌方半场无空格——编成表数据事故");
                    _enemyRoster.Add(u);
                }
                // 掉落记账（批次6）：只看编成表挡位字段、不查回合类型（#17③——非奖励关不掉靠填表纪律实现）
                if (spawn.OreTier > 0) _oreTierByUnitId[u.Id] = spawn.OreTier;
            }
        }

        /// <summary>敌方半场（row 3~5）行主序扫到的首个空格。</summary>
        private Pos FirstFreeEnemyCell()
        {
            for (int y = BoardData.Height - BoardData.PlayerHalfRows; y < BoardData.Height; y++)
            {
                for (int x = 0; x < BoardData.Width; x++)
                {
                    var c = new Pos(x, y);
                    if (_board.GetOccupant(c) == null) return c;
                }
            }
            return new Pos(-1, -1);
        }

        // ─────────────────────── 合成（9.4） ───────────────────────

        /// <summary>
        /// 合成检查（买入后触发；连锁到 3 星）：同 CharId 同星单位每凑满 3 → 合 1 升星。
        /// 升星锚点优先"场上名单中的第一个同星单位"，没有场上同星才取备战席最早者；
        /// 两张材料优先从备战席消耗，不足才消耗其他场上同星单位（2026-10-27 拍板）。
        /// 高星单位在锚点格位/名单原位置替换（Unit 的星级与出厂面板只读，故新建高星 Unit）。
        /// 返回值 = 是否有任何一次合成改变了场上成员；调用方据此决定要不要全量重算（仅备战席合成为 false）。
        /// </summary>
        private bool TryMerge(int charId)
        {
            CharacterDef def = _tables.Characters.Get(charId);
            if (def.Id == 0) return false;

            bool boardChanged = false;
            int star = 1;
            while (star <= 2)   // 合出 star+1：1→2、2→3；3 星不再合（CopiesForStar 封顶）
            {
                var boardCandidates = new List<Unit>();
                for (int i = 0; i < _playerBoard.Count; i++)
                {
                    Unit u = _playerBoard[i];
                    if (u.CharId == charId && u.Star == star) boardCandidates.Add(u);
                }
                var benchCandidates = new List<Unit>();
                for (int i = 0; i < _bench.Count; i++)
                {
                    Unit u = _bench[i];
                    if (u.CharId == charId && u.Star == star) benchCandidates.Add(u);
                }
                if (boardCandidates.Count + benchCandidates.Count < 3) { star++; continue; }

                // 1) 升星锚点：优先场上同星；没有才取备战席最早者
                Unit anchor = boardCandidates.Count > 0 ? boardCandidates[0] : benchCandidates[0];
                bool anchorOnBoard = anchor.IsOnBoard;
                Pos anchorCell = anchor.Pos;

                // 2) 材料：备战席优先（锚点除外），不足再从其他场上同星补
                var materials = new List<Unit>(2);
                for (int i = 0; i < benchCandidates.Count && materials.Count < 2; i++)
                {
                    Unit u = benchCandidates[i];
                    if (ReferenceEquals(u, anchor)) continue;
                    materials.Add(u);
                }
                for (int i = 0; i < boardCandidates.Count && materials.Count < 2; i++)
                {
                    Unit u = boardCandidates[i];
                    if (ReferenceEquals(u, anchor)) continue;
                    materials.Add(u);
                }
                if (materials.Count < 2)
                    throw new InvalidOperationException("合成候选已满三张但材料不足——名册账本自相矛盾");

                // 3) 先消耗材料（涉及场上的材料同时腾格），记录本次合成是否改变场上成员
                bool mergeChangesBoard = anchorOnBoard;
                for (int i = 0; i < materials.Count; i++)
                {
                    Unit material = materials[i];
                    if (material.IsOnBoard)
                    {
                        mergeChangesBoard = true;
                        if (!_board.Remove(material))
                            throw new InvalidOperationException("合成材料腾格失败——在架者账本自相矛盾");
                        _playerBoard.Remove(material);
                    }
                    else
                    {
                        _bench.Remove(material);
                    }
                }

                // 4) 高星单位在锚点格位/名单原位置替换；合成体满血出厂、新星面板
                int newStar = star + 1;
                var merged = new Unit(_ids.Next(), charId, true, def.StatsForStar(newStar), _bus, newStar);
                if (anchorOnBoard)
                {
                    if (!_board.Remove(anchor))
                        throw new InvalidOperationException("升星锚点腾格失败——在架者账本自相矛盾");
                    int index = _playerBoard.IndexOf(anchor);
                    if (index < 0) throw new InvalidOperationException("升星锚点不在我方战场名册中");
                    if (!_board.TryPlace(merged, anchorCell))
                        throw new InvalidOperationException("合成体继承格位失败——刚腾出的格不可能被占");
                    _playerBoard[index] = merged;
                }
                else
                {
                    int index = _bench.IndexOf(anchor);
                    if (index < 0) throw new InvalidOperationException("升星锚点不在备战席名册中");
                    _bench[index] = merged;
                }

                _bus.Publish(new UnitMergedEvent(merged.Id, charId, newStar,
                    new[] { anchor.Id, materials[0].Id, materials[1].Id }));
                boardChanged |= mergeChangesBoard;
                // 不升星、继续查当前星：当前星凑不齐 3 时才 star++ 看下一星（连锁合成）
            }
            return boardChanged;
        }

        // ─────────────────────── 晶矿拾取（批次6） ───────────────────────

        /// <summary>
        /// 捡矿（7.2）：拒绝矩阵 = 非备战期 / 未知掉落物 id，纯拒绝（四不动）。
        /// 通过后挡位查池 → 会话随机源抽 1 条 → 立即发放 → 广播 OreOpenedEvent → 移出列表。
        /// 发放路径（2026-10-27 拍板：掉落物只有金币/经验/装备散件三种）：
        /// 金币 → Eco.AddGold；经验 → Eco.AddXp；装备 → 仅事件（EquipmentId 已带，背包阶段3 接——#12⑧ 只给散件）。
        /// </summary>
        public bool PickOre(int oreId)
        {
            if (Phase != GamePhase.Prep) return false;

            int index = -1;
            OreDrop drop = default;
            for (int i = 0; i < _oresOnGround.Count; i++)
            {
                if (_oresOnGround[i].Id != oreId) continue;
                index = i;
                drop = _oresOnGround[i];
                break;
            }
            if (index < 0) return false;   // 未知 id（含已捡过的——成功即移出列表，再捡自然落空）

            OreDef def = DrawOre(drop.Tier);
            switch (def.Type)
            {
                case OreRewardType.Gold: _eco.AddGold(def.Amount); break;
                case OreRewardType.Xp: _eco.AddXp(def.Amount); break;
                case OreRewardType.Equipment: break;   // 阶段2 仅事件：消费者阶段3 订阅 OreOpenedEvent
            }

            _bus.Publish(new OreOpenedEvent(drop.Id, drop.Tier, drop.Cell,
                def.Id, def.Type, def.Amount, def.EquipmentId));
            _oresOnGround.RemoveAt(index);   // 唯一出口（7.2 留存纪律）
            return true;
        }

        /// <summary>挡位查池抽 1 条（7.2）：会话随机源吃一次整数抽取——
        /// 同种子同操作序列 = 同奖励（确定性测试焊死，也是阶段5 回放重演的地基之一）。</summary>
        private OreDef DrawOre(int tier)
        {
            OreTierDef pool = _tables.Ores.GetTier(tier);
            int pick = _random.NextInt(pool.OreIds.Length);
            return _tables.Ores.GetOre(pool.OreIds[pick]);
        }

        /// <summary>战斗死亡事件的掉落记账（批次6）：死者在本场账本里 → 生成掉落物
        /// （Id 用 IdAllocator 发、Cell = 死亡格）→ 出账本 → 广播 OreDroppedEvent。
        /// 纯事件驱动：CombatSystem 零改动、不知道晶矿存在；
        /// 玩家单位不在账本里永不掉矿；未死的怪随退场自然不掉（没死就没事件，#17⑧）。</summary>
        private void OnUnitDied(UnitDiedEvent e)
        {
            if (!_oreTierByUnitId.TryGetValue(e.UnitId, out int tier)) return;
            _oreTierByUnitId.Remove(e.UnitId);
            var drop = new OreDrop(_ids.Next(), tier, e.Cell);
            _oresOnGround.Add(drop);
            _bus.Publish(new OreDroppedEvent(drop.Id, drop.Tier, drop.Cell));
        }

        // ─────────────────────── 内部 ───────────────────────

        /// <summary>羁绊重算的唯一入口。临时输入 = 我方战场 + 备战席（刚下场者也要能摘到旧加成）；
        /// SynergySystem 内部只给棋盘上的玩家单位挂账——备战席只摘不挂。</summary>
        private void RecalculateSynergies()
        {
            _synergyInput.Clear();
            _synergyInput.AddRange(_playerBoard);
            _synergyInput.AddRange(_bench);
            _synergy.Recalculate(_synergyInput, _board);
        }

        /// <summary>血量唯一写入口：回复封顶 100、扣血钳 ≥ 0；实变才广播（只陈述真发生的事实）。</summary>
        private void ApplyHpChange(int delta)
        {
            int target = PlayerHp + delta;
            if (target > StartHp) target = StartHp;
            if (target < 0) target = 0;
            int applied = target - PlayerHp;
            if (applied == 0) return;
            PlayerHp = target;
            _bus.Publish(new HpChangedEvent(PlayerHp, applied));
        }

        /// <summary>评级纯函数（9.3）：未通关 C；通关按剩余血量 S(≥50)/A(≥25)/B。测试直调。</summary>
        public static RunRating RateRun(bool runWon, int finalHp)
        {
            if (!runWon) return RunRating.C;
            if (finalHp >= 50) return RunRating.S;
            if (finalHp >= 25) return RunRating.A;
            return RunRating.B;
        }

        private Unit FindPlayerUnit(int unitId)
        {
            return FindBoardUnit(unitId) ?? FindBenchUnit(unitId);
        }

        private Unit FindBoardUnit(int unitId)
        {
            for (int i = 0; i < _playerBoard.Count; i++)
            {
                if (_playerBoard[i].Id == unitId) return _playerBoard[i];
            }
            return null;
        }

        private Unit FindBenchUnit(int unitId)
        {
            for (int i = 0; i < _bench.Count; i++)
            {
                if (_bench[i].Id == unitId) return _bench[i];
            }
            return null;
        }
    }
}
