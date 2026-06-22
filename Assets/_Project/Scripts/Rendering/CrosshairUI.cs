using UnityEngine;
using Game.Core;

namespace Game.Rendering
{
    /// <summary>
    /// 准心 UI：订阅 AimStateChangedEvent，按瞄准状态显隐准心 GameObject。
    /// 经 EventBus 与 gameplay 解耦——不引用 Game.Character。SetActive 一个 GameObject，无需 UGUI 程序集引用。
    /// </summary>
    public class CrosshairUI : MonoBehaviour
    {
        [SerializeField] private GameObject _crosshair; // 准心根物体（默认隐藏）

        private void Awake()
        {
            if (_crosshair != null) _crosshair.SetActive(false);
        }

        private void OnEnable()  => EventBus<AimStateChangedEvent>.Subscribe(OnAimStateChanged);
        private void OnDisable() => EventBus<AimStateChangedEvent>.Unsubscribe(OnAimStateChanged);

        private void OnAimStateChanged(AimStateChangedEvent e)
        {
            if (_crosshair != null) _crosshair.SetActive(e.Active);
        }
    }
}
