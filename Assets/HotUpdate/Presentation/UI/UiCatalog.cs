using System.Collections.Generic;
using AutoChess.Game;
using FairyGUI;

namespace AutoChess.Presentation
{
    /// <summary>
    /// FGUI 包资源目录（批次4U1）：包名与"棋子 id → 立绘资源名"映射的单一真源。
    /// 立绘命名 = 用户 FGUI 包内 role0~role17，与 Res/Art/MainRole 文件夹 0 基映射同款
    /// （roleN ↔ 文件夹 N ↔ CharId N+1，UnitActorConfig 同源约定：角色键 = CharId，素材文件夹 = CharId-1）。
    /// 族名从 SynergyTable 查 DisplayName（表是唯一真源，不在这里复抄中文名）。
    /// </summary>
    public static class UiCatalog
    {
        public const string PackageName = "Package1";

        /// <summary>包的 YooAsset 定位（描述符 = 本定位 + "_fui.bytes" = Assets/Res/UI/Package1_fui.bytes；
        /// 图集等资源按裸名拼回同目录）。HotUpdateEntry 装配 UiRoot 后即 Load——包常驻整局。</summary>
        public const string PackageLocation = "Assets/Res/UI/" + PackageName;

        private static readonly Dictionary<int, string> PortraitUrls = new Dictionary<int, string>(18);

        /// <summary>棋子立绘的包内 url（null = 越界 id；url 为 null/空 = 包内缺资源——调用方置空槽显示）。</summary>
        public static string PortraitUrl(int charId)
        {
            if (charId < 1 || charId > 18) return null;
            if (!PortraitUrls.TryGetValue(charId, out string url))
            {
                url = UIPackage.GetItemURL(PackageName, "role" + (charId - 1));
                PortraitUrls[charId] = url;
            }
            return url;
        }

        /// <summary>羁绊族显示名（查 SynergyTable；未知族返回空串）。</summary>
        public static string FactionName(SynergyId id)
        {
            foreach (SynergyDef def in GameTables.Default.Synergies.All)
                if (def.Id == id) return def.DisplayName;
            return string.Empty;
        }
    }
}
