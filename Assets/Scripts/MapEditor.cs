using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using com.example;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

public class MapEditor : MonoBehaviour
{
    public int cubeSize = 10;
    private char[,,] _map;
    private MapCreating _currentMapCreating;
    public static MapEditor Instance;
    [SerializeField] private char currentTile = (char)TileType.Road;
    public char CurrentTile
    {
        get => currentTile;
        set
        {
            if (currentTile == (char)TileType.PortalOut && value !=  (char)TileType.PortalIn)
            {
                _tiles[_previousPortalInPos.x, _previousPortalInPos.y, _previousPortalInPos.z].SimpleRender((char)TileType.Empty);
                PortalEditing = false;
            }
            currentTile = value;
        }
    }

    private Vector3Int _previousPortalInPos;
    private bool _portalEditing = false;
    public bool PortalEditing
    {
        set
        {
            if (value)
                cam.cullingMask &= ~(1 << LayerMask.NameToLayer("UI"));
            else
                cam.cullingMask |= (1 << LayerMask.NameToLayer("UI"));
            _portalEditing = value;
        }
    }
    private Dictionary<Vector3Int, Vector3Int> _portalPairDict = new();

    [SerializeField] private Transform playerModel;
    [SerializeField] private Transform  cubeParent;
    [SerializeField] private GameObject tileIndicatorParent;
    [SerializeField] private GameObject[] indicators;
    [SerializeField] private GameObject rightSideButtonsParent;
    [SerializeField] private Button exportButton;
    [SerializeField] private GameObject linePrefab;
    public LineRenderer currentLine;
    [SerializeField] private Camera cam;
    [SerializeField] private Camera thumbnailCamera;
    private PuzzleTile[,,] _tiles;
    private bool _isValidated;
    private CancellationTokenSource _autoTestCancellation;
    
    [SerializeField] private Button showAnswerButton;
    [SerializeField] private GameObject stopShowAnswerButton;
    [SerializeField] private Transform ghostPlayer;
    [SerializeField] private float showAnswerTimePerPos = 0.5f;
    private CancellationTokenSource _showAnswerCts;
    private Stack<Vector3Int> _answer;
    private bool _isShowingAnswer = false;
    [SerializeField] private GameObject escPanel;
    [SerializeField] private GameObject escMenuPanel;
    [SerializeField] private GameObject resetPanel;
    [SerializeField] private GameObject resetCheckPanel;
    [SerializeField] private GameObject returnCheckPanel;
    public bool IsTesting = false;

    private int _selectedAxis = 0;
    public int SelectedAxis // 툴에서 선택된 축
    {
        get => _selectedAxis;
        set
        {
            foreach(var i in indicators)
                i.SetActive(false);
            if (value == 0) // 타일 모드이면 다시 툴셋 패널 활성화
                leftPanel.SetActive(true);
            else
                leftPanel.SetActive(false);
            indicators[value].SetActive(true);
            _selectedAxis = value; 
        }
    }
    private int _rotAxis = 0;
    private int RotAxis
    {
        set
        {
            if (_rotAxis != value) // 다른 축으로 전환
            {
                if (_rotAxis != 0) // 이전에 있던 축이 빈 축이 아니면 정리
                {
                    for (int i = 0; i != cubeSize; ++i)
                        _canRotate[i] = false;
                    LayerRender();
                }
                foreach (var i in innerCubes)
                    i.gameObject.SetActive(false);
                innerCubes[value].gameObject.SetActive(true);
            }
            _rotAxis = value;
        }
    }
    private bool[] _canRotate;
    [SerializeField] private GameObject leftPanel;
    [SerializeField] private Transform[] innerCubes;
    [SerializeField] private Button undoButton;
    
    private struct MapState
    {
        public char[,,] Map;
        public Vector3 PlayerPos;
        public bool PlayerActive;
    }

    private readonly Stack<MapState> _undoStack = new();
    
    private void SaveUndoState()
    {
        // 얕은 복사가 아닌 깊은 복사 필수
        var snapshot = (char[,,])_map.Clone();
        _undoStack.Push(new MapState()
            { Map = snapshot, PlayerPos = playerModel.position, PlayerActive = playerModel.gameObject.activeSelf});
        undoButton.interactable = true;
    }

