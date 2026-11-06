using System;
using UnityEngine;

namespace AutoChess.Presentation
{
    /// <summary>
    /// 序列帧烘焙产物（批次4C，#23⑥ 动画数据契约的「帧序列」那一半）：
    /// 一个单位四个标准动作的帧序列 + 帧率 + 脚底偏移，由编辑期烘焙器（菜单「工具/表现层序列帧烘焙」）
    /// 从素材 .anim 的 m_Sprite 对象曲线提取落盘（Res/Art/UnitActorClips/ 下、随素材目录不入 git）。
    ///
    /// 为什么运行期手工换帧而不是 Animator 直驱（三理由，2026-11-06 方案拍板）：
    /// ①#23 播放器制式=一切姿势按 playhead 参数化（暂停即冻结、倍速零漂移）——Animator 自走表，
    ///   speed 同步只能近似（两侧各自累计浮点，帧抖动下可差一帧）；手工换帧=playhead 的纯函数，天然精确；
    /// ②素材 clip 全部 isLooping=true，Animator 播单次动作会在末尾跳回第一帧（攻击重挥/尸体诈尸），
    ///   改素材文件违背"不动作者素材"纪律；帧表方案播完停帧是天然行为；
    /// ③素材状态机零参数，直驱本就只能是硬切状态——禁用 Animator 零表现力损失、还省组件开销。
    ///
    /// 命中帧百分比/截窗等"规划口径"不在这里——归 UnitActorConfig（同一份数据、两边各算各的另一半）。
    /// 素材热插拔：换素材/修帧序后重跑烘焙菜单即更新本 SO。
    /// </summary>
    public sealed class UnitActorClipSet : ScriptableObject
    {
        /// <summary>一个动作的帧序列（烘焙时按 .anim 时间序原样提取，不截窗——截窗是配置层的口径）。</summary>
        [Serializable]
        public sealed class Clip
        {
            /// <summary>帧序列（逐帧喂 SpriteRenderer.sprite）。烘焙器保证四动作非空（缺失动作用代用帧填充并在烘焙报告标记）。</summary>
            public Sprite[] Frames = Array.Empty<Sprite>();

            /// <summary>原始 clip 时长（秒）。</summary>
            public float Duration;

            /// <summary>帧率 = 帧数 / 时长（烘焙时算好，运行期直接用；素材全库实测约 12）。</summary>
            public float FramesPerSecond;
        }

        public Clip Idle = new Clip();
        public Clip Walk = new Clip();
        public Clip Attack = new Clip();
        public Clip Death = new Clip();

        /// <summary>脚底对齐偏移（世界单位）= idle 首帧 pivot.y / PPU——pivot 在中心的素材（怪物组）
        /// 靠它把「脚」抬回格面上，而不是半个身子陷进地里。</summary>
        public float FeetOffset;

        /// <summary>idle 首帧的视觉高度（世界单位，scale=1 时）——配置表按它归一化各单位的最终缩放。</summary>
        public float BodyHeight = 1f;
    }
}
