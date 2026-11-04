using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AutoChess.Game;
using UnityEngine;

namespace AutoChess.Presentation
{
    /// <summary>
    /// 战台组合根（#23④，批次B2）：运行时串联 会话 → 预演（后台线程）→ 规划 → 播放 全链。
    /// 调试驱动形态：整局会话 = L8 满级 + 注资买满阵容（预演基准同款构建，内容随种子）；
    /// 面板逐场点「开战」——预演后台 Task → await 主线程续接（预演期间主线程不碰会话，
    /// #23① 纪律）→ 规划器排时间轴 → TimelinePlayer 播放 → 播完 Finished 即结算
    /// （SettleBattle）——这是 4E 正式排空闸门接入前的调试口径，闸门四条件/保险丝归 4E。
    /// 场景零脚本铁律：本组件由 SceneFlowDriver 在 Main 激活后运行时装配。
    /// </summary>
    public sealed class BattleRoot : MonoBehaviour
    {
        // 布阵固定格位（玩家半场 4x6；近战贴中线 X3 列、远程 X0~2 纵向展开——左右分 2026-11-04 拍板，预演基准/平衡压测同款格表）
        private static readonly Pos[] FrontCells =
        {
            new Pos(3, 2), new Pos(3, 3), new Pos(3, 1), new Pos(3, 4), new Pos(3, 0), new Pos(3, 5),
            new Pos(2, 0), new Pos(2, 5),
        };
        private static readonly Pos[] BackCells =
        {
            new Pos(2, 2), new Pos(2, 3), new Pos(1, 2), new Pos(1, 3),
            new Pos(2, 1), new Pos(2, 4), new Pos(1, 1), new Pos(1, 4),
            new Pos(0, 2), new Pos(0, 3),
        };

        private GameSession _session;
        private BattlePreviewer _previewer;
        private TimelinePlayer _player;
        private DebugBattleView _view;
        private uint _seed = 20261104u;
        private bool _busy;
        private bool _lastWon;
        private readonly List<string> _log = new List<string>(16);

        // ── 调试观察口（验收脚本只读） ──
        public bool DebugBusy => _busy;
        public int DebugRound => _session != null ? _session.Round : 0;
        public GamePhase DebugPhase => _session != null ? _session.Phase : GamePhase.PreRun;
        public bool DebugRunOver => _session != null && _session.RunOver;

        private void Start()
        {
            var playerGo = new GameObject("TimelinePlayer");
            playerGo.transform.SetParent(transform, false);
            _player = playerGo.AddComponent<TimelinePlayer>();

            var viewGo = new GameObject("DebugBattleView");
            viewGo.transform.SetParent(transform, false);
            _view = viewGo.AddComponent<DebugBattleView>();
            _view.Bind(_player);

            _player.Finished += OnPlaybackFinished;

            NewRun();
        }

        private void OnDestroy()
        {
            if (_player != null) _player.Finished -= OnPlaybackFinished;
        }

        // ─────────────────────── 对局与会话构建 ───────────────────────

        private void NewRun()
        {
            _session = new GameSession(new EventBus(), _seed, GameTables.Default);
            if (!_session.StartRun()) { Log("StartRun 被拒——调试对局建立失败"); return; }

            // L8 满级 + 注资 + 买满备战席（架上任意有货就买——内容随种子，与预演基准同款）
            _session.Eco.AddGold(300);
            while (_session.Eco.Level < 8)
                if (!_session.BuyXp()) break;
            int guard = 0;
            while (_session.Bench.Count < GameSession.BenchCapacity && guard++ < 100)
            {
                int slot = -1;
                for (int i = 0; i < ShopSystem.Slots; i++)
                    if (_session.Shop.Offers[i].Id != 0) { slot = i; break; }
                if (slot < 0) { if (!_session.RefreshShop()) break; continue; }
                if (!_session.Buy(slot)) break;
            }
            Log($"调试对局就绪：种子 {_seed} · L{_session.Eco.Level} · 备战席 {_session.Bench.Count} 人 —— 点「开战」逐场播放");
        }

        // ─────────────────────── 驱动链（面板入口） ───────────────────────