    public void Undo(bool doRender = true)
    {
        if (_undoStack.Count == 0) return;
        
        AudioManager.Instance.PlaySFX(AudioManager.SFXType.Undo);
        SetValidated(false);
        var s = _undoStack.Pop();
        _map = s.Map;
        playerModel.position = s.PlayerPos;
        playerModel.gameObject.SetActive(s.PlayerActive);
        PortalEditing = false;
        
        if(CurrentTile == (char)TileType.PortalOut)
            CurrentTile = (char)TileType.PortalIn;

        if (doRender)
            Render();

        if (_undoStack.Count == 0)
            undoButton.interactable = false;
    }
    
    private void Render()
    {
        for (var i = 0; i != cubeSize; ++i)
        for (var j = 0; j != cubeSize; ++j)
        for (var k = 0; k != cubeSize; ++k)
        {
            var c = _map[i, j, k];
            _tiles[i, j, k].SimpleRender(c);
        }
    }

    private void LayerRender()
    {
        for (int i = 1; i != cubeSize-1; ++i)
                innerCubes[_rotAxis].GetChild(i).GetChild(0).gameObject.SetActive(_canRotate[i]);
    }

    private void Awake()
    {
        if(Instance != null && Instance != this)
            Destroy(gameObject);
        else
        {
            Instance = this;
        }

        showAnswerButton.onClick.AddListener(() => ShowAnswer().Forget());
        
        _tiles = new PuzzleTile[cubeSize, cubeSize, cubeSize];
        int x = 0;
        foreach (Transform plane in cubeParent) // 평면 (10개)
        {
            int y = 0;
            foreach (Transform row in plane) // 줄 (10개)
            {
                int z = 0;
                foreach (Transform cell in row) // 칸 (10개)
                {
                    _tiles[x, y, z] = cell.GetComponent<PuzzleTile>();
                    z++;
                }
                y++;
            }
            x++;
        }

        _canRotate = new bool[cubeSize];
        
        SetValidated(false);

        if (_map is null)
        {
            _map = new char[cubeSize, cubeSize, cubeSize];
            for (int i = 0; i != cubeSize; ++i)
            for (int j = 0; j != cubeSize; ++j)
            for (int k = 0; k != cubeSize; ++k)
                _map[i, j, k] = (char)TileType.Empty;
        }
        
        Initialize(GameManager.Instance.GetMapCreating());
    }

    private void OnDisable()
    {
        _autoTestCancellation?.Cancel();
    }

    private void OnDestroy()
    {
        if(Instance == this)
            Instance = null;
    }

    public void ResetEditor()
    {
        AudioManager.Instance.PlaySFX(AudioManager.SFXType.MapRemove);
        SetValidated(false);
        
        playerModel.gameObject.SetActive(false);
        
        for (int i = 0; i != cubeSize; ++i)
        for (int j = 0; j != cubeSize; ++j)
        for (int k = 0; k != cubeSize; ++k)
            _map[i, j, k] = (char)TileType.Empty;

        _portalPairDict = new();
        RotAxis = 0;
        Render();
    }

    public void Initialize(MapCreating mapCreating) // 외부에서 데이터 넣어주고 초기화
    {
        if (mapCreating == null) return;
        SetValidated(false);
        
        _currentMapCreating = mapCreating;
        if(_currentMapCreating.Data != null)
            _map = StringHelper.DecodeCube(_currentMapCreating.Data);
        if (_currentMapCreating.PortalPairs != null)
        {
            var portalPairList = PortalPairHelper.Decode(_currentMapCreating.PortalPairs);
            _portalPairDict = PortalPairHelper.ToDict(portalPairList);

            foreach (var i in portalPairList)
            {
                var l = Instantiate(linePrefab, _tiles[i.InPos.x, i.InPos.y, i.InPos.z].transform)
                    .GetComponent<LineRenderer>();
                l.SetPosition(0, i.InPos);
                l.SetPosition(1, i.OutPos);
            }
        }
        if (_currentMapCreating.RotInfo != null)
        {
            var rotInfo = RotateHelper.Decode(_currentMapCreating.RotInfo);
            RotAxis = rotInfo.Axis;
            _canRotate = rotInfo.Layers;
        }
        for (var i = 0; i < cubeSize; ++i)
        for (var j = 0; j < cubeSize; ++j)
        for (var k = 0; k < cubeSize; ++k)
        {
            var c = _map[i, j, k];
            switch (c)
            {
                case (char)TileType.Player:
                    playerModel.gameObject.SetActive(true);
                    playerModel.position = new Vector3(i, j, k);
                    break;
            }
        }
        if(_rotAxis != 0)
            LayerRender();
        Render();
    }

