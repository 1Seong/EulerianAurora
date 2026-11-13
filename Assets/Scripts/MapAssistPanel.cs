using System;
using System.Threading;
using System.Collections.Generic;
using UnityEngine.EventSystems;
using EulerianAurora.Maps;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

internal sealed class MapAssistPanel : MonoBehaviour
{
    private const string RequestHint = MapTools.RebuildMessage;
    private MapEditor _editor;
    private TMP_FontAsset _font;
    private TMP_Text _buttonLabelStyle;
    private Button _buttonStyle;
    private GameObject _modal;
    private Transform _assistCanvas;
    private Button _open;
    private Button _send;
    private Button _apply;
    private Button _compare;
    private Button _previousButton;
    private TMP_Text _compareLabel;
    private TMP_Text _previousLabel;
    private TMP_Text _feedback;
    private TMP_InputField _input;
    private Toggle _autoGimmicks;
    private Toggle _autoDifficulty;
    private readonly List<Toggle> _gimmickOptions = new();
    private readonly List<Toggle> _difficultyOptions = new();
    private readonly MapGimmick[] _gimmickKinds = { MapGimmick.Portal, MapGimmick.Rotation,
        MapGimmick.Ghost, MapGimmick.Inverter, MapGimmick.Laser, MapGimmick.Dash };
    private readonly Dictionary<GameObject, bool> _hiddenObjects = new();

    private GameObject _previousSelection;
    private CancellationTokenSource _request;
    private MapGenerationResult _latest;
    private MapGenerationResult _previous;
    private bool _viewingPrevious;
    private bool _showingPreview;
    private MapGenerationResult ActiveResult => _viewingPrevious ? _previous : _latest;

    internal bool IsOpen => _modal != null && _modal.activeSelf;

    internal void Initialize(MapEditor editor, Button dockReference, Image panelStyle)
    {
        _editor = editor;
        _buttonStyle = dockReference;
        _buttonLabelStyle = dockReference.GetComponentInChildren<TMP_Text>(true);
        _font = _buttonLabelStyle != null ? _buttonLabelStyle.font : TMP_Settings.defaultFontAsset;
        var dock = (RectTransform)dockReference.transform;
        _open = CreateButton(dock.parent, "맵 보조", Open, out var openLabel,
            dock.sizeDelta.x, dock.sizeDelta.y, dock.anchoredPosition.x - dock.rect.width - 8,
            dock.anchoredPosition.y, dock.anchorMin);
        if (_buttonLabelStyle != null)
            openLabel.fontSize = _buttonLabelStyle.fontSize;

        var root = new GameObject("Map Assist Canvas", typeof(RectTransform), typeof(Canvas),
            typeof(CanvasScaler), typeof(GraphicRaycaster));
        root.transform.SetParent(transform, false);
        _assistCanvas = root.transform;
        root.layer = dock.gameObject.layer;
        var canvas = root.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;
        var scaler = root.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1440, 900);
        scaler.matchWidthOrHeight = 0.5f;

        var backdrop = CreateImage(root.transform, "Map Assist Modal", Color.clear);
        Stretch(backdrop.rectTransform, Vector2.zero, Vector2.zero);
        _modal = backdrop.gameObject;

        var panel = CreateImage(backdrop.transform, "Conversation", panelStyle.color, panelStyle);
        Place(panel.rectTransform, new Vector2(1, 0.5f), 340, 460, -16, 0);
        var body = panel.transform;
        var titleBar = CreateImage(body, "창 이동", panelStyle.color, panelStyle);
        Place(titleBar.rectTransform, Vector2.up, 256, 38, 0, 0);
        titleBar.gameObject.AddComponent<MapAssistWindowDrag>().Initialize(panel.rectTransform);
        var title = CreateLabel(titleBar.transform, "맵 보조 · 드래그 이동", 17);
        Place(title.rectTransform, Vector2.up, 240, 30, 12, -4);
        CreateButton(body, "닫기", Close, out _, 64, 28, -10, -5, Vector2.one);