        /// <summary>开战并播放下一场（面板按钮与验收脚本的统一入口）。</summary>
        public void PlayNext()
        {
            if (_busy) { Log("预演/播放中——不可重入"); return; }
            if (_session == null || _session.RunOver) { Log("对局已结束——点「重开一局」"); return; }
            if (_session.Phase == GamePhase.Settlement)
            {
                if (!_session.StartPrep()) { Log("StartPrep 被拒"); return; }
            }
            if (_session.Phase != GamePhase.Prep) { Log($"当前 {_session.Phase} 不可开战"); return; }

            ConsumeGates();
            FillBoard();
            RunPreviewAndPlay();
        }

        private async void RunPreviewAndPlay()
        {
            _busy = true;
            _previewer = new BattlePreviewer();
            if (!_previewer.Start(_session))
            {
                _busy = false;
                Log($"R{_session.Round} 开战被门禁拒（矿未捡完/策略未选完）");
                return;
            }

            Log($"R{_session.Round} 预演中（后台线程）…");
            BattlePreviewResult result;
            try
            {
                result = await _previewer.RunToEndInBackground();
            }
            catch (Exception e)
            {
                _busy = false;
                Log($"预演异常: {e.Message}");
                return;
            }

            // 主线程续接（Unity 同步上下文）——预演期间主线程未触碰会话（#23① 纪律）
            PlannedTimeline timeline = new BattlePlanner().Plan(result.Stream, BuildTimingTable(result));
            Log($"预演完成：{result.TickCount} 步 · 流 {result.Stream.Count} 条 → 时间轴 {timeline.Count} 节点 · Makespan {timeline.Makespan:F1}s");

            if (timeline.Count == 0)
            {
                // 秒胜链：战斗根本没发生——无画面直接结算
                SettleAfterPlayback();
                return;
            }

            _view.BeginBattle(result.Snapshots);   // 开战帧建立方体
            _player.Play(timeline);
            Log($"R{_session.Round} 开始播放（速率旋钮随时可切）");
        }

        private void OnPlaybackFinished()
        {
            SettleAfterPlayback();
        }

        /// <summary>4E 正式闸门接入前的调试口径：播放完成即开闸结算。</summary>
        private void SettleAfterPlayback()
        {
            if (_session != null && _session.Phase == GamePhase.Battle && _session.Combat.IsOver)
            {
                _lastWon = _session.Combat.PlayerWon;
                bool ok = _session.SettleBattle();
                Log(ok
                    ? $"R{_session.Round} 结算完成：{(_lastWon ? "胜" : "败")} → {_session.Phase}"
                    : "结算被拒");
                if (_session.RunOver) Log("对局终局——点「重开一局」换种子再来");
            }
            _previewer?.Release();   // 播完即释放（#23②）——流与快照容量留给下一场
            _previewer = null;
            _busy = false;
        }

        private PlannerTimingTable BuildTimingTable(BattlePreviewResult result)
        {
            // 4C 前全单位共用占位时刻（4B1 拍板：攻 0.45s/50%、施法 0.60s/50%、移动 0.30s、倒地 0.40s）
            var timing = new UnitTiming
            {
                AttackDuration = 0.45f, AttackHitPercent = 0.5f,
                CastDuration = 0.60f, CastHitPercent = 0.5f,
                MoveDuration = 0.30f, DeathDuration = 0.40f,
            };
            var table = new PlannerTimingTable(timing);
            if (result.Snapshots != null && result.Snapshots.Count > 0)
            {
                // 名册展开（占位数据逐单位 Set——4C AnimationDef 数据到位后逐单位替换）
                SnapshotUnit[] units = result.Snapshots[0].Units;
                for (int i = 0; i < units.Length; i++)
                    table.Set(units[i].UnitId, timing);
            }
            return table;
        }

        // ─────────────────────── 备战收尾（预演基准同款） ───────────────────────

        private void ConsumeGates()
        {
            int guard = 0;
            while (_session.OresOnGround.Count > 0)
            {
                if (++guard > 50) { Log("捡矿循环异常长，已防御性跳出"); break; }
                if (!_session.PickOre(_session.OresOnGround[0].Id)) { Log("捡矿被拒，已跳出"); break; }
            }
            if (_session.HasPolicyChoice)
                _session.PickPolicy(0);   // 调试固定选第一格
        }

