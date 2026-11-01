using System;

namespace AutoChess.Game
{
    /// <summary>
    /// 技能目标选择规则（7c-2 扩容：v1 只有 NearestEnemy，8 值定案；封闭集合 - enum，P3-5 划线原则。
    /// 枚举加值会改序列化口径，所以仍然不预占"以后可能用"的位——需要时再加）。
    ///
    /// 门禁分两类（7c-2 拍板）：
    ///   距离过滤类（NearestEnemy/LowestHpEnemy/NearestEnemyShape）：
    ///     目标/锚点必须落在施法距离内，否则不施法不扣蓝（沿用 v1"够不着白等"纪律）；
    ///   形状即门禁（SelfShape）：不看施法距离——形状就是触及范围（CastRange 字段不消费，填 1），
    ///     形状内无敌人 = 不施法不扣蓝（防"触发距离填大了永远空放"）。
    /// 全局前置（所有规则共用）：敌方场上必须还有人——清场后的余势行动位不再施法。
    /// 友方目标规则（Self/Ally 两类/AllAllies）无距离门：蓝满即放（buff 可能在赶路途中施放，
    /// 时长按 4~6 秒对冲；敌方在场由全局前置兜底）。
    /// </summary>
    public enum SkillTargetRule
    {
        /// <summary>单体：最近的存活敌人（切比雪夫距离最小，与 CombatSystem 的索敌同套规则；并列取扫描序先到）。</summary>
        NearestEnemy = 0,

        /// <summary>单体：当前生命最低的敌人（施法距离内筛选；并列取扫描序先到）——收割残血语义。</summary>
        LowestHpEnemy = 1,

        /// <summary>群体：以最近的敌人为锚点，按形状遍历形状内全部敌人（锚点必在形状中心，至少命中锚点）。</summary>
        NearestEnemyShape = 2,

        /// <summary>群体：以自身为锚点，按形状遍历敌人（自身中心形状/整行/整列/十字都走它）。</summary>
        SelfShape = 3,

        /// <summary>自我 buff：目标 = 施法者自己（1~2 费纯 buff 技能的主规则）。</summary>
        Self = 4,

        /// <summary>友方单体：攻击最高的友方（含自身；并列取扫描序先到）——"递刀给主C"语义。</summary>
        AllyHighestAttack = 5,

        /// <summary>友方单体：生命比例最低的友方（含自身；整数交叉相乘比较不做浮点除法，比例并列取扫描序先到）——治疗语义。</summary>
        AllyLowestHpRatio = 6,

        /// <summary>友方全体：施法者一侧全部存活单位（含自身，行主序）——团队 buff 语义。</summary>
        AllAllies = 7,
    }

    /// <summary>
    /// 技能作用形状（7c-2）。封闭集合 - enum（P3-5）；
    /// 全部矩形类形状（3x3/2x2/2x3/3x2/6x6/多行多列）都由 Box + 宽高两字段参数化表达，不再逐个开枚举位。
    /// 形状遍历只收"敌方"单位——友方永不因形状被误伤（构造上就不进名单）。
    /// </summary>
    public enum SkillShape
    {
        /// <summary>单体/无形状（单体与友方规则用；形状规则带它 = 建表事故，构造校验拒）。</summary>
        Single = 0,

        /// <summary>矩形：宽 ShapeWidth 列 x 高 ShapeHeight 行，锚点为中心。奇数维完全对称；
        /// 偶数维向右/向下多占一格（确定性约定，"向下"= 行号增大 = 玩家视角的敌方方向）。
        /// 棋盘边缘自然截断（IsInShape 判区间，越界格本就不存在单位）。</summary>
        Box = 1,

        /// <summary>锚点所在整行（全宽）——近战"剑刃"。</summary>
        FullRow = 2,

        /// <summary>锚点所在整列（全高）。</summary>
        FullColumn = 3,

        /// <summary>整行 + 整列并集（十字）。</summary>
        FullCross = 4,