        var field = CreateImage(body, "Request", panelStyle.color, panelStyle);
        Place(field.rectTransform, Vector2.up, 316, 64, 12, -44);
        var viewport = new GameObject("Viewport", typeof(RectTransform), typeof(RectMask2D));
        viewport.transform.SetParent(field.transform, false);
        var viewportRect = (RectTransform)viewport.transform;
        Stretch(viewportRect, new Vector2(10, 5), new Vector2(-10, -5));
        var inputText = CreateLabel(viewport.transform, "", 18);
        inputText.alignment = TextAlignmentOptions.TopLeft;
        Stretch(inputText.rectTransform, Vector2.zero, Vector2.zero);
        var placeholder = CreateLabel(viewport.transform, "원하는 조건 입력", 18);
        placeholder.alignment = TextAlignmentOptions.TopLeft;
        placeholder.color = new Color(1, 1, 1, 0.55f);
        Stretch(placeholder.rectTransform, Vector2.zero, Vector2.zero);
        _input = field.gameObject.AddComponent<TMP_InputField>();
        _input.targetGraphic = field;
        _input.textViewport = viewportRect;
        _input.textComponent = (TextMeshProUGUI)inputText;
        _input.placeholder = placeholder;
        _input.lineType = TMP_InputField.LineType.MultiLineNewline;
        _input.characterLimit = 1600;

