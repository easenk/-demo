using System;
using AutoChess.Game;
using FairyGUI;
using UnityEngine;

namespace AutoChess.Presentation
{
    /// <summary>
    /// 备战主面板（批次4U1，PanelId.MainHud）：绑定 FGUI 包 Package1 的 PrepPanel 组件——
    /// 金币（shopBtn 内 titile 文本）/ 血量（n30 = Health 组件实例）/ 等级·经验·买经验价格（expBtn，价格随"学者会"折扣刷新）/
    /// 经验填充条（expBar 垂直填充：进度 = 级内经验 / 级内跨度，升级清零重计、满级钉 100%——与 Economy 逻辑一致）/
    /// 回合节点条（nodeScroll 内 Node1~Node14 装载器：隐藏已过、只显现当前，每次备战期刷新——2026-11-19 用户拍板）/
    /// 羁绊侧栏（syScroll 六行：计数 + 档位变色 2 档灰 / 4 档银 / 6 档金，2026-11-19 用户拍板）/
    /// 备战席 9 卡（preBench 内 role1~role9：立绘 roleN + 星级 startLevel 仅占用时可见，2026-11-19 用户拍板）/
    /// 背包 25 格（eqView：装备图标素材未定——本批保持空槽，件数上限门禁在逻辑层）/
    /// 拆卸器按钮数量（n31 = Disassembler 组件实例）。
    /// 交互：beginBtn 开战（回调 BattleRoot.PlayNext）、expBtn 买经验、shopBtn 开商店；
    /// 备战席互拖换位（draggable + 落点命中 → 会话 SwapBenchSlots——"交换里面的角色不交换容器"，容器拖完归位）。
    /// 数据：初值开面板时从会话拉、后续变化走 UiEventBridge 事件；每回合开始全量重刷（换局 / 进回合自愈）。
    /// 战斗期面板保持可见（HUD 语义）——按钮操作全部被会话相位门禁纯拒绝，天然无副作用。
    /// </summary>
    public sealed class PrepHudPanel : FguiPanelBase
    {
        private const int NodeCount = GameSession.TotalRounds;   // 14 节点 = RoundTable 14 回合
        private const int BenchSlots = GameSession.BenchCapacity; // 9 卡 = D2-8 备战席容量

        // 羁绊档位三色（2026-11-19 用户拍板：2 档灰 / 4 档银 / 6 档金；未激活未指定 → 暗灰弱化）
        private static readonly Color SynergyColorInactive = new Color(0.45f, 0.45f, 0.45f);
        private static readonly Color SynergyColorTier0 = new Color(0.62f, 0.62f, 0.62f);
        private static readonly Color SynergyColorTier1 = new Color(0.80f, 0.80f, 0.86f);
        private static readonly Color SynergyColorTier2 = new Color(1f, 0.84f, 0f);

        private readonly PanelManager _panels;
        private readonly Func<GameSession> _session;
        private readonly Action _beginBattle;

        // 子件引用（OnViewCreated 一次抓取；视图销毁即置空——防读已销毁对象，DevStubPanel 同款纪律）
        private GButton _beginBtn;
        private GButton _expBtn;
        private GButton _shopBtn;
        private GTextField _levelTxt;
        private GTextField _expTxt;
        private GTextField _priceTxt;
        private GImage _expBar;
        private GTextField _goldTxt;
        private GLoader[] _nodes;
        private GTextField[] _syTxts;      // 下标 0~5 对应 SynergyId 1~6（Brute..Crown，enum 值即行号）
        private GButton[] _benchCards;
        private GTextField _hpTxt;
        private GTextField _unsmithTxt;

        // 互拖换位的瞬时状态（单指针单拖拽；松手即清）
        private Vector2 _dragOrigin;
        private int _dragIndex = -1;

        public PrepHudPanel(UiEventBridge bridge, PanelManager panels, Func<GameSession> session, Action beginBattle)
            : base(PanelId.MainHud, bridge)
        {
            _panels = panels ?? throw new ArgumentNullException(nameof(panels));
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _beginBattle = beginBattle ?? throw new ArgumentNullException(nameof(beginBattle));
        }

        protected override string PackageName => UiCatalog.PackageName;
        protected override string ComponentName => "PrepPanel";

        protected override void OnViewCreated()
        {
            GrabChildren();
            BindEvents();
            HookInteractions();
            RefreshAll();   // 初值：面板晚于 NewRun 打开——已发生过的事件靠拉会话现势补
        }

        protected override void OnViewClosing()
        {
            _beginBtn = null; _expBtn = null; _shopBtn = null;
            _levelTxt = null; _expTxt = null; _priceTxt = null; _expBar = null; _goldTxt = null;
            _nodes = null; _syTxts = null; _benchCards = null;
            _hpTxt = null; _unsmithTxt = null;
        }