    public void SetTile(int layer, int row, int col)
    {
        if (_portalEditing && _previousPortalInPos == new Vector3Int(layer, row, col))
        {
            PopUpManager.Instance.Show("다른 위치를 지정해주세요");
            return;
        }
        
        if(!_portalEditing)
            SaveUndoState();
        //Debug.Log(layer + " " + row + " " + col);
        SetValidated(false);
        AudioManager.Instance.PlaySFX(currentTile == (char)TileType.Empty ? 
            AudioManager.SFXType.TileDelete : AudioManager.SFXType.TileLoad);
        
        if(playerModel.position == new Vector3(layer, row, col))
        {
            playerModel.gameObject.SetActive(false);
        }
        else if (_portalPairDict.ContainsKey(new Vector3Int(layer, row, col)))
        {
            var key = new Vector3Int(layer, row, col);
            var val = _portalPairDict[key];
            _portalPairDict.Remove(key);
            _portalPairDict.Remove(val);
            _map[val.x, val.y, val.z] = (char)TileType.Empty;
            _tiles[val.x, val.y, val.z].SimpleRender((char)TileType.Empty);
        }

        switch (currentTile)
        {
            case (char)TileType.Player:
                if (!playerModel.gameObject.activeSelf)
                {
                    playerModel.gameObject.SetActive(true);
                }
                else // 이전 위치 받아와서 지움, 같은 위치 지정할 경우 주의
                {
                    var pos = playerModel.position;
                    _map[(int)pos.x, (int)pos.y, (int)pos.z] = (char)TileType.Empty;
                }
            
                playerModel.position = new Vector3(layer, row, col);
                _tiles[layer, row, col].SimpleRender((char)TileType.Empty);
                break;
   
            case (char)TileType.PortalIn:
                _previousPortalInPos = new Vector3Int(layer, row, col);
                currentLine = _tiles[layer, row, col].GetComponentInChildren<LineRenderer>(true); // 점선 초기화
                if (currentLine == null)
                {
                    currentLine = Instantiate(linePrefab, _tiles[layer, row, col].transform).GetComponent<LineRenderer>();
                }
                currentLine.gameObject.SetActive(true);
                currentLine.SetPosition(0, new Vector3(layer, row, col));
                currentLine.SetPosition(1,  new Vector3(layer, row, col));
                PortalEditing = true;
                break;
            
            case (char)TileType.PortalOut:
                var outPos = new Vector3Int(layer, row, col);
                _portalPairDict[_previousPortalInPos] = outPos;
                _portalPairDict[outPos] = _previousPortalInPos; // 딕셔너리에 쌍 등록
                currentLine.SetPosition(1,  outPos); // 점선 잇기
                PortalEditing = false;
                break;
        }
        _map[layer, row, col] = currentTile;
        _tiles[layer, row, col].SimpleRender(currentTile);
        
        if(currentTile == (char)TileType.PortalIn)
            CurrentTile = (char)TileType.PortalOut;
        else if(currentTile == (char)TileType.PortalOut)
            CurrentTile = (char)TileType.PortalIn;
    }

    public void SetLayer(int index)
    {
        SetValidated(false);
        RotAxis = _selectedAxis;
        
        _canRotate[index] = !_canRotate[index];
        innerCubes[_rotAxis].GetChild(index).GetChild(0).gameObject.SetActive(_canRotate[index]);
        AudioManager.Instance.PlaySFX(_canRotate[index]
            ? AudioManager.SFXType.TileLoad
            : AudioManager.SFXType.TileDelete);
        if (_canRotate.Any(i => i))
            return;
        RotAxis = 0;
    }
    
    public void PlayTest()
    {
        if (!playerModel.gameObject.activeSelf)
        {
            PopUpManager.Instance.Show("시작 위치를 찾을 수 없습니다.");
            return;
        }
        
        gameObject.SetActive(false);
        IsTesting = true;
        // 로딩 오래 걸리면 씬 전환 효과 넣기
        GameManager.Instance.PlayGame((char[,,])_map.Clone(), _portalPairDict, _rotAxis, _canRotate);
    }