        private void FillBoard()
        {
            while (_session.PlayerBoard.Count < _session.Eco.Level && _session.Bench.Count > 0)
            {
                Unit u = _session.Bench[0];
                bool ranged = GameTables.Default.Characters.Get(u.CharId).BaseStats.AttackRange > 1;
                bool placed = false;
                foreach (Pos cell in (ranged ? BackCells : FrontCells))
                    if (_session.PlaceOnBoard(u.Id, cell)) { placed = true; break; }
                if (!placed)
                    foreach (Pos cell in (ranged ? FrontCells : BackCells))
                        if (_session.PlaceOnBoard(u.Id, cell)) { placed = true; break; }
                if (!placed) break;
            }
        }

        // ─────────────────────── 调试面板 ───────────────────────

        private void OnGUI()
        {
            var panel = new Rect(12f, 12f, 372f, 320f);
            GUI.Box(panel, "自走棋 · 战台调试（批次4B2）");

            float y = panel.y + 26f;
            if (_session == null)
            {
                GUI.Label(new Rect(panel.x + 16f, y, 340f, 22f), "会话未建立");
                return;
            }

            string state = _session.RunOver
                ? $"对局终局（{( _lastWon ? "胜" : "败" )}）"
                : $"R{_session.Round} · {_session.Phase}";
            GUI.Label(new Rect(panel.x + 16f, y, 340f, 22f), $"状态：{state} · 种子 {_seed}");
            y += 24f;

            // 主按钮
            bool canPlay = !_busy && !_session.RunOver
                && (_session.Phase == GamePhase.Prep || _session.Phase == GamePhase.Settlement);
            GUI.enabled = canPlay;
            if (GUI.Button(new Rect(panel.x + 16f, y, 170f, 30f), "开战（预演+播放）"))
                PlayNext();
            GUI.enabled = true;
            if (_session.RunOver)
            {
                if (GUI.Button(new Rect(panel.x + 196f, y, 160f, 30f), "重开一局（换种子）"))
                {
                    _seed++;
                    _view.BeginBattle(null);
                    _log.Clear();
                    NewRun();
                }
            }
            y += 36f;

            // 速率旋钮（0=暂停 / 0.5 / 1 / 2 同一旋钮）
            GUI.Label(new Rect(panel.x + 16f, y, 120f, 22f), $"速率 ×{_player.Rate:0.0#}");
            y += 22f;
            DrawRateButton(panel.x + 16f, y, "暂停", 0f);
            DrawRateButton(panel.x + 96f, y, "0.5×", 0.5f);
            DrawRateButton(panel.x + 176f, y, "1×", 1f);
            DrawRateButton(panel.x + 256f, y, "2×", 2f);
            y += 30f;

            // 时间轴进度
            float progress = _player.Progress;
            GUI.Box(new Rect(panel.x + 16f, y, 340f, 18f), "");
            GUI.Box(new Rect(panel.x + 16f, y, 340f * progress, 18f), "");
            y += 22f;
            GUI.Label(new Rect(panel.x + 16f, y, 340f, 22f),
                $"播放 {_player.Time:F1}s / {_player.Makespan:F1}s · 节点 {_player.DispatchedCount}/{_player.TotalCount}（封口 {_player.CappedCount}）");
            y += 22f;
            GUI.Label(new Rect(panel.x + 16f, y, 340f, 22f),
                $"场上 {_view.ActorCount} 体（阵亡 {_view.DeadCount}）· 矿 {_view.OreCount} 个");
            y += 26f;

            // 日志尾
            GUI.Label(new Rect(panel.x + 16f, y, 340f, 22f), "—— 运行日志 ——");
            y += 20f;
            int from = Math.Max(0, _log.Count - 7);
            for (int i = from; i < _log.Count; i++)
            {
                GUI.Label(new Rect(panel.x + 16f, y, 340f, 20f), _log[i]);
                y += 19f;
                if (y > panel.yMax - 18f) break;
            }
        }

        private void DrawRateButton(float x, float y, string label, float rate)
        {
            bool active = Mathf.Approximately(_player.Rate, rate);
            GUI.enabled = !active;
            if (GUI.Button(new Rect(x, y, 72f, 26f), active ? $"[{label}]" : label))
                _player.Rate = rate;
            GUI.enabled = true;
        }

        private void Log(string line)
        {
            _log.Add(line);
            Debug.Log($"[BattleRoot] {line}");
        }
    }
}
