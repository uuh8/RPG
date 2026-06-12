using UnityEngine;

namespace Game.Combat
{
    /// <summary>
    /// 【临时验证脚本】站位代替 Character 侧的攻击激活窗口驱动：
    /// 每隔 _interval 秒自动开启窗口 _windowDuration 秒，用于验证 MeleeHitDetector。
    /// 也提供 Inspector 右键菜单手动开/关。真正的窗口驱动（数据驱动 normalized-time）
    /// 由 Character 侧实现，不在本轮。
    /// </summary>
    public class MeleeSwingTestDriver : MonoBehaviour
    {
        [SerializeField] private MeleeHitDetector _detector;
        [SerializeField] private float _windowDuration = 0.3f;
        [SerializeField] private float _interval = 2f;

        private float _timer;
        private bool _open;

        private void Update()
        {
            if (_detector == null) return;

            _timer += Time.deltaTime;
            if (!_open && _timer >= _interval)
            {
                _detector.OpenHitWindow();
                _open = true;
                _timer = 0f;
            }
            else if (_open && _timer >= _windowDuration)
            {
                _detector.CloseHitWindow();
                _open = false;
                _timer = 0f;
            }
        }

        [ContextMenu("Open Hit Window")]
        private void DebugOpen()
        {
            if (_detector != null) _detector.OpenHitWindow();
        }

        [ContextMenu("Close Hit Window")]
        private void DebugClose()
        {
            if (_detector != null) _detector.CloseHitWindow();
        }
    }
}
