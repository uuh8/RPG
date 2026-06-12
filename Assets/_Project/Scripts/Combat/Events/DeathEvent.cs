using UnityEngine;
using Game.Core;

namespace Game.Combat
{
    /// <summary>目标生命值归零后同帧派发。</summary>
    public struct DeathEvent : IGameEvent
    {
        public int TargetId;
        public int AttackerId;
        public Vector3 Position;
    }
}
