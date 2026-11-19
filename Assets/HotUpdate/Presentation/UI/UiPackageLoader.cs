using System;
using AutoChess.Runtime;
using FairyGUI;
using UnityEngine;

namespace AutoChess.Presentation
{
    /// <summary>
    /// FGUI 包加载器（批次4U0）：UIPackage 经 YooAsset 入货——编辑器模拟与真机同一代码路径
    /// （HotUpdateEntry「取真货」哲学同款：不走 AssetDatabase 直读的编辑器后门）。
    /// 约定（FGUI 编辑器发布侧须对齐，4U1 做界面时一起定稿）：
    ///  · 包产物落 Assets/Res/UI/&lt;包名&gt;/ 目录（收集根内才进 YooAsset 包）；
    ///  · 描述符文件名 = 包名 + "_fui" + ".bytes"（FairyGUI AddPackage(string, LoadResource) 的硬约定）；
    ///  · 图集等资源与描述符同目录、文件名与描述符内登记一致（FGUI 编辑器默认发布即如此）。
    /// location = 描述符的 YooAsset 定位（路径制去扩展名），如 "Assets/Res/UI/StartUI/StartUI"
    /// （对应磁盘文件 Assets/Res/UI/StartUI/StartUI_fui.bytes）。
    /// 句柄不释放（AssetService 会话级缓存语义，同 4C 预载口径）——包卸载与句柄释放是 4U1+ 的事，
    /// 现在（4U0）无包可卸。DestroyMethod.None = 资产生死归 YooAsset 管，FGUI 不插手。
    /// </summary>
    public static class UiPackageLoader
    {
        private const string ConventionRoot = "Assets/Res/UI/";

        /// <summary>加载（或复用）一个 FGUI 包。同 location 重复调用 = AddPackage 内建去重。</summary>
        public static UIPackage Load(string location)
        {
            if (string.IsNullOrEmpty(location)) throw new ArgumentException("location 不能为空", nameof(location));
            if (!location.StartsWith(ConventionRoot, StringComparison.Ordinal))
                Debug.LogWarning($"[UiPackageLoader] 非约定目录的包位置：{location}（约定 = {ConventionRoot}，收集根内才进包）");

            // 图集等资源的裸文件名（如 atlas0）要拼回描述符所在目录——目录从 location 截取
            int slash = location.LastIndexOf('/');
            string folder = location.Substring(0, slash + 1);

            return UIPackage.AddPackage(location, (string name, string extension, Type type, out DestroyMethod destroyMethod) =>
            {
                destroyMethod = DestroyMethod.None;
                // 描述符调用：name = location + "_fui"（AddPackage 内部拼好，已是全路径）；
                // 资源调用：name = 裸文件名（如 atlas0）——拼回同目录即得 YooAsset 定位（去扩展名）
                string loadLocation = name.StartsWith("Assets/", StringComparison.Ordinal)
                    ? name
                    : folder + name;
                return AssetService.LoadAsset(loadLocation);
            });
        }
    }
}