    public async void AutoTest()
    {
        if (_autoTestCancellation != null || _map == null || _portalEditing)
            return;

        var map = (char[,,])_map.Clone();
        var portals = new Dictionary<Vector3Int, Vector3Int>(_portalPairDict);
        var axis = _rotAxis;
        var layers = (bool[])_canRotate.Clone();
        using var cancellation = new CancellationTokenSource();
        _autoTestCancellation = cancellation;
        try
        {
            var result = await Task.Run(() => PuzzleSolver.Solve(map, portals, axis, layers,
                cancellationToken: cancellation.Token), cancellation.Token);
            if (cancellation.IsCancellationRequested)
                return;
            if (!result.IsSolvable)
            {
                PopUpManager.Instance.Show(result.ErrorMsg);
                return;
            }

            SetValidated(true, result.SolutionPath);
            PopUpManager.Instance.Show("자동 테스트를 통과했습니다!");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // The map changed or the editor closed before the search completed.
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            if (!cancellation.IsCancellationRequested)
                PopUpManager.Instance.Show("자동 테스트 중 오류가 발생했습니다.");
        }
        finally
        {
            _autoTestCancellation = null;
        }
    }

    #region ToolSet
    public void OnPortalLineToggleChanged(bool b)
    {
        if(b)
            ShowPortalLines();
        else
        {
            HidePortalLines();
        }
    }
    
    public void ShowPortalLines()
    {
        cam.cullingMask |= (1 << LayerMask.NameToLayer("PortalLine"));
    }

    public void HidePortalLines()
    {
        cam.cullingMask &= ~(1 << LayerMask.NameToLayer("PortalLine"));
    }

    public void SetCurrentTileToRoad()
    {
        CurrentTile = (char)TileType.Road;
        // 여기에서 현재 mat 지정
    }
    
    public void SetCurrentTileToEraser()
    {
        CurrentTile = (char)TileType.Empty;
        // 여기에서 현재 mat 지정
    }
    
    public void SetCurrentTileToPlayer()
    {
        CurrentTile = (char)TileType.Player;
        // 여기에서 현재 mat 지정
    }
    
    public void SetCurrentTileToPortal()
    {
        CurrentTile = (char)TileType.PortalIn;
        // 여기에서 현재 mat 지정
    }
    
    public void SetCurrentTileToGhost()
    {
        CurrentTile = (char)TileType.Ghost;
        // 여기에서 현재 mat 지정
    }
    
    public void SetCurrentTileToInv()
    {
        CurrentTile = (char)TileType.Inv;
        // 여기에서 현재 mat 지정
    }
    
    public void SetCurrentTileToLaser()
    {
        CurrentTile = (char)TileType.Laser;
        // 여기에서 현재 mat 지정
    }
    
    public void SetCurrentTileToDashXp()
    {
        CurrentTile = (char)TileType.DashXp;
        // 여기에서 현재 mat 지정
    }
    public void SetCurrentTileToDashXm()
    {
        CurrentTile = (char)TileType.DashXm;
        // 여기에서 현재 mat 지정
    }
    public void SetCurrentTileToDashYp()
    {
        CurrentTile = (char)TileType.DashYp;
        // 여기에서 현재 mat 지정
    }
    public void SetCurrentTileToDashYm()
    {
        CurrentTile = (char)TileType.DashYm;
        // 여기에서 현재 mat 지정
    }
    public void SetCurrentTileToDashZp()
    {
        CurrentTile = (char)TileType.DashZp;
        // 여기에서 현재 mat 지정
    }
    public void SetCurrentTileToDashZm()
    {
        CurrentTile = (char)TileType.DashZm;
        // 여기에서 현재 mat 지정
    }

    public void SetCurrentAxis(int i)
    {
        SelectedAxis = i;
    }
    
    #endregion

    public void EditorReturn()
    {
        if(_undoStack.Count == 0)
            ExitEditorWithoutSave();
        else
            returnCheckPanel.SetActive(true);
    }
    
    public async void ExitEditor()
    {
        _showAnswerCts?.Cancel();
        _showAnswerCts?.Dispose();
        _showAnswerCts = null;
        
        await SaveToDB();
        _currentMapCreating = null;
        await SceneChange.Instance.LoadScene("EditorMenu", false);
    }

    public async void ExitEditorWithoutSave()
    {
        _showAnswerCts?.Cancel();
        _showAnswerCts?.Dispose();
        _showAnswerCts = null;
        
        _currentMapCreating = null;
        await SceneChange.Instance.LoadScene("EditorMenu", false);
    }

    public void OnClickSave() => SaveToDB().Forget();
    