        // ─────────────────────── 子件抓取（名字 = FGUI 包内实名，含用户的手写名如 titile/startLevel） ───────────────────────

        private void GrabChildren()
        {
            _beginBtn = (GButton)View.GetChild("beginBtn");
            _expBtn = (GButton)View.GetChild("expBtn");
            _shopBtn = (GButton)View.GetChild("shopBtn");
            _levelTxt = (GTextField)_expBtn.GetChild("levelTxt");
            _expTxt = (GTextField)_expBtn.GetChild("expTxt");
            _priceTxt = (GTextField)_expBtn.GetChild("n4");
            _expBar = (GImage)_expBtn.GetChild("expBar");
            _goldTxt = (GTextField)_shopBtn.GetChild("titile");   // 包内元件名即此拼写（用户手写）

            GComponent nodeScroll = (GComponent)View.GetChild("nodeScroll");
            _nodes = new GLoader[NodeCount];
            for (int i = 0; i < NodeCount; i++)
                _nodes[i] = (GLoader)nodeScroll.GetChild("Node" + (i + 1));

            GComponent syScroll = (GComponent)View.GetChild("syScroll");
            _syTxts = new GTextField[6];
            string[] rowNames = { "bruteTxt", "bulwarkTxt", "arcaneTxt", "rangerTxt", "shadowTxt", "crownTxt" };
            for (int i = 0; i < 6; i++)
                _syTxts[i] = (GTextField)syScroll.GetChild(rowNames[i]);

            GComponent preBench = (GComponent)View.GetChild("preBench");
            _benchCards = new GButton[BenchSlots];
            for (int i = 0; i < BenchSlots; i++)
                _benchCards[i] = (GButton)preBench.GetChild("role" + (i + 1));

            _hpTxt = (GTextField)((GComponent)View.GetChild("n30")).GetChild("n1");          // Health 组件实例
            _unsmithTxt = (GTextField)((GButton)View.GetChild("n31")).GetChild("count");     // Disassembler 组件实例
        }

        // ─────────────────────── 事件接线（Bind 关面板自动摘） ───────────────────────

        private void BindEvents()
        {
            Bind<GoldChangedEvent>(e => { if (_goldTxt != null) _goldTxt.text = "X" + e.Gold; });
            Bind<XpChangedEvent>(e => RefreshEconomy());
            Bind<HpChangedEvent>(e => { if (_hpTxt != null) _hpTxt.text = e.Hp.ToString(); });
            Bind<SynergiesChangedEvent>(e => RefreshSynergies());
            Bind<InventoryChangedEvent>(e => { if (_unsmithTxt != null) _unsmithTxt.text = e.UnsmithTools.ToString(); });
            Bind<RoundStartedEvent>(e => RefreshAll());       // 每回合开始全量重刷（换局/进回合自愈）
            Bind<PolicyPickedEvent>(e => RefreshEconomy());   // "学者会"改买经验价
            Bind<UnitAcquiredEvent>(e => RefreshBench());
            Bind<UnitSoldEvent>(e => RefreshBench());
            Bind<UnitMergedEvent>(e => RefreshBench());
            // 上场/下场/互拖的会话级名册事实（4U1）：BoardData 的 Placed/Removed 在名册中途发布，
            // 在这里刷会读到旧名单（真台实抓）——BenchChanged 在名册终值后发布，拉现势安全
            Bind<BenchChangedEvent>(e => RefreshBench());
        }

        private void HookInteractions()
        {
            _beginBtn.onClick.Add(() => _beginBattle());
            _expBtn.onClick.Add(() => _session()?.BuyXp());
            _shopBtn.onClick.Add(() => _panels.Open(PanelId.Shop));

            for (int i = 0; i < _benchCards.Length; i++)
            {
                int index = i;                 // 闭包捕获循环变量
                GButton card = _benchCards[i];
                card.draggable = true;         // 互拖换位：拖的是"卡上的角色"，不是容器
                card.onDragStart.Add(ctx => { _dragOrigin = card.xy; _dragIndex = index; });
                card.onDragEnd.Add(ctx => OnBenchDragEnd(ctx, index));
            }
        }

