using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// 外部系统写入元素场的值命令。Projectile 只描述“在哪里、写什么、写多少”，
    /// 不直接取得 Grid 写权限，从而把碰撞时机与固定 Tick 模拟解耦。
    /// </summary>
    public readonly struct ElementWriteRequest
    {
        public readonly Vector3 WorldPosition;
        public readonly ElementMaterialKind MaterialKind;
        public readonly ushort TotalAmount;
        public readonly float Radius;
        public readonly bool UseLinearFalloff;
        public readonly Vector3 InitialVelocity;

        public ElementWriteRequest(
            Vector3 worldPosition,
            ElementMaterialKind materialKind,
            ushort totalAmount,
            float radius,
            bool useLinearFalloff)
            : this(
                worldPosition,
                materialKind,
                totalAmount,
                radius,
                useLinearFalloff,
                Vector3.zero)
        {
        }

        public ElementWriteRequest(
            Vector3 worldPosition,
            ElementMaterialKind materialKind,
            ushort totalAmount,
            float radius,
            bool useLinearFalloff,
            Vector3 initialVelocity)
        {
            WorldPosition = worldPosition;
            MaterialKind = materialKind;
            TotalAmount = totalAmount;
            Radius = radius;
            UseLinearFalloff = useLinearFalloff;
            InitialVelocity = initialVelocity;
        }
    }
}
