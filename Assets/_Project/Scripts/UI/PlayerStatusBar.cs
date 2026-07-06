using UnityEngine;
using Game.Core;
using Game.Combat;

namespace Game.UI
{
    public class PlayerStatusBar : MonoBehaviour
    {
        [SerializeField] private HealthComponent _playerHealth;
        [SerializeField] private StatusIconView _burningView;
        [SerializeField] private StatusIconView _wetView;
        [SerializeField] private StatusIconView _poisonedView;
        [SerializeField] private StatusIconView _stickyView;

        private void OnEnable()
        {
            EventBus<StatusChangedEvent>.Subscribe(OnStatusChanged);
        }

        private void OnDisable()
        {
            EventBus<StatusChangedEvent>.Unsubscribe(OnStatusChanged);
        }

        private void Start()
        {
            ClearAll();
        }

        private void OnStatusChanged(StatusChangedEvent e)
        {
            if (_playerHealth == null || e.TargetId != _playerHealth.Id)
                return;

            StatusIconView view = GetView(e.Kind);
            if (view == null)
                return;

            if (e.IsActive)
                view.Set(e.Icon, e.Intensity);
            else
                view.Clear();
        }

        private void ClearAll()
        {
            if (_burningView != null)
                _burningView.Clear();
            if (_wetView != null)
                _wetView.Clear();
            if (_poisonedView != null)
                _poisonedView.Clear();
            if (_stickyView != null)
                _stickyView.Clear();
        }

        private StatusIconView GetView(StatusKind kind)
        {
            switch (kind)
            {
                case StatusKind.Burning:
                    return _burningView;
                case StatusKind.Wet:
                    return _wetView;
                case StatusKind.Poisoned:
                    return _poisonedView;
                case StatusKind.Sticky:
                    return _stickyView;
                default:
                    return null;
            }
        }
    }
}