        /// <summary>拖拽松手：容器先归位（交换角色不交换容器），再按落点换位——命中另一张卡才会话换位+重刷。</summary>
        private void OnBenchDragEnd(EventContext ctx, int index)
        {
            GButton card = _benchCards[index];
            if (card == null) return;
            card.xy = _dragOrigin;   // 容器归位（用户拍板"交换里面的角色不交换容器"）
            _dragIndex = -1;

            GameSession s = _session();
            if (s == null) return;
            var bench = s.Bench;
            if (index >= bench.Count) return;   // 空卡拖不动（SwapBenchSlots 也会拒，双保险）

            Vector2 p = ctx.inputEvent.position;   // 指针在 Stage 坐标系的落点
            for (int j = 0; j < _benchCards.Length; j++)
            {
                if (j == index) continue;
                GButton other = _benchCards[j];
                if (other == null) continue;
                Vector2 local = other.WorldToLocal(new Vector3(p.x, p.y, 0f));
                if (local.x < 0f || local.y < 0f || local.x > other.width || local.y > other.height) continue;
                s.SwapBenchSlots(index, j);   // 成功必发 BenchChangedEvent → 上面的绑定自动重刷；拒绝静默（空卡换位等）
                return;
            }
        }

        // ─────────────────────── 刷新（全部从会话现势拉——事件只当"该刷了"的信号） ───────────────────────

        private void RefreshAll()
        {
            RefreshEconomy();
            RefreshHp();
            RefreshNodes();
            RefreshSynergies();
            RefreshBench();
            RefreshInventory();
        }

        private void RefreshEconomy()
        {
            GameSession s = _session();
            if (s == null) return;
            Economy eco = s.Eco;
            if (_goldTxt != null) _goldTxt.text = "X" + eco.Gold;
            if (_levelTxt != null) _levelTxt.text = "LV : " + eco.Level + "\n";   // 保留包内文本的行尾换行（版式）
            if (_priceTxt != null) _priceTxt.text = "-" + eco.CurrentXpCost;
            if (_expTxt != null && _expBar != null)
            {
                if (eco.Level >= Economy.MaxLevel)
                {
                    _expTxt.text = "MAX";         // 满级：经验丢弃静默（Economy 语义），显示钉满
                    _expBar.fillAmount = 1f;
                }
                else
                {
                    int span = eco.XpProgress + eco.XpToNextLevel;   // 级内跨度 = 已攒 + 距下一级
                    _expTxt.text = eco.XpProgress + " / " + span;
                    _expBar.fillAmount = span > 0 ? (float)eco.XpProgress / span : 0f;
                }
            }
        }

        private void RefreshHp()
        {
            GameSession s = _session();
            if (s != null && _hpTxt != null) _hpTxt.text = s.PlayerHp.ToString();
        }

        private void RefreshNodes()
        {
            GameSession s = _session();
            if (s == null) return;
            int round = s.Round;
            for (int i = 0; i < _nodes.Length; i++)
                if (_nodes[i] != null)
                    _nodes[i].visible = (i + 1) == round;   // 隐藏已过、只显现当前（2026-11-19 用户拍板）
        }

        private void RefreshSynergies()
        {
            GameSession s = _session();
            if (s == null) return;
            var snapshot = s.Synergies;
            for (int i = 0; i < _syTxts.Length; i++)
            {
                GTextField txt = _syTxts[i];
                if (txt == null) continue;
                int count = 0, tier = -1;
                if (snapshot != null)
                {
                    foreach (SynergyInfo info in snapshot)
                        if ((int)info.SynergyId == i + 1) { count = info.Count; tier = info.TierIndex; break; }
                }
                txt.text = UiCatalog.FactionName((SynergyId)(i + 1)) + "X" + count;
                txt.color = tier < 0 ? SynergyColorInactive
                    : tier == 0 ? SynergyColorTier0
                    : tier == 1 ? SynergyColorTier1
                    : SynergyColorTier2;
            }
        }

        private void RefreshBench()
        {
            GameSession s = _session();
            if (s == null) return;
            var bench = s.Bench;
            for (int i = 0; i < _benchCards.Length; i++)
            {
                GButton card = _benchCards[i];
                if (card == null) continue;
                Unit u = i < bench.Count ? bench[i] : null;
                GLoader portrait = (GLoader)card.GetChild("n1");
                if (portrait != null) portrait.url = u != null ? UiCatalog.PortraitUrl(u.CharId) : null;
                GObject star = card.GetChild("startLevel");
                if (star != null) star.visible = u != null;   // 星级仅占用时可见（2026-11-19 用户拍板）
            }
        }

        private void RefreshInventory()
        {
            GameSession s = _session();
            if (s == null) return;
            if (_unsmithTxt != null) _unsmithTxt.text = s.UnsmithToolCount.ToString();
            // eqView 25 格：装备图标素材未定（2026-11-19 挂账——3 散件 + 8 大件共 11 张图待用户拍板来源），
            // 本批保持空槽；件数变化的门禁已在逻辑层（Inventory.Capacity）锁死
        }
    }
}
