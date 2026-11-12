using System;
using YooAsset.Editor;

namespace AutoChess.EditorTools
{
    /// <summary>
    /// 在 NormalIgnoreRule 基础上额外忽略动画控制器资产（.controller / .overrideController）。
    /// 背景：素材包自带的 Xxx.controller 与同目录同名的 Xxx.png / Xxx.anim 在
    /// SupportExtensionless（路径剥扩展名寻址）下地址相同，触发 YooAsset
    /// 「Location already exists」警告每次运行刷屏 22 条；
    /// 本工程表现层为序列帧 + 公告板，运行时从不经 YooAsset 加载 AnimatorController，
    /// 烘焙器走 AssetDatabase，也不依赖清单条目，故可安全排除。
    /// </summary>
    public class IgnoreAnimatorControllerRule : IAssetIgnoreRule
    {
        // 除扩展名白名单外，其余判断与 NormalIgnoreRule 逐条保持一致（组合而非复制）
        private readonly NormalIgnoreRule _normal = new NormalIgnoreRule();

        public bool IsIgnoreAsset(EditorAssetInfo assetInfo)
        {
            string ext = assetInfo.FileExtension;
            if (ext.Equals(".controller", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".overrideController", StringComparison.OrdinalIgnoreCase))
                return true;
            return _normal.IsIgnoreAsset(assetInfo);
        }
    }
}
