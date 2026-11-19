using System;
using System.Threading.Tasks;
using AutoChess.Game;
using FairyGUI;

namespace AutoChess.Presentation
{
    /// <summary>
    /// 商店弹层（批次4U1，PanelId.Shop）：绑定 FGUI 包 Package1 的 ShopPanel 组件。
    /// 开合动画 = 作者设计的控制器协议（2026-11-19 补充拍板）：
    ///  · 控制器 panelState 两页——close = 商品区(n1)整体置于组件画布外（y=-520），
    ///    open = 滑回画布内（y=0）；过渡 t1/t2（各 0.2s）挂在页切换上，代码只翻
    ///    selectedIndex，滑入/滑出自动播放（Play 实测 40ms 采样 -520→-312→-141→-36→0）；
    ///  · 打开 = 建视图（默认页 close，内容在画布外）后立即翻到 open 页——内容滑入登场；
    ///  · 关闭 = 先翻回 close 页（滑出），等过渡播完再真正 Close 销毁视图——直接销毁会把
    ///    滑出动画拦腰截断；Mask 是全屏触摸块，必须随内容一起退场（否则备战 UI 被挡）。
    /// 其余：全屏透明 Mask（点空白处退出）+ 商品区（刷新按钮 + 5 张商品卡）。
    /// 商品卡（RoleChooseBtn 结构）：立绘 Role / 费用 cost / 双羁绊标签 syTxt1·syTxt2 / 星级 starLevel。
    /// 商店恒卖 1 星——starLevel 三颗星格按 1 星亮法（通用按星显隐，非 1 星入口将来复用）。
    /// 买不起的卡置灰（grayed）仍可点击——点击走会话 Buy 的纯拒绝（提示 UI 4U2 拍板）。
    /// </summary>
    public sealed class ShopPanel : FguiPanelBase
    {
        private const int CardCount = ShopSystem.Slots;   // 5 格货架

        /// <summary>开合状态控制器名（FGUI 包内实名；页序 0=close 在画布外、1=open 在画布内）。</summary>
        private const string StateControllerName = "panelState";

        /// <summary>销毁前等待滑出的时长：t2 时长 0.2s + 余量（过渡播完视图才消失）。</summary>
        private const float SlideOutSeconds = 0.25f;

        private readonly PanelManager _panels;
        private readonly Func<GameSession> _session;

        private GButton _refBtn;
        private GTextField _refTxt;
        private GButton[] _cards;
        private Controller _stateController;
        private bool _closing;

        public ShopPanel(UiEventBridge bridge, PanelManager panels, Func<GameSession> session)
            : base(PanelId.Shop, bridge)
        {
            _panels = panels ?? throw new ArgumentNullException(nameof(panels));
            _session = session ?? throw new ArgumentNullException(nameof(session));
        }

        protected override string PackageName => UiCatalog.PackageName;
        protected override string ComponentName => "ShopPanel";

        protected override void OnViewCreated()
        {
            _closing = false;   // 重开可再关（上次关闭的滑出标志随旧视图作废）

            GComponent content = (GComponent)View.GetChild("n1");   // 商品区（ShopContent 实例）
            _refBtn = (GButton)content.GetChild("refBtn");
            _refTxt = (GTextField)content.GetChild("refTxt");
            _cards = new GButton[CardCount];
            for (int i = 0; i < CardCount; i++)
                _cards[i] = (GButton)content.GetChild("g" + (i + 1));

            // 控制器协议：翻到 open 页——t1 滑入自动播（内容从画布外滑回画布内）
            _stateController = View.GetController(StateControllerName);
            if (_stateController != null) _stateController.selectedIndex = 1;

            // Mask：点空白退出（内容层 n1 在遮罩之上、是兄弟节点——点内容不会冒泡到遮罩）
            ((GComponent)View.GetChild("n0")).onClick.Add(BeginClose);

            _refBtn.onClick.Add(() => _session()?.RefreshShop());
            for (int i = 0; i < CardCount; i++)
            {
                int slot = i;   // 闭包捕获循环变量
                _cards[i].onClick.Add(() => _session()?.Buy(slot));
            }

            Bind<ShopRolledEvent>(e => RefreshCards());
            Bind<ShopBoughtEvent>(e => RefreshCards());
            Bind<GoldChangedEvent>(e => RefreshCards());   // 钱变了 → 重算买得起置灰
            RefreshCards();
        }

        protected override void OnViewClosing()
        {
            _refBtn = null; _refTxt = null; _cards = null; _stateController = null;
        }

        /// <summary>点遮罩关商店：先翻 close 页滑出（t2 自动播），等播完再销毁视图。
        /// 滑出期间再点遮罩忽略（_closing 挡）；期间面板被其它路径提前关（场景拆除 CloseAll）——
        /// Panels.Close 幂等无害；await 续走 Unity 主线程同步上下文（BattleRoot 同款模式）。</summary>
        private void BeginClose()
        {
            if (_closing) return;
            _closing = true;
            if (_stateController != null) _stateController.selectedIndex = 0;   // 滑出自动播
            CloseAfterSlideAsync();
        }

        private async void CloseAfterSlideAsync()
        {
            await Task.Delay(TimeSpan.FromSeconds(SlideOutSeconds));
            _panels.Close(Id);
        }

        private void RefreshCards()
        {
            GameSession s = _session();
            if (s == null) return;
            CharacterDef[] offers = s.Shop.Offers;
            if (_refTxt != null) _refTxt.text = "X" + ShopSystem.RefreshCost;

            for (int i = 0; i < _cards.Length; i++)
            {
                GButton card = _cards[i];
                if (card == null) continue;
                CharacterDef def = offers[i];
                card.visible = def.Id != 0;                 // 空格（已买走/未上架）整卡隐藏
                if (def.Id == 0) continue;

                GLoader portrait = (GLoader)card.GetChild("Role");
                if (portrait != null) portrait.url = UiCatalog.PortraitUrl(def.Id);
                GTextField cost = (GTextField)card.GetChild("cost");
                if (cost != null) cost.text = "X" + def.Cost;
                GTextField sy1 = (GTextField)card.GetChild("syTxt1");
                if (sy1 != null) sy1.text = def.Synergies.Length > 0 ? UiCatalog.FactionName(def.Synergies[0]) : "";
                GTextField sy2 = (GTextField)card.GetChild("syTxt2");
                if (sy2 != null) sy2.text = def.Synergies.Length > 1 ? UiCatalog.FactionName(def.Synergies[1]) : "";

                GComponent star = (GComponent)card.GetChild("starLevel");
                if (star != null)
                {
                    star.GetChild("n0").visible = true;    // 商店恒 1 星：只亮第一颗
                    star.GetChild("n1").visible = false;
                    star.GetChild("n2").visible = false;
                }
                card.grayed = s.Eco.Gold < def.Cost;       // 买不起置灰；点击仍走会话纯拒绝
            }
        }
    }
}