        BuildOptions(body);
        var feedbackArea = CreateImage(body, "결과 안내", Color.clear);
        Place(feedbackArea.rectTransform, Vector2.up, 316, 70, 12, -292);
        feedbackArea.gameObject.AddComponent<RectMask2D>();
        _feedback = CreateLabel(feedbackArea.transform, RequestHint, 15);
        _feedback.alignment = TextAlignmentOptions.TopLeft;
        _feedback.rectTransform.anchorMin = Vector2.up;
        _feedback.rectTransform.anchorMax = Vector2.one;
        _feedback.rectTransform.pivot = Vector2.up;
        _feedback.rectTransform.sizeDelta = Vector2.zero;
        _feedback.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        var feedbackScroll = feedbackArea.gameObject.AddComponent<ScrollRect>();
        feedbackScroll.viewport = feedbackArea.rectTransform;
        feedbackScroll.content = _feedback.rectTransform;
        feedbackScroll.horizontal = false;
        feedbackScroll.movementType = ScrollRect.MovementType.Clamped;
        feedbackScroll.scrollSensitivity = 20;
        _send = CreateButton(body, "후보 생성", Send, out _, 152, 34, 12, 50, Vector2.zero);
        _previousButton = CreateButton(body, "이전 후보", SwitchCandidate, out _previousLabel,
            152, 34, 176, 50, Vector2.zero);
        _compare = CreateButton(body, "원본 보기", Compare, out _compareLabel, 152, 34, -176, 10,
            Vector2.right);
        _apply = CreateButton(body, "후보 적용", Apply, out _, 152, 34, -12, 10, Vector2.right);
        _previousButton.interactable = _compare.interactable = _apply.interactable = false;
        _input.onValueChanged.AddListener(_ =>
        {
            ClearPreview();
            SetFeedback(RequestHint);
        });
        _modal.SetActive(false);
    }

    private void Open()
    {
        if (IsOpen || !_editor.CanOpenAssist)
            return;
        _open.gameObject.SetActive(false);
        _modal.SetActive(true);
        LockOtherUI();
        EventSystem.current?.SetSelectedGameObject(_send.gameObject);
    }

    private void Close()
    {
        _request?.Cancel();
        _request = null;
        ClearPreview();
        _modal.SetActive(false);
        UnlockOtherUI();
        _open.gameObject.SetActive(true);
        _input.interactable = _send.interactable = true;
        RefreshOptions();
        SetFeedback(RequestHint);
    }

    private void OnDisable()
    {
        if (IsOpen)
            Close();
    }

    private async void Send()
    {
        if (_request != null)
            return;
        var requestData = new MapAssistRequest { Operation = MapAssistOperation.CreateMap };
        if (!string.IsNullOrWhiteSpace(_input.text) &&
            !MapAssistRequestInterpreter.TryInterpret(_input.text, out requestData))
        {
            SetFeedback("이 요청은 현재 해석할 수 없어요.\n" + RequestHint);
            return;
        }
        requestData.UiConditions = ReadUiConditions();

        using var request = new CancellationTokenSource();
        _request = request;
        RefreshOptions();
        _input.interactable = _send.interactable = false;
        _previousButton.interactable = _compare.interactable = _apply.interactable = false;
        SetFeedback("요청한 조건에 맞는 배치와 해답을 확인하고 있어요.");
        try
        {
            var result = await _editor.GenerateAssistProposal(requestData, Guid.NewGuid().GetHashCode(), request.Token);
            if (request.IsCancellationRequested)
                return;
            if (result.Candidate == null)
            {
                SetFeedback(_latest == null ? result.Message : result.Message + "\n기존 후보는 계속 볼 수 있어요.");
                return;
            }
            _previous = _latest;
            _latest = result;
            _viewingPrevious = false;
            PresentCandidate();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            if (!request.IsCancellationRequested)
                SetFeedback("후보를 생성하는 중 오류가 발생했어요.");
        }
        finally
        {
            if (_request == request)
            {
                _request = null;
                RefreshOptions();
                _input.interactable = _send.interactable = true;
                _compare.interactable = _apply.interactable = _latest != null;
                _previousButton.interactable = _previous != null;
            }
        }
    }

    private void ClearPreview()
    {
        if (_showingPreview)
            _editor.RenderAssistPreview(null);
        _showingPreview = false;
        _latest = _previous = null;
        _viewingPrevious = false;
        _previousLabel.text = "이전 후보";
        _previousButton.interactable = _compare.interactable = _apply.interactable = false;
    }

    private void SwitchCandidate()
    {
        if (_previous == null)
            return;
        _viewingPrevious = !_viewingPrevious;
        PresentCandidate();
    }

    private void PresentCandidate()
    {
        var result = ActiveResult;
        var candidate = result.Candidate;
        _previousLabel.text = _viewingPrevious ? "최신 후보" : "이전 후보";
        SetFeedback(result.Message);
        ShowPreview(true);
    }

    private void Compare() => ShowPreview(!_showingPreview);

    private void ShowPreview(bool show)
    {
        var candidate = ActiveResult?.Candidate;
        if (candidate == null)
            return;
        _showingPreview = show;
        _editor.RenderAssistPreview(_showingPreview ? candidate : null);
        _compareLabel.text = _showingPreview ? "원본 보기" : "후보 보기";
    }

    private void Apply()
    {
        var candidate = ActiveResult?.Candidate;
        if (candidate == null)
            return;
        _editor.ApplyAssistProposal(candidate);
        _showingPreview = false;
        Close();
    }

    private void SetFeedback(string message)
    {
        _feedback.text = message;
        _feedback.rectTransform.anchoredPosition = Vector2.zero;
    }
    private void BuildOptions(Transform body)
    {
        var title = CreateLabel(body, "허용 기믹", 20);
        Place(title.rectTransform, Vector2.up, 130, 26, 12, -120);
        _autoGimmicks = CreateToggle(body, "기믹 자동", 220, -120, true);
        string[] names = { "포탈", "회전", "고스트", "인버터", "레이저", "대시" };
        for (int i = 0; i < names.Length; i++)
            _gimmickOptions.Add(CreateToggle(body, names[i], 12 + i % 3 * 108, -154 - i / 3 * 32, true));
        title = CreateLabel(body, "난이도", 20);
        Place(title.rectTransform, Vector2.up, 130, 26, 12, -226);
        _autoDifficulty = CreateToggle(body, "난이도 자동", 220, -226, true);
        var group = new GameObject("난이도 단일 선택", typeof(RectTransform), typeof(ToggleGroup));
        group.transform.SetParent(body, false);
        var toggleGroup = group.GetComponent<ToggleGroup>();
        toggleGroup.allowSwitchOff = false;
        string[] levels = { "쉬움", "보통", "어려움" };
        for (int i = 0; i < levels.Length; i++)
        {
            var toggle = CreateToggle(body, levels[i], 12 + i * 108, -260, i == 1);
            toggle.group = toggleGroup;
            _difficultyOptions.Add(toggle);
        }
        RefreshOptions();
    }

    private Toggle CreateToggle(Transform parent, string text, float x, float y, bool initial)
    {
        var row = CreateImage(parent, text, Color.clear);
        Place(row.rectTransform, Vector2.up, 108, 28, x, y);
        var style = (Image)_buttonStyle.targetGraphic;
        var box = CreateImage(row.transform, "선택 상자", new Color(0.62f, 0.60f, 0.78f));
        Place(box.rectTransform, Vector2.up, 20, 20, 0, -4);
        var boxFill = CreateImage(box.transform, "선택 상자 배경", style.color, style);
        Stretch(boxFill.rectTransform, new Vector2(1, 1), new Vector2(-1, -1));
        var mark = CreateImage(box.transform, "선택 표시", new Color(0.85f, 0.85f, 1f));
        Stretch(mark.rectTransform, new Vector2(4, 4), new Vector2(-4, -4));
        mark.raycastTarget = false;
        var label = CreateLabel(row.transform, text, 15);
        Place(label.rectTransform, Vector2.up, 84, 28, 24, 0);
        var toggle = row.gameObject.AddComponent<Toggle>();
        toggle.targetGraphic = boxFill;
        toggle.graphic = mark;
        toggle.colors = _buttonStyle.colors;
        toggle.SetIsOnWithoutNotify(initial);
        row.gameObject.AddComponent<CanvasGroup>();
        toggle.onValueChanged.AddListener(_ => ConditionsChanged());
        return toggle;
    }

    internal GenerationUiSettings ReadUiConditions()
    {
        var allowed = MapGimmick.None;
        for (int i = 0; i < _gimmickOptions.Count; i++)
            if (_gimmickOptions[i].isOn) allowed |= _gimmickKinds[i];
        MapDifficulty difficulty = MapDifficulty.Medium;
        for (int i = 0; i < _difficultyOptions.Count; i++)
            if (_difficultyOptions[i].isOn) difficulty = (MapDifficulty)(i + 1);
        // 수동 선택값은 보관하되, 자동 여부를 함께 전달하여 조건 해석 단계에서 제외한다.
        return new GenerationUiSettings
        {
            AutomaticGimmicks = _autoGimmicks.isOn,
            AllowedGimmicks = allowed,
            AutomaticDifficulty = _autoDifficulty.isOn,
            Difficulty = difficulty
        };
    }

    private void ConditionsChanged()
    {
        RefreshOptions();
        if (_feedback == null) return;
        ClearPreview();
        SetFeedback(RequestHint);
    }

    private void RefreshOptions()
    {
        if (_autoGimmicks == null || _autoDifficulty == null) return;
        _autoGimmicks.interactable = _autoDifficulty.interactable = _request == null;
        foreach (var toggle in _gimmickOptions) SetEnabled(toggle, !_autoGimmicks.isOn && _request == null);
        foreach (var toggle in _difficultyOptions) SetEnabled(toggle, !_autoDifficulty.isOn && _request == null);
        void SetEnabled(Toggle toggle, bool enabled)
        {
            toggle.interactable = enabled;
            toggle.GetComponent<CanvasGroup>().alpha = enabled ? 1f : 0.4f;
        }
    }

    private void LockOtherUI()
    {
        _previousSelection = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
        // 보조 창과 에디터의 실행 경로는 유지하고 나머지 UI 오브젝트를 숨긴다.
        foreach (var canvas in FindObjectsByType<Canvas>(FindObjectsInactive.Include))
        {
            if (canvas.rootCanvas != canvas || canvas.transform == _assistCanvas) continue;
            HideBranch(canvas.transform);
        }
    }

    private void HideBranch(Transform branch)
    {
        if (branch == _assistCanvas) return;
        if (transform.IsChildOf(branch) || _modal.transform.IsChildOf(branch))
        {
            // 에디터가 Canvas 아래에 있어도 자기 자신을 비활성화하지 않는다.
            for (int i = 0; i < branch.childCount; i++)
                if (branch.GetChild(i) is RectTransform) HideBranch(branch.GetChild(i));
            return;
        }
        _hiddenObjects.TryAdd(branch.gameObject, branch.gameObject.activeSelf);
        branch.gameObject.SetActive(false);
    }

    private void UnlockOtherUI()
    {
        foreach (var pair in _hiddenObjects)
            if (pair.Key != null) pair.Key.SetActive(pair.Value);
        _hiddenObjects.Clear();
        EventSystem.current?.SetSelectedGameObject(
            _previousSelection != null && _previousSelection.activeInHierarchy ? _previousSelection : null);
        _previousSelection = null;
    }

    private Button CreateButton(Transform parent, string text, UnityEngine.Events.UnityAction action,
        out TMP_Text label, float width, float height, float x, float y, Vector2 anchor)
    {
        var style = (Image)_buttonStyle.targetGraphic;
        var image = CreateImage(parent, text, style.color, style);
        Place(image.rectTransform, anchor, width, height, x, y);
        var button = image.gameObject.AddComponent<Button>();
        button.targetGraphic = image;
        button.transition = _buttonStyle.transition;
        button.colors = _buttonStyle.colors;
        button.spriteState = _buttonStyle.spriteState;
        button.onClick.AddListener(action);
        label = CreateLabel(image.transform, text, 18);
        if (_buttonLabelStyle != null)
        {
            label.fontSharedMaterial = _buttonLabelStyle.fontSharedMaterial;
            label.fontStyle = _buttonLabelStyle.fontStyle;
            label.color = _buttonLabelStyle.color;
        }
        label.alignment = TextAlignmentOptions.Center;
        Stretch(label.rectTransform, Vector2.zero, Vector2.zero);
        return button;
    }

    private TMP_Text CreateLabel(Transform parent, string text, float size)
    {
        var label = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI))
            .GetComponent<TextMeshProUGUI>();
        label.transform.SetParent(parent, false);
        label.font = _font;
        label.fontSize = size;
        label.text = text;
        label.raycastTarget = false;
        return label;
    }

    private static Image CreateImage(Transform parent, string name, Color color, Image style = null)
    {
        var image = new GameObject(name, typeof(RectTransform), typeof(Image)).GetComponent<Image>();
        image.transform.SetParent(parent, false);
        image.gameObject.layer = parent.gameObject.layer;
        image.color = color;
        if (style != null)
        {
            image.material = style.material;
            image.sprite = style.sprite;
            image.type = style.type;
            image.pixelsPerUnitMultiplier = style.pixelsPerUnitMultiplier;
        }
        return image;
    }

    private static void Place(RectTransform rect, Vector2 anchor, float width, float height, float x, float y)
    {
        rect.anchorMin = rect.anchorMax = rect.pivot = anchor;
        rect.sizeDelta = new Vector2(width, height);
        rect.anchoredPosition = new Vector2(x, y);
    }

    private static void Stretch(RectTransform rect, Vector2 min, Vector2 max)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = min;
        rect.offsetMax = max;
    }
}