        /// <summary>施法者视角的敌方半场全部格子（玩家施法 - row 3~5，敌方施法 - row 0~2）。
        /// v1 编表未用，语义先钉死防将来歧义（要用"己方半场"时再加枚举值，不预占）。</summary>
        HalfBoard = 5,
    }

    /// <summary>
    /// 表现层交付标记（7c-2 拍板"SkillDef 现在就留 范围/弹道/瞬发 标记字段"）。
    /// 逻辑层只存不消费；阶段4 规划器按 #23⑥ 动画数据契约读取
    /// （弹速/出弹帧百分比/范围演出的具体参数在表现侧配置，本字段只做"播哪种演出"的路由）。
    /// </summary>
    public enum SkillDelivery
    {
        /// <summary>瞬发：近战斩击 / buff 光环——出手即结算，无飞行物。</summary>
        Instant = 0,

        /// <summary>弹道：远程技能——表现层要出弹帧 + 飞行，在途弹计入排空条件（#23④ 排空闸门）。</summary>
        Projectile = 1,

        /// <summary>范围：地面效果（践踏/裂地类）——表现层播范围演出。</summary>
        Area = 2,
    }

    /// <summary>附带 Buff 的挂载目标（7c-2）。与 BuffId 双向绑定（有 BuffId 必挂目标、挂目标必有 BuffId，表校验锁）。</summary>
    public enum SkillBuffTarget
    {
        /// <summary>无附带 Buff。</summary>
        None = 0,

        /// <summary>挂给本次施法的作用名单：伤害技能 = 受击敌人，友方规则 = 选中的友方。
        /// 被击杀者不挂（毒不上尸体）；名单在施法循环里按行主序产生。</summary>
        Targets = 1,
    }

