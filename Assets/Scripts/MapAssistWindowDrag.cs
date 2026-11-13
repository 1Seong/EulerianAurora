using UnityEngine;
using UnityEngine.EventSystems;

internal sealed class MapAssistWindowDrag : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
{
    private RectTransform _window;
    private RectTransform _bounds;
    private Vector2 _offset;
    private bool _dragging;

    internal void Initialize(RectTransform window)
    {
        _window = window;
        _bounds = (RectTransform)window.parent;
    }

    public void OnBeginDrag(PointerEventData data)
    {
        if (data.button != PointerEventData.InputButton.Left || _window == null) return;
        _dragging = RectTransformUtility.ScreenPointToLocalPointInRectangle(
            _bounds, data.position, data.pressEventCamera, out var point);
        _offset = (Vector2)_window.localPosition - point;
    }

    public void OnDrag(PointerEventData data)
    {
        if (!_dragging || !RectTransformUtility.ScreenPointToLocalPointInRectangle(
            _bounds, data.position, data.pressEventCamera, out var point)) return;
        _window.localPosition = point + _offset;
        ClampToScreen();
    }

    public void OnEndDrag(PointerEventData data) => _dragging = false;
    private void OnDisable() => _dragging = false;
    private void LateUpdate() => ClampToScreen();

    private void ClampToScreen()
    {
        if (_window == null || _bounds == null) return;
        var area = _bounds.rect;
        var size = _window.rect.size;
        var position = _window.localPosition;
        // 화면 밖으로 닫기 버튼이 사라지지 않도록 제한한다. 작은 화면에서는 상단 바를 우선 보존한다.
        position.x = size.x <= area.width
            ? Mathf.Clamp(position.x, area.xMin + size.x * _window.pivot.x, area.xMax - size.x * (1 - _window.pivot.x))
            : area.xMax - size.x * (1 - _window.pivot.x);
        position.y = size.y <= area.height
            ? Mathf.Clamp(position.y, area.yMin + size.y * _window.pivot.y, area.yMax - size.y * (1 - _window.pivot.y))
            : area.yMax - size.y * (1 - _window.pivot.y);
        _window.localPosition = position;
    }
}
