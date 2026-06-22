namespace Game.Character
{
    /// <summary>
    /// 弓箭手控制器。本阶段（Archer Phase 1）仅继承 PlayerControllerBase，
    /// 即拥有与战士一致的移动/跳跃/冲刺/坡度能力、但无任何攻击（TryStartAttack 用基类默认 false）。
    /// 普通攻击 / 蓄力重击 / 箭矢系统在 phases 3–4 充实。
    /// </summary>
    public class ArcherController : PlayerControllerBase
    {
    }
}