    public async UniTask<bool> SaveToDB()
    {
        if (!GameManager.Instance.CheckNetworkAndLogIn())
            return false;
        
        SceneChange.Instance.LightLoading(true);
        string url = null;
        // 스크린샷 + DB에 update
        try
        {
            url = await CaptureThumbnailAsync();
        }
        catch (Exception e)
        {
            Debug.LogWarning(e.Message);
        }
        
        // 정보 넣기
        _currentMapCreating.ThumbnailUrl = url;
        _currentMapCreating.Data = StringHelper.Encode(_map);
        _currentMapCreating.PortalPairs = PortalPairHelper.Encode(PortalPairHelper.ToList(_portalPairDict));
        _currentMapCreating.RotInfo = RotateHelper.Encode(new RotateInfo{Axis =  _rotAxis, Layers = _canRotate});

        try
        {
            await DBManager.Instance.UpdateMapCreatingAsync(_currentMapCreating);
        }
        catch (Exception e)
        {
            Debug.LogWarning(e.Message);
            PopUpManager.Instance.Show("저장 실패");
            SceneChange.Instance.LightLoading(false);
            return false;
        }
        PopUpManager.Instance.Show("저장 완료");
        SceneChange.Instance.LightLoading(false);
        return true;
    }
    
    public void EndShowAnswer()
    {
        _showAnswerCts?.Cancel();
        _showAnswerCts?.Dispose();
        _showAnswerCts = null;
        _isShowingAnswer = false;
    
        ghostPlayer.gameObject.SetActive(false);
        tileIndicatorParent.SetActive(true);
        rightSideButtonsParent.SetActive(true);
        stopShowAnswerButton.SetActive(false);
    }

    public async UniTaskVoid ShowAnswer()
    {
        _showAnswerCts?.Cancel();
        _showAnswerCts?.Dispose();
        _showAnswerCts = new CancellationTokenSource();
        _isShowingAnswer = true;
        
        rightSideButtonsParent.SetActive(false);
        tileIndicatorParent.SetActive(false);
        stopShowAnswerButton.SetActive(true);
        
        ghostPlayer.gameObject.SetActive(true);

        await ShowAnswerAnim(_showAnswerCts.Token);
    
        // Cancel이 아닌 정상 완료일 때만 EndShowAnswer 호출
        if (!_showAnswerCts.IsCancellationRequested)
            EndShowAnswer();
    }

    private async UniTask ShowAnswerAnim(CancellationToken ct)
    {
        foreach(var i in _answer)
        {
            ghostPlayer.position = i;
            await UniTask.WaitForSeconds(showAnswerTimePerPos, cancellationToken: ct);
        }
    }

    private void Update()
    {
        if (GameManager.Instance.blockIndicators) return;
        
        if (Input.GetKeyDown(KeyCode.Z))
        {
            Undo();
        }
        
        if (_portalEditing) return;

        if (Input.GetKeyDown(KeyCode.R))
        {
            resetPanel.SetActive(true);
        }
        else if (Input.GetKeyDown(KeyCode.Escape) && !_isShowingAnswer)
        {
            if (resetCheckPanel.activeSelf)
            {
                resetCheckPanel.SetActive(false);
                escMenuPanel.SetActive(true);
                return;
            }
            
            escPanel.SetActive(!escPanel.activeSelf);
        }
        else if(Input.GetKeyDown(KeyCode.D) && leftPanel.activeSelf)
        {
            SetCurrentTileToRoad();
        }
        else if (Input.GetKeyDown(KeyCode.E) && leftPanel.activeSelf)
        {
            SetCurrentTileToEraser();
        }
    }

    public void SetValidated(bool validated, Stack<Vector3Int> answer = null)
    {
        if (!validated)
            _autoTestCancellation?.Cancel();
        _isValidated = validated;
        showAnswerButton.interactable = validated;
        exportButton.interactable = validated;
        
        if(answer is not null)
            _answer = new Stack<Vector3Int>(answer);
    }
    
    public async Task<string> CaptureThumbnailAsync()
    {
        // 특정 카메라 시점에서 RenderTexture로 캡처
        var renderTexture = new RenderTexture(256, 256, 24);
        thumbnailCamera.targetTexture = renderTexture;
        thumbnailCamera.gameObject.SetActive(true);
        thumbnailCamera.Render();
        thumbnailCamera.gameObject.SetActive(false);

        RenderTexture.active = renderTexture;
        var texture = new Texture2D(256, 256, TextureFormat.RGB24, false);
        texture.ReadPixels(new Rect(0, 0, 256, 256), 0, 0);
        texture.Apply();

        RenderTexture.active = null;
        thumbnailCamera.targetTexture = null;

        var bytes = texture.EncodeToJPG(quality: 80);
        Destroy(texture);
        Destroy(renderTexture);

        // Storage에 업로드
        var path = $"{_currentMapCreating.MapId}.jpg";
        await SupabaseManager.Instance.Supabase().Storage
            .From("map-thumbnails")
            .Upload(bytes, path, new Supabase.Storage.FileOptions{Upsert = true});

        return SupabaseManager.Instance.Supabase().Storage
            .From("map-thumbnails")
            .GetPublicUrl(path);
    }