    /// <summary>
    /// 技能定义（表的每一行）。readonly struct——定义一旦入库永不修改。
    ///
    /// 伤害口径（决策 #15）：
    ///   技能伤害 = 基础值(按星级) + 法强系数 x 法强/100 + 攻击力系数 x 攻击力/100。
    ///   法强基准 100（= 技能基准威力；装备/羁绊按百分点增减）。
    ///   系数 150 = 150% 法强。法强系数与攻击力系数都为 0 的技能 = 纯固定值技能。
    ///
    /// 纯 buff 技能（7c-2）：三档基础值与两系数全 0 = 无伤害段（HasDamageSegment = false）——
    ///   施法循环跳过掷骰与伤害结算，只发 SkillCastEvent + 挂 Buff + 清蓝。
    ///   1~2 费"没有攻击技能、单纯给自己或我方上 buff"（7c-2 拍板）。
    ///   纯 buff 行的 DamageType 不消费（编表统一填 Physical 占位）。
    ///
    /// 施法消耗口径（2026-10-16 拍板）：
    ///   消耗 = 该单位的蓝条上限（MaxMana），施法后清零——所以"消耗"不进技能表：
    ///   每个单位的施法阈值就是自己的上限（同一个字段），技能表另设一份消耗=两套真相。
    ///   KeepManaAfterCast 是"保留型"预留位（施法后不清零），v1 全为 false。
    ///
    /// 暴击口径（决策 #10）：只有物理伤害可暴击——由 DamageInput.CanCrit 表达，
    ///   技能表不另设"可暴击"开关（那会和伤害类型打架）。
    ///   群体物理技能一次掷骰、全部目标共享暴击结果（7c-2 拍板——RollCount 口径"一次施法至多掷一次"）。
    /// </summary>
    public readonly struct SkillDef
    {
        /// <summary>技能 id（>0；0 = 无技能。开放内容表 - int，见 RoleId 的划线注释）。
        /// 7c-2 起与棋子 id 对齐（技能 1~18 = 棋子 1~18），自解释且测试引用稳定。</summary>
        public readonly int Id;

        /// <summary>仅显示用（禁止当键）。</summary>
        public readonly string DisplayName;

        /// <summary>伤害类型（物理走护甲 / 魔法走法抗 / 真实不打折；纯 buff 行不消费）。</summary>
        public readonly DamageType DamageType;

        /// <summary>1 星基础值（固定加值，不含系数段）。</summary>
        public readonly int BaseDamage1;

        /// <summary>2 星基础值。</summary>
        public readonly int BaseDamage2;

        /// <summary>3 星基础值。</summary>
        public readonly int BaseDamage3;

        /// <summary>法强系数（150 = 150% 法强；0 = 不吃法强）。</summary>
        public readonly int AbilityPowerCoefficient;

        /// <summary>攻击力系数（150 = 150% 攻击力；0 = 不吃攻击力）。</summary>
        public readonly int AttackPowerCoefficient;

        /// <summary>施法距离（格）。距离过滤类规则的目标/锚点门槛；
        /// SelfShape 与友方目标规则不消费（填 1，见 SkillTargetRule 注释）。</summary>
        public readonly int CastRange;

        /// <summary>目标选择规则（7c-2 扩为 8 值）。</summary>
        public readonly SkillTargetRule TargetRule;

        /// <summary>施法后是否保留当前蓝量（保留型技能；v1 全 false = 施法后清零）。</summary>
        public readonly bool KeepManaAfterCast;

        // ── 7c-2 新增字段 ──

        /// <summary>作用形状（形状规则的必备项；单体/友方规则必须 Single，表校验锁）。</summary>
        public readonly SkillShape Shape;

        /// <summary>Box 形状宽（列数；仅 Shape = Box 时消费，其余形状不消费）。</summary>
        public readonly int ShapeWidth;

        /// <summary>Box 形状高（行数；仅 Shape = Box 时消费）。</summary>
        public readonly int ShapeHeight;

        /// <summary>表现层交付标记（瞬发/弹道/范围）——逻辑层只存不消费。</summary>
        public readonly SkillDelivery Delivery;

        /// <summary>附带 Buff id（0 = 无；指向 BuffTable）。</summary>
        public readonly int BuffId;

        /// <summary>附带 Buff 的挂载目标（与 BuffId 双向绑定）。</summary>
        public readonly SkillBuffTarget BuffTarget;

        public SkillDef(int id, string displayName, DamageType damageType,
            int baseDamage1, int baseDamage2, int baseDamage3,
            int abilityPowerCoefficient, int attackPowerCoefficient,
            int castRange, SkillTargetRule targetRule, bool keepManaAfterCast,
            SkillShape shape = SkillShape.Single, int shapeWidth = 0, int shapeHeight = 0,
            SkillDelivery delivery = SkillDelivery.Instant,
            int buffId = 0, SkillBuffTarget buffTarget = SkillBuffTarget.None)
        {
            Id = id;
            DisplayName = displayName ?? "";
            DamageType = damageType;
            BaseDamage1 = baseDamage1;
            BaseDamage2 = baseDamage2;
            BaseDamage3 = baseDamage3;
            AbilityPowerCoefficient = abilityPowerCoefficient;
            AttackPowerCoefficient = attackPowerCoefficient;
            CastRange = castRange;
            TargetRule = targetRule;
            KeepManaAfterCast = keepManaAfterCast;
            Shape = shape;
            ShapeWidth = shapeWidth;
            ShapeHeight = shapeHeight;
            Delivery = delivery;
            BuffId = buffId;
            BuffTarget = buffTarget;
        }

        /// <summary>是否携带伤害段（三档基础值与两系数全 0 = 纯 buff 技能，施法循环据此跳过掷骰与伤害结算）。</summary>
        public bool HasDamageSegment
            => BaseDamage1 > 0 || BaseDamage2 > 0 || BaseDamage3 > 0
            || AbilityPowerCoefficient > 0 || AttackPowerCoefficient > 0;

        /// <summary>按星级取基础值。星级越界抛异常——星级是受控输入（合成链产出），不是外部数据。</summary>
        public int BaseDamageForStar(int star)
        {
            switch (star)
            {
                case 1: return BaseDamage1;
                case 2: return BaseDamage2;
                case 3: return BaseDamage3;
                default: throw new ArgumentOutOfRangeException(nameof(star), "星级必须为 1~3");
            }
        }
    }
}
