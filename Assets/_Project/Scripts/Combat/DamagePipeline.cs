namespace Game.Combat
{
    /// <summary>
    /// 纯函数伤害计算管道。不依赖 MonoBehaviour，可在 EditMode 单测。
    /// 本轮：True 无视防御；Physical/Magical 读取防御档案但暂用 passthrough，
    /// 具体减伤公式留待后续（见 DefenseProfile）。
    /// </summary>
    public static class DamagePipeline
    {
        public static DamageResult Resolve(in DamageRequest req, in DefenseProfile def)
        {
            float final;
            bool mitigated = false;

            switch (req.Type)
            {
                case DamageType.True:
                    final = req.BaseAmount; // 无视防御
                    break;
                case DamageType.Physical:
                    // 预留公式位：例如 final = req.BaseAmount - def.Armor
                    final = req.BaseAmount;
                    break;
                case DamageType.Magical:
                    // 预留公式位：例如 final = req.BaseAmount * (1f - def.MagicResist)
                    final = req.BaseAmount;
                    break;
                default:
                    final = req.BaseAmount;
                    break;
            }

            if (final < 0f) final = 0f; // 结果非负钳制
            return new DamageResult(final, req.Type, mitigated);
        }
    }
}