    public async void UploadMap()
    {
        if (!GameManager.Instance.CheckNetworkAndLogIn())
            return;
        
        await SaveToDB();
        SceneChange.Instance.LightLoading(true);
        Tuple<string, string> urls = null;
        // 스크린샷 + DB에 update
        try
        {
            urls = await CaptureThumbnailAsyncBoth();
        }
        catch (Exception e)
        {
            Debug.LogWarning(e.Message);
        }
        
        // 정보 넣기
        var data = StringHelper.Encode(_map);
        var portalPairs = PortalPairHelper.Encode(PortalPairHelper.ToList(_portalPairDict));
        var rotInfo = RotateHelper.Encode(new RotateInfo{Axis =  _rotAxis, Layers = _canRotate});
        _currentMapCreating.ThumbnailUrl = urls.Item1;
        _currentMapCreating.Data = data;
        _currentMapCreating.PortalPairs = portalPairs;
        _currentMapCreating.RotInfo = rotInfo;

        var uploadMap = new Map
        {
            MapId = _currentMapCreating.MapId,
            Name = _currentMapCreating.Name,
            Data = data,
            UserId = _currentMapCreating.UserId,
            Desc = _currentMapCreating.Desc,
            PortalPairs = portalPairs,
            RotInfo = rotInfo,
            ThumbnailUrl = urls.Item2
        };

        try
        {
            await DBManager.Instance.UpdateMapCreatingAsync(_currentMapCreating);
        }
        catch (Exception e)
        {
            Debug.LogWarning(e.Message);
            PopUpManager.Instance.Show("저장 실패");
        }
        
        try
        {
            await DBManager.Instance.UpsertMapAsync(uploadMap);
        }
        catch (Exception e)
        {
            Debug.LogWarning(e.Message);
            PopUpManager.Instance.Show("업로드 실패");
            SceneChange.Instance.LightLoading(false);
            return;
        }
        PopUpManager.Instance.Show("업로드 성공");
        SceneChange.Instance.LightLoading(false);
    }
    
    public async Task<Tuple<string, string>> CaptureThumbnailAsyncBoth()
    {
        // 특정 카메라 시점에서 RenderTexture로 캡처
        var renderTexture = new RenderTexture(256, 256, 24);
        thumbnailCamera.targetTexture = renderTexture;
        thumbnailCamera.gameObject.SetActive(true);
        thumbnailCamera.Render();
        thumbnailCamera.gameObject.SetActive(false);

        RenderTexture.active = renderTexture;
        var texture = new Texture2D(256, 256, TextureFormat.RGB24, false);
        texture.ReadPixels(new Rect(0, 0, 256, 256), 0, 0);
        texture.Apply();

        RenderTexture.active = null;
        thumbnailCamera.targetTexture = null;

        var bytes = texture.EncodeToJPG(quality: 80);
        Destroy(texture);
        Destroy(renderTexture);

        // Storage에 업로드
        var path1 = $"{_currentMapCreating.MapId}.jpg";
        var path2 = $"{_currentMapCreating.UserId}/{_currentMapCreating.MapId}.jpg";
        await SupabaseManager.Instance.Supabase().Storage
            .From("map-thumbnails")
            .Upload(bytes, path1, new Supabase.Storage.FileOptions{Upsert = true});
        
        await SupabaseManager.Instance.Supabase().Storage
            .From("uploaded-map-thumbnails")
            .Upload(bytes, path2, new Supabase.Storage.FileOptions{Upsert = true});
        
        var url1 = SupabaseManager.Instance.Supabase().Storage
            .From("map-thumbnails")
            .GetPublicUrl(path1);

        var url2 = SupabaseManager.Instance.Supabase().Storage
            .From("uploaded-map-thumbnails")
            .GetPublicUrl(path2);
        
        return new Tuple<string, string>(url1, url2);
    }
}
