
using System;
using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Quaternion = UnityEngine.Quaternion;
using Vector3 = UnityEngine.Vector3;

#region TileHelpers
public enum TileType
{
    Empty = 'A', Painted, Player, Road,
    PortalIn, PortalOut, PortalInPainted, PortalOutPainted,
    Ghost, GhostPainted,
    Inv, InvPainted,
    Laser, LaserPainted,
    DashXp, DashXm, DashYp, DashYm, DashZp, DashZm,
    DashXpPainted, DashXmPainted, DashYpPainted, DashYmPainted, DashZpPainted, DashZmPainted,
    Count
}

public static class TileHelper
{
    internal static readonly Vector3Int[] Directions =
    {
        Vector3Int.right, Vector3Int.left, Vector3Int.up, Vector3Int.down,
        new Vector3Int(0, 0, 1), new Vector3Int(0, 0, -1)
    };

    internal static bool IsInBounds(this Vector3Int cell, int size) =>
        (uint)cell.x < size && (uint)cell.y < size && (uint)cell.z < size;

    internal static bool IsOnSurface(this Vector3Int cell, int size)
    {
        int last = size - 1;
        return cell.IsInBounds(size) &&
            (cell.x == 0 || cell.y == 0 || cell.z == 0 || cell.x == last || cell.y == last || cell.z == last);
    }

    internal static bool IsDash(char tile) => tile >= (char)TileType.DashXp && tile <= (char)TileType.DashZm;

    public static bool IsPainted(char tile)
    {
        return tile is (char)TileType.Painted or (char)TileType.PortalInPainted or (char)TileType.PortalOutPainted
            or (char)TileType.GhostPainted or (char)TileType.InvPainted or (char)TileType.LaserPainted
            or (char)TileType.DashXmPainted or (char)TileType.DashXpPainted or (char)TileType.DashYmPainted
            or (char)TileType.DashYpPainted or (char)TileType.DashZmPainted or (char)TileType.DashZpPainted;
    }

    public static readonly Dictionary<char, char> PaintTable = new()
    {
        {(char)TileType.Player, (char)TileType.Painted}, // 초기화에 사용
        {(char)TileType.Road, (char)TileType.Painted},
        {(char)TileType.PortalIn, (char)TileType.PortalInPainted},
        {(char)TileType.PortalOut, (char)TileType.PortalOutPainted},
        {(char)TileType.Ghost, (char)TileType.Painted},
        {(char)TileType.Inv, (char)TileType.Painted},
        {(char)TileType.Laser, (char)TileType.Painted},
        {(char)TileType.DashXm, (char)TileType.DashXmPainted},
        {(char)TileType.DashXp, (char)TileType.DashXpPainted},
        {(char)TileType.DashYm, (char)TileType.DashYmPainted},
        {(char)TileType.DashYp, (char)TileType.DashYpPainted},
        {(char)TileType.DashZm, (char)TileType.DashZmPainted},
        {(char)TileType.DashZp, (char)TileType.DashZpPainted}
    };
    
    public static readonly Dictionary<char, char> InvTable = new()
    {
        {(char)TileType.Road, (char)TileType.Painted},
        {(char)TileType.Painted, (char)TileType.Road},
        {(char)TileType.PortalIn, (char)TileType.PortalInPainted},
        {(char)TileType.PortalOut, (char)TileType.PortalOutPainted},
        {(char)TileType.PortalInPainted, (char)TileType.PortalIn},
        {(char)TileType.PortalOutPainted, (char)TileType.PortalOut},
        {(char)TileType.Ghost, (char)TileType.GhostPainted},
        {(char)TileType.Inv, (char)TileType.InvPainted},
        {(char)TileType.GhostPainted, (char)TileType.Ghost},
        {(char)TileType.InvPainted, (char)TileType.Inv},
        {(char)TileType.Laser, (char)TileType.LaserPainted},
        {(char)TileType.LaserPainted, (char)TileType.Laser},
        {(char)TileType.DashXm, (char)TileType.DashXmPainted},
        {(char)TileType.DashXp, (char)TileType.DashXpPainted},
        {(char)TileType.DashYm, (char)TileType.DashYmPainted},
        {(char)TileType.DashYp, (char)TileType.DashYpPainted},
        {(char)TileType.DashZm, (char)TileType.DashZmPainted},
        {(char)TileType.DashZp, (char)TileType.DashZpPainted},
        {(char)TileType.DashXmPainted, (char)TileType.DashXm},
        {(char)TileType.DashXpPainted, (char)TileType.DashXp},
        {(char)TileType.DashYmPainted, (char)TileType.DashYm},
        {(char)TileType.DashYpPainted, (char)TileType.DashYp},
        {(char)TileType.DashZmPainted, (char)TileType.DashZm},
        {(char)TileType.DashZpPainted, (char)TileType.DashZp}
    };

    public const int GhostCount = 5;
}
#endregion
public class PuzzlePlayer : MonoBehaviour
{
    [Header("GamePlay")]
    // ReSharper disable once InconsistentNaming
    private int CubeSize;
    
    [SerializeField] private Transform cubeParent;
    private PuzzleTile[,,] _tiles;

    private char[,,] _map;
    private char[,,] _initialMap;
    private int _roadLeftCount;
    private Dictionary<Vector3Int, Vector3Int> _portalPairDic;
    private Dictionary<Vector3Int, Vector3Int> _initialPortalPairDic;

    [SerializeField] private Transform playerModel;
    [SerializeField] private float playerMoveDuration = 0.5f;
    [SerializeField] private Ease playerMoveEase = Ease.OutExpo;
    [SerializeField] private bool _isMoving;
    [SerializeField] private TMP_Text ghostText;
    private readonly List<CellPulse> _highlightCells = new();
    [SerializeField] private float waveInterval = 0.2f;
    
    #region GhostProperty
    private int _currentGhostCount;
    private int CurrentGhostCount
    {
        get => _currentGhostCount;
        set
        {
            ghostText.text = value.ToString();
            if (_currentGhostCount > 0 && value == 0) // 카운트가 꺼지는 경우
            {
                AudioManager.Instance.PlaySFX(AudioManager.SFXType.GhostEnd);
                /*
                playerModel.GetComponent<MeshRenderer>().materials[0].DOFade(1f, 0.5f)
                    .SetEase(Ease.InOutSine).OnComplete(()=>ghostText.gameObject.SetActive(false));
                */
                ghostText.gameObject.SetActive(false);
                GhostEffectController.Instance.Exit();
            }
            else if (_currentGhostCount == 0 && value > 0) // 켜지는 경우
            {
                AudioManager.Instance.PlaySFX(AudioManager.SFXType.GhostGet);
                ghostText.gameObject.SetActive(true);
                GhostEffectController.Instance.Enter();
                /*
                playerModel.GetComponent<MeshRenderer>().materials[0].DOFade(0.45f, 0.5f)
                    .SetEase(Ease.InOutSine);
                */
            }
            _currentGhostCount = value;
        }
    }
    #endregion
    #region LaserProperty
    private bool _hasLaser;

    private bool HasLaser
    {
        get => _hasLaser;
        set
        {
            if (!_hasLaser && value) // 켜짐
            {
                AudioManager.Instance.PlaySFX(AudioManager.SFXType.LaserGet);
                _highlightCells.Clear();
                var p = playerModel.position;
                int x = (int)p.x, y = (int)p.y, z = (int)p.z;
                bool d1 = true, d2 = true, d3 = true, d4 = true, d5 = true, d6 = true;
                var depth = 1;
                var offset = 0.08f;
                while (d1 || d2 || d3 || d4 || d5 || d6)
                {
                    if (d1)
                    {
                        var t = x + depth;
                        if (CannotMove(t, y, z))
                            d1 = false;
                        else
                        {
                            var cell = _tiles[t, y, z].GetComponent<CellPulse>();
                            _highlightCells.Add(cell);
                            cell.StartPulse((depth-1) * offset);
                        }
                    }
                    if (d2)
                    {
                        var t = x - depth;
                        if (CannotMove(t, y, z))
                            d2 = false;
                        else
                        {
                            var cell = _tiles[t, y, z].GetComponent<CellPulse>();
                            _highlightCells.Add(cell);
                            cell.StartPulse((depth-1) * offset);
                        }
                    }
                    if (d3)
                    {
                        var t = y + depth;
                        if (CannotMove(x, t, z))
                            d3 = false;
                        else
                        {
                            var cell = _tiles[x, t, z].GetComponent<CellPulse>();
                            _highlightCells.Add(cell);
                            cell.StartPulse((depth-1) * offset);
                        }
                    }
                    if (d4)
                    {
                        var t = y - depth;
                        if (CannotMove(x, t, z))
                            d4 = false;
                        else
                        {
                            var cell = _tiles[x, t, z].GetComponent<CellPulse>();
                            _highlightCells.Add(cell);
                            cell.StartPulse((depth-1) * offset);
                        }
                    }
                    if (d5)
                    {
                        var t = z + depth;
                        if (CannotMove(x, y, t))
                            d5 = false;
                        else
                        {
                            var cell = _tiles[x, y, t].GetComponent<CellPulse>();
                            _highlightCells.Add(cell);
                            cell.StartPulse((depth-1) * offset);
                        }
                    }
                    if (d6)
                    {
                        var t = z - depth;
                        if (CannotMove(x, y, t))
                            d6 = false;
                        else
                        {
                            var cell = _tiles[x, y, t].GetComponent<CellPulse>();
                            _highlightCells.Add(cell);
                            cell.StartPulse((depth-1) * offset);
                        }
                    }
                    ++depth;
                }
            }
            else if (_hasLaser && !value) // 꺼짐
            {
                foreach (var cell in _highlightCells)
                    cell.StopPulse();
            }
            _hasLaser = value;
        }
    }
    #endregion

    [Header("Camera")]
    [SerializeField] private Camera cam;
    [SerializeField] private Transform pivot;
    [SerializeField] private float camMoveDuration = 0.5f;
    [SerializeField] private Ease camMoveEase = Ease.OutExpo;
    [SerializeField] private Vector3 currentUp = Vector3.up;
    [SerializeField] private Vector3 currentRight = Vector3.right;
    [SerializeField] private Vector3 currentLeft = Vector3.back;
    private Vector3 _initialCameraPosition;
    private Quaternion _initialCameraRotation;

    [Header("System")] 
    [SerializeField] private InputActionReference camera1Action;
    [SerializeField] private InputActionReference camera2Action;
    [SerializeField] private InputActionReference camera3Action;
    [SerializeField] private InputActionReference camera4Action;
    [SerializeField] private InputActionReference camera5Action;
    [SerializeField] private InputActionReference camera6Action;
    [SerializeField] private InputActionReference qAction;
    [SerializeField] private InputActionReference dAction;
    [SerializeField] private InputActionReference aAction;
    [SerializeField] private InputActionReference eAction;
    [SerializeField] private InputActionReference wAction;
    [SerializeField] private InputActionReference sAction;
    [SerializeField] private InputActionReference undoAction;
    [SerializeField] private InputActionReference resetAction;
    [SerializeField] private bool _isTesting;
    [SerializeField] private bool _isSingle;
    [SerializeField] private bool _isUser;
    private bool _isCleared;
    private Stack<Vector3Int> _answer;
    private short _moves = 0;
    [SerializeField] private TextMeshProUGUI movesText;

    private short Moves
    {
        set
        {
            _moves = value;
            movesText.text = _moves.ToString();
        }
    }
    [SerializeField] private GameObject[] innerCubes; // empty, x, y, z
    private bool[] _canRotate;
    private Transform[] _layers;
    private int _rotAxis; // 0, x, y, z
    [SerializeField] EdgeCubeInitializer _edgeCubeInitializer;
    [SerializeField] private Button undoButton;

    private struct MapState
    {
        public int FirstTileChange;
        public int AnswerCount;
        public Vector3 PlayerPos;
        public int RoadLeftCount;
        public int CurrentGhostCount;
        public bool HasLaser;
        public Dictionary<Vector3Int, Vector3Int> PortalDic;
    }

    private Stack<MapState> _undoStack;
    private readonly List<(int cell, char previous)> _undoTileChanges = new();
    private bool _recordUndo;

    private void Awake()
    {
        _initialCameraPosition = pivot.position;
        _initialCameraRotation = pivot.rotation;
        _tiles = new PuzzleTile[CubeSize, CubeSize, CubeSize];

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
    }
    #region EnableDisable
    private void OnEnable()
    {
        camera1Action.action.performed += CameraRotation1InputWrapper;
        camera2Action.action.performed += CameraRotation2InputWrapper;
        camera3Action.action.performed += CameraRotation3InputWrapper;
        camera4Action.action.performed += CameraRotation4InputWrapper;
        camera5Action.action.performed += CameraRotation5InputWrapper;
        camera6Action.action.performed += CameraRotation6InputWrapper;
        undoAction.action.performed += UndoInputWrapper;
        camera1Action.action.Enable();
        camera2Action.action.Enable();
        camera3Action.action.Enable();
        camera4Action.action.Enable();
        camera5Action.action.Enable();
        camera6Action.action.Enable();
        qAction.action.Enable();
        dAction.action.Enable();
        aAction.action.Enable();
        eAction.action.Enable();
        wAction.action.Enable();
        sAction.action.Enable();
        undoAction.action.Enable();
        resetAction.action.Enable();
        
        InitGame();
        RepositionCamera(false);
        _isMoving = false;
        CheckGameCleared();
    }

    private void OnDisable()
    {
        camera1Action.action.performed -= CameraRotation1InputWrapper;
        camera2Action.action.performed -= CameraRotation2InputWrapper;
        camera3Action.action.performed -= CameraRotation3InputWrapper;
        camera4Action.action.performed -= CameraRotation4InputWrapper;
        camera5Action.action.performed -= CameraRotation5InputWrapper;
        camera6Action.action.performed -= CameraRotation6InputWrapper;
        undoAction.action.performed -= UndoInputWrapper;
        camera1Action.action.Disable();
        camera2Action.action.Disable();
        camera3Action.action.Disable();
        camera4Action.action.Disable();
        camera5Action.action.Disable();
        camera6Action.action.Disable();
        qAction.action.Disable();
        dAction.action.Disable();
        aAction.action.Disable();
        eAction.action.Disable();
        wAction.action.Disable();
        sAction.action.Disable();
        undoAction.action.Disable();
        resetAction.action.Disable();
        
        _edgeCubeInitializer.Clear();
        GhostEffectController.Instance.Exit();
        AudioManager.Instance.SetMasterMuffled(false);
    }
    #endregion

    #region GameSystem

    private void InitGame()
    {
        // 렌더링을 하면서
        // 플레이어 시작점 파악
        // 남은 블럭 개수 파악
        _isCleared = false;
        _roadLeftCount = 0;
        _recordUndo = false;
        _undoTileChanges.Clear();
        _map = (char[,,])_initialMap.Clone();
        _portalPairDic = new Dictionary<Vector3Int, Vector3Int>(_initialPortalPairDic);

        for (var i = 0; i < CubeSize; ++i)
        for (var j = 0; j < CubeSize; ++j)
        for (var k = 0; k < CubeSize; ++k)
        {
            var c = _map[i, j, k];
            _tiles[i, j, k].SimpleRender(c);
            
            switch (c)
            {
                case (char)TileType.Empty:
                    continue;
                case (char)TileType.Player:
                    playerModel.position = new Vector3(i, j, k);
                    PaintWithRender(i, j, k, true);
                    break;
                case (char)TileType.Road:
                case (char) TileType.PortalIn:
                case (char) TileType.PortalOut:
                case (char) TileType.Ghost:
                case (char) TileType.Inv:
                case (char) TileType.Laser:
                case (char) TileType.DashXp:
                case (char) TileType.DashXm:
                case (char) TileType.DashYp:
                case (char) TileType.DashYm:
                case (char) TileType.DashZp:
                case (char) TileType.DashZm:
                    ++_roadLeftCount;
                    break;
            }
        }
        
        if(_answer is not null)
            _answer.Clear();
        else
            _answer = new();
        if(_undoStack is not null)
            _undoStack.Clear();
        else
            _undoStack = new();
        undoButton.interactable = false;
        pivot.position = _initialCameraPosition;
        pivot.rotation = _initialCameraRotation;
        currentUp = Vector3.up;
        currentRight = Vector3.right;
        currentLeft = Vector3.back;
        Moves = 0;
        CurrentGhostCount = 0;
        HasLaser = false;
        
        foreach(var i in innerCubes)
            i.gameObject.SetActive(false);
        innerCubes[_rotAxis].SetActive(true);
        if (_rotAxis != 0)
        {
            _layers = new Transform[CubeSize];
            var t = innerCubes[_rotAxis].GetComponent<Transform>();
            for (int i = 1; i != CubeSize-1; ++i)
            {
                _layers[i] = t.GetChild(i);
                if (_canRotate[i])
                {
                    _layers[i].GetChild(0).gameObject.SetActive(true);
                }
                else
                {
                    _layers[i].GetChild(0).gameObject.SetActive(false);
                }
            }
        }
        _edgeCubeInitializer.Initialize(_canRotate, _rotAxis, CubeSize);
        RepositionCamera(false);
    }

    public void SetMapData(char[,,] map, Dictionary<Vector3Int, Vector3Int> portalPairDic = null, int rotateAxis = 0, 
        bool[] canRotate = null)
    {
        _map = map;
        _initialPortalPairDic = portalPairDic;
        _initialMap = (char[,,])map.Clone();
        _rotAxis = rotateAxis;
        _canRotate = canRotate;
        CubeSize = map.GetLength(0);
    }

    // 맵 전체 렌더링
    // Undo 할때 사용
    private void Render()
    {
        for (var i = 0; i < CubeSize; ++i)
        for (var j = 0; j < CubeSize; ++j)
        for (var k = 0; k < CubeSize; ++k)
        {
            var c = _map[i, j, k];
            _tiles[i, j, k].SimpleRender(c);
        }
    }

    private void CheckGameCleared()
    {
        //Debug.Log(_roadLeftCount);
        if (_roadLeftCount != 0) return;

        _isCleared = true;
        AudioManager.Instance.PlaySFX(AudioManager.SFXType.MapClear);
        
        if (_isTesting)
        {
            MapEditor.Instance.SetValidated(true, _answer);
            GameManager.Instance.GameClearedTest(_moves);
        }
        else if (_isSingle)
        {
            GameManager.Instance.GameClearedSingle(_moves);
        }
        else if (_isUser)
        {
            GameManager.Instance.GameClearedUser(_moves).Forget();
        }
    }

    #endregion

    #region Player

    private bool CannotMove(int x, int y, int z) // 회전 고려 x
    {
        return x == -1 || x == CubeSize || y == -1 || y == CubeSize || z == -1 || z == CubeSize
               || _map[x, y, z] == (char)TileType.Empty
               || _currentGhostCount == 0 && TileHelper.IsPainted(_map[x, y, z]);
    }

    private bool IsOutOfBoundary(int x, int y, int z) => !new Vector3Int(x, y, z).IsOnSurface(CubeSize);

    // 인덱스 계산
    private async UniTask MovePlayer(Vector3 dir, bool doSave = true)
    {
        var pos = playerModel.position;
        var cx = (int)pos.x;
        var cy = (int)pos.y;
        var cz = (int)pos.z;
        var nPos = pos + dir;
        var nLayer = (int)nPos.x;
        var nRow = (int)nPos.y;
        var nCol = (int)nPos.z;
        
        if (IsOutOfBoundary(nLayer, nRow, nCol) // 바운더리를 벗어난 경우 무조건 이동 안됨
            || ((_map[nLayer, nRow, nCol] == (char)TileType.Empty || _currentGhostCount == 0 && TileHelper.IsPainted(_map[nLayer, nRow, nCol]))
                && (_currentGhostCount > 0 || _rotAxis == 0
                    || _rotAxis == 1 && (!_canRotate[cx] || dir.x != 0)
                    || _rotAxis == 2 && (!_canRotate[cy] || dir.y != 0)
                    || _rotAxis == 3 && (!_canRotate[cz] || dir.z != 0))))
        {
            // 이동 불가 애니메이션
            AudioManager.Instance.PlaySFX(AudioManager.SFXType.MoveBlocked);
            await playerModel.DOShakePosition(0.2f, 0.2f, 20).AsyncWaitForCompletion().AsUniTask();
            return;
        }
        // ---- 전진 ----
        var before = _map[nLayer, nRow, nCol];
        try
        {
            if (doSave)
            {
                SaveUndoState();
                Moves = (short)(_moves+1);
            }

            if (_currentGhostCount == 0 && _rotAxis != 0
                    &&(_map[nLayer, nRow, nCol] == (char)TileType.Empty || TileHelper.IsPainted(_map[nLayer, nRow, nCol]))) // 회전
            {
                if (_rotAxis == 1 && (cz == 0 && dir.y > 0 || cz == CubeSize - 1 && dir.y < 0
                        || cy == 0 && dir.z < 0 || cy == CubeSize - 1 && dir.z > 0)
                    || _rotAxis == 2 && (cx == 0 && dir.z > 0 || cx == CubeSize - 1 && dir.z < 0
                        || cz == 0 && dir.x < 0 || cz == CubeSize - 1 && dir.x > 0)
                    || _rotAxis == 3 && (cx == 0 && dir.y < 0 || cx == CubeSize - 1 && dir.y > 0
                        || cy == 0 && dir.x > 0 || cy == CubeSize - 1 && dir.x < 0))
                {
                    await ApplyRotate(nLayer, nRow, nCol, true); // 시계방향
                }
                else
                {
                    await ApplyRotate(nLayer, nRow, nCol, false); // 반시계
                }
                return;
            }
            _answer.Push(new Vector3Int(nLayer, nRow, nCol));

            if (_currentGhostCount > 0)
                --CurrentGhostCount;

            if (_hasLaser)
            {
                AudioManager.Instance.PlaySFX(AudioManager.SFXType.LaserShoot);
                HasLaser = false;
                var animTasks =  new List<UniTask>();
                do
                {
                    --_roadLeftCount;
                    var afterInv = TileHelper.InvTable[_map[(int)nPos.x, (int)nPos.y, (int)nPos.z]];
                    SetMapTile((int)nPos.x, (int)nPos.y, (int)nPos.z, afterInv);
                    animTasks.Add(_tiles[(int)nPos.x, (int)nPos.y, (int)nPos.z].Pop());
                    _tiles[(int)nPos.x, (int)nPos.y, (int)nPos.z].SimpleRender(afterInv); // TODO: 나중에 Render로 변경
                    await UniTask.WaitForSeconds(0.1f);
                    nPos += dir;
                } while (!CannotMove((int)nPos.x, (int)nPos.y, (int)nPos.z));
                await UniTask.WhenAll(animTasks);
            }
            else
            {
                // 플레이어 움직임 애니메이션 기다리고
                AudioManager.Instance.PlaySFX(AudioManager.SFXType.Move);
                await playerModel.DOMove(nPos, playerMoveDuration).SetEase(playerMoveEase).AsyncWaitForCompletion()
                    .AsUniTask();
                // 색칠 및 아이템 사용
                if (_currentGhostCount == 0)
                {
                    if (!TileHelper.IsPainted(before))
                        --_roadLeftCount;
                    await PaintWithRender(nLayer, nRow, nCol, true); // 일단 simpleRender 사용
                    await ApplyItem(nLayer, nRow, nCol, before); // 아이템 사용
                }
            }

            CheckGameCleared();
        }
        finally
        {
            if (doSave)
                _recordUndo = false;
        }
    }

    private async UniTask ApplyItem(int x, int y, int z, char before)
    {
        switch (before)
        {
            case (char)TileType.PortalIn:
                //Debug.Log(before);
                AudioManager.Instance.PlaySFX(AudioManager.SFXType.Portal);
                await playerModel.DOScale(Vector3.zero, playerMoveDuration).SetEase(Ease.InOutSine)
                    .AsyncWaitForCompletion().AsUniTask();
                var afterPos = _portalPairDic[new Vector3Int(x, y, z)];
                playerModel.position = afterPos;
                _answer.Push(afterPos);
                await playerModel.DOScale(Vector3.one, playerMoveDuration).SetEase(Ease.InOutSine)
                    .AsyncWaitForCompletion().AsUniTask();
                
                if (!TileHelper.IsPainted(_map[afterPos.x, afterPos.y, afterPos.z]))
                    --_roadLeftCount;
                await PaintWithRender(afterPos.x, afterPos.y, afterPos.z, true);
                RepositionCamera();
                break;
            
            case (char)TileType.Ghost:
                CurrentGhostCount = TileHelper.GhostCount;
                break;
            
            case (char)TileType.Inv:
                AudioManager.Instance.PlaySFX(AudioManager.SFXType.Inverter);
                await InverterEffectAsync(x, y, z);
                break;
            case (char)TileType.Laser:
                HasLaser = true;
                break;
            case (char)TileType.DashXp:
                AudioManager.Instance.PlaySFX(AudioManager.SFXType.Dash);
                await MovePlayer(new Vector3(1, 0, 0), false);
                break;
            case (char)TileType.DashXm:
                AudioManager.Instance.PlaySFX(AudioManager.SFXType.Dash);
                await MovePlayer(new Vector3(-1, 0, 0), false);
                break;
            case (char)TileType.DashYp:
                AudioManager.Instance.PlaySFX(AudioManager.SFXType.Dash);
                await MovePlayer(new Vector3(0, 1, 0), false);
                break;
            case (char)TileType.DashYm:
                AudioManager.Instance.PlaySFX(AudioManager.SFXType.Dash);
                await MovePlayer(new Vector3(0, -1, 0), false);
                break;
            case (char)TileType.DashZp:
                AudioManager.Instance.PlaySFX(AudioManager.SFXType.Dash);
                await MovePlayer(new Vector3(0, 0, 1), false);
                break;
            case (char)TileType.DashZm:
                AudioManager.Instance.PlaySFX(AudioManager.SFXType.Dash);
                await MovePlayer(new Vector3(0, 0, -1), false);
                break;
        }
    }
    
    private async UniTask InverterEffectAsync(int x, int y, int z)
    {
        RippleEffectController.Instance.Play(new Vector3(x, y, z));

        _roadLeftCount = 0;

        // 모든 겉면 타일을 출발점으로부터의 맨해튼 거리 기준으로 그룹핑
        var waveGroups = new Dictionary<int, List<(int cx, int cy, int cz)>>();

        for (var i = 0; i < CubeSize; i++)
        for (var j = 0; j < CubeSize; j++)
        for (var k = 0; k < CubeSize; k++)
        {
            // 겉면 타일만, 발동 위치 제외
            if (!IsOnSurface(i, j, k)) continue;
            if (_map[i, j, k] == (char)TileType.Empty) continue;
            if (i == x && j == y && k == z) continue;

            var dist = Mathf.Abs(i - x) + Mathf.Abs(j - y) + Mathf.Abs(k - z);
            if (!waveGroups.ContainsKey(dist))
                waveGroups[dist] = new List<(int, int, int)>();
            waveGroups[dist].Add((i, j, k));
        }

        // 데이터 반전 즉시 전체 적용
        for (var i = 0; i < CubeSize; i++)
        for (var j = 0; j < CubeSize; j++)
        for (var k = 0; k < CubeSize; k++)
        {
            if (!IsOnSurface(i, j, k)) continue;
            if (_map[i, j, k] == (char)TileType.Empty) continue;
            if (i == x && j == y && k == z) continue;

            var after = TileHelper.InvTable[_map[i, j, k]];
            SetMapTile(i, j, k, after);
            if (!TileHelper.IsPainted(after))
                ++_roadLeftCount;
        }

        // 거리 순으로 웨이브 애니메이션
        var allAnimTasks = new List<UniTask>();
        foreach (var dist in waveGroups.Keys.OrderBy(d => d))
        {
            foreach (var (cx, cy, cz) in waveGroups[dist])
            {
                _tiles[cx, cy, cz].SimpleRender(_map[cx, cy, cz]);
                allAnimTasks.Add(_tiles[cx, cy, cz].Pop(false));
            }
            AudioManager.Instance.PlaySFX(AudioManager.SFXType.TileWave2);
            await UniTask.Delay(TimeSpan.FromSeconds(waveInterval));
        }

        await UniTask.WhenAll(allAnimTasks);
    }

    private bool IsOnSurface(int i, int j, int k) => new Vector3Int(i, j, k).IsOnSurface(CubeSize);

    #region Rotation
    private async UniTask ApplyRotate(int x, int y, int z, bool clockWise, bool cascade = true)
    {
        char[][] temp = new char[CubeSize][];
        List<GameObject> rotationObjects = new();
        for (int index = 0; index != CubeSize; ++index)
            temp[index] = new char[CubeSize];
        Vector3 targetAngle;
        int playerX = (int)playerModel.position.x, playerY = (int)playerModel.position.y, playerZ = (int)playerModel.position.z;
        Vector3 playerTarget = default;
        var hadLaser = _hasLaser;
        HasLaser = false;

        switch(_rotAxis)
        {
            case 1:
                for (int i = 0; i != CubeSize; ++i)
                for (int j = 0; j != CubeSize; ++j)
                    temp[i][j] = _map[x, i, j];
                for (int i = 0; i != CubeSize; ++i)
                for (int j = 0; j != CubeSize; ++j)
                {
                    var c = temp[i][j];
                    switch(c)
                    {
                        case (char)TileType.PortalIn:
                        case (char)TileType.PortalInPainted:
                        case (char)TileType.PortalOut:
                        case (char)TileType.PortalOutPainted:
                            var original = new Vector3Int(x, i, j);
                            var target = clockWise ? new Vector3Int(x, CubeSize-1-j, i) : new Vector3Int(x, j, CubeSize-1-i);
                            MovePortalLink(original, target);
                            break;
                        case (char)TileType.DashYp:
                            temp[i][j] = clockWise ? (char)TileType.DashZp : (char)TileType.DashZm;
                            break;
                        case (char)TileType.DashYpPainted:
                            temp[i][j] = clockWise ? (char)TileType.DashZpPainted : (char)TileType.DashZmPainted;
                            break;
                        case (char)TileType.DashYm:
                            temp[i][j] = clockWise ? (char)TileType.DashZm : (char)TileType.DashZp;
                            break;
                        case (char)TileType.DashYmPainted:
                            temp[i][j] = clockWise ? (char)TileType.DashZmPainted :  (char)TileType.DashZpPainted;
                            break;
                        case (char)TileType.DashZp:
                            temp[i][j] = clockWise ? (char)TileType.DashYm : (char)TileType.DashYp;
                            break;
                        case (char)TileType.DashZpPainted:
                            temp[i][j] = clockWise ? (char)TileType.DashYmPainted : (char)TileType.DashYpPainted;
                            break;
                        case (char)TileType.DashZm:
                            temp[i][j] = clockWise ? (char)TileType.DashYp : (char)TileType.DashYm;
                            break;
                        case (char)TileType.DashZmPainted:
                            temp[i][j] = clockWise ? (char)TileType.DashYpPainted : (char)TileType.DashYmPainted;
                            break;
                    }
                    if (clockWise)
                    {
                        SetMapTile(x, CubeSize - 1 - j, i, temp[i][j]);
                        playerTarget = new Vector3(playerX, CubeSize - 1 - playerZ, playerY);
                    }
                    else
                    {
                        SetMapTile(x, j, CubeSize - 1 - i, temp[i][j]);
                        playerTarget = new Vector3(playerX, playerZ, CubeSize-1-playerY);
                    }
                    rotationObjects.Add(_tiles[x, i, j].gameObject);
                }
                targetAngle = clockWise ? new Vector3(90f, 0, 0) : new Vector3(-90f, 0, 0);
                if (cascade)
                {
                    List<UniTask> tasks = new();
                    bool b1 = true, b2 = true;
                    var depth = 1;
                    tasks.Add(RotateTilesEffect(x, rotationObjects, targetAngle, true, playerTarget));
                    if (x - depth < 0 || !_canRotate[x - depth]) b1 = false;
                    if (x + depth >= CubeSize || !_canRotate[x + depth]) b2 = false;
                    while (b1 || b2)
                    {
                        await UniTask.WaitForSeconds(0.13f);
                        if (b1)
                        {
                            if (x - (depth+1) < 0 || !_canRotate[x - (depth+1)]) b1 = false;
                            tasks.Add(ApplyRotate(x-depth, y, z, clockWise, false));
                        }
                        if (b2)
                        {
                            if (x + (depth+1) >= CubeSize || !_canRotate[x + (depth+1)]) b2 = false;
                            tasks.Add(ApplyRotate(x+depth, y, z, clockWise, false));
                        }
                        ++depth;
                    }
                    await UniTask.WhenAll(tasks);
                }
                else
                    await RotateTilesEffect(x, rotationObjects, targetAngle, false);
                break;
            case 2:
                for (int i = 0; i != CubeSize; ++i)
                for (int j = 0; j != CubeSize; ++j)
                    temp[i][j] = _map[i, y, j];
                for (int i = 0; i != CubeSize; ++i)
                for (int j = 0; j != CubeSize; ++j)
                {
                    var c = temp[i][j];
                    switch(c)
                    {
                        case (char)TileType.PortalIn:
                        case (char)TileType.PortalInPainted:
                        case (char)TileType.PortalOut:
                        case (char)TileType.PortalOutPainted:
                            var original = new Vector3Int(i, y, j);
                            var target = clockWise ? new Vector3Int(j, y, CubeSize-1-i) : new Vector3Int(CubeSize-1-j, y, i);
                            MovePortalLink(original, target);
                            break;
                        case (char)TileType.DashXp:
                            temp[i][j] = clockWise ? (char)TileType.DashZm : (char)TileType.DashZp;
                            break;
                        case (char)TileType.DashXpPainted:
                            temp[i][j] = clockWise ? (char)TileType.DashZmPainted : (char)TileType.DashZpPainted;
                            break;
                        case (char)TileType.DashXm:
                            temp[i][j] = clockWise ? (char)TileType.DashZp : (char)TileType.DashZm;
                            break;
                        case (char)TileType.DashXmPainted:
                            temp[i][j] = clockWise ? (char)TileType.DashZpPainted :  (char)TileType.DashZmPainted;
                            break;
                        case (char)TileType.DashZp:
                            temp[i][j] = clockWise ? (char)TileType.DashXp : (char)TileType.DashXm;
                            break;
                        case (char)TileType.DashZpPainted:
                            temp[i][j] = clockWise ? (char)TileType.DashXpPainted : (char)TileType.DashXmPainted;
                            break;
                        case (char)TileType.DashZm:
                            temp[i][j] = clockWise ? (char)TileType.DashXm : (char)TileType.DashXp;
                            break;
                        case (char)TileType.DashZmPainted:
                            temp[i][j] = clockWise ? (char)TileType.DashXmPainted : (char)TileType.DashXpPainted;
                            break;
                    }
                    if (clockWise)
                    {
                        SetMapTile(j, y, CubeSize - 1 - i, temp[i][j]);
                        playerTarget = new Vector3(playerZ, playerY, CubeSize-1-playerX);
                    }
                    else
                    {
                        SetMapTile(CubeSize - 1 - j, y, i, temp[i][j]);
                        playerTarget = new Vector3(CubeSize-1-playerZ, playerY, playerX);
                    }
                    rotationObjects.Add(_tiles[i, y, j].gameObject);
                }
                targetAngle = clockWise ? new Vector3(0, 90f, 0) : new Vector3(0, -90f, 0);
                if (cascade)
                {
                    List<UniTask> tasks = new();
                    bool b1 = true, b2 = true;
                    var depth = 1;
                    tasks.Add(RotateTilesEffect(y, rotationObjects, targetAngle, true, playerTarget));
                    if (y - depth < 0 || !_canRotate[y - depth]) b1 = false;
                    if (y + depth >= CubeSize || !_canRotate[y + depth]) b2 = false;
                    while (b1 || b2)
                    {
                        await UniTask.WaitForSeconds(0.13f);
                        if (b1)
                        {
                            if (y - (depth+1) < 0 || !_canRotate[y - (depth+1)]) b1 = false;
                            tasks.Add(ApplyRotate(x, y-depth, z, clockWise, false));
                        }
                        if (b2)
                        {
                            if (y + (depth+1) >= CubeSize || !_canRotate[y + (depth+1)]) b2 = false;
                            tasks.Add(ApplyRotate(x, y+depth, z, clockWise, false));
                        }
                        ++depth;
                    }
                    await UniTask.WhenAll(tasks);
                }
                else
                    await RotateTilesEffect(y, rotationObjects, targetAngle, false);
                break;
            case 3:
                for (int i = 0; i != CubeSize; ++i)
                for (int j = 0; j != CubeSize; ++j)
                    temp[i][j] = _map[i, j, z];
                for (int i = 0; i != CubeSize; ++i)
                for (int j = 0; j != CubeSize; ++j)
                {
                    var c = temp[i][j];
                    switch(c)
                    {
                        case (char)TileType.PortalIn:
                        case (char)TileType.PortalInPainted:
                        case (char)TileType.PortalOut:
                        case (char)TileType.PortalOutPainted:
                            var original = new Vector3Int(i, j, z);
                            var target = clockWise ? new Vector3Int(CubeSize-1-j, i, z) : new Vector3Int(j, CubeSize-1-i, z);
                            MovePortalLink(original, target);
                            break;
                        case (char)TileType.DashYp:
                            temp[i][j] = clockWise ? (char)TileType.DashXm : (char)TileType.DashXp;
                            break;
                        case (char)TileType.DashYpPainted:
                            temp[i][j] = clockWise ? (char)TileType.DashXmPainted : (char)TileType.DashXpPainted;
                            break;
                        case (char)TileType.DashYm:
                            temp[i][j] = clockWise ? (char)TileType.DashXp : (char)TileType.DashXm;
                            break;
                        case (char)TileType.DashYmPainted:
                            temp[i][j] = clockWise ? (char)TileType.DashXpPainted :  (char)TileType.DashXmPainted;
                            break;
                        case (char)TileType.DashXp:
                            temp[i][j] = clockWise ? (char)TileType.DashYp : (char)TileType.DashYm;
                            break;
                        case (char)TileType.DashXpPainted:
                            temp[i][j] = clockWise ? (char)TileType.DashYpPainted : (char)TileType.DashYmPainted;
                            break;
                        case (char)TileType.DashXm:
                            temp[i][j] = clockWise ? (char)TileType.DashYm : (char)TileType.DashYp;
                            break;
                        case (char)TileType.DashXmPainted:
                            temp[i][j] = clockWise ? (char)TileType.DashYmPainted : (char)TileType.DashYpPainted;
                            break;
                    }
                    if (clockWise)
                    {
                        SetMapTile(CubeSize - 1 - j, i, z, temp[i][j]);
                        playerTarget = new Vector3(CubeSize-1-playerY, playerX, playerZ);
                    }
                    else
                    {
                        SetMapTile(j, CubeSize - 1 - i, z, temp[i][j]);
                        playerTarget = new Vector3(playerY, CubeSize - 1 - playerX, playerZ);
                    }
                    rotationObjects.Add(_tiles[i, j, z].gameObject);
                }
                targetAngle = clockWise ? new Vector3(0, 0, 90f) : new Vector3(0, 0, -90f);
                if (cascade)
                {
                    List<UniTask> tasks = new();
                    bool b1 = true, b2 = true;
                    var depth = 1;
                    tasks.Add(RotateTilesEffect(z, rotationObjects, targetAngle, true, playerTarget));
                    if (z - depth < 0 || !_canRotate[z - depth]) b1 = false;
                    if (z + depth >= CubeSize || !_canRotate[z + depth]) b2 = false;
                    while (b1 || b2)
                    {
                        await UniTask.WaitForSeconds(0.13f);
                        if (b1)
                        {
                            if (z - (depth+1) < 0 || !_canRotate[z - (depth+1)]) b1 = false;
                            tasks.Add(ApplyRotate(x, y, z-depth, clockWise, false));
                        }
                        if (b2)
                        {
                            if (z + (depth+1) >= CubeSize || !_canRotate[z + (depth+1)]) b2 = false;
                            tasks.Add(ApplyRotate(x, y, z+depth, clockWise, false));
                        }
                        ++depth;
                    }
                    await UniTask.WhenAll(tasks);
                }
                else
                    await RotateTilesEffect(z, rotationObjects, targetAngle, false);
                break;
        }
        _answer.Push(new Vector3Int((int)playerTarget.x, (int)playerTarget.y, (int)playerTarget.z));
        if (hadLaser)
            HasLaser = true;
        RepositionCamera();
    }

    private async UniTask RotateTilesEffect(int index, List<GameObject> objectsToRotate, Vector3 targetAngle, bool includePlayer = true,
        Vector3 playerPosition = default)
    {
        GameObject pivot = new GameObject("RotationPivot");
        pivot.transform.position = new Vector3((CubeSize - 1) / 2.0f, (CubeSize - 1) / 2.0f, (CubeSize - 1) / 2.0f);
        // 피벗에 붙이기 전 저장
        var originalData = objectsToRotate.Select(obj => (
            obj: obj,
            parent: obj.transform.parent,
            siblingIndex: obj.transform.GetSiblingIndex(),
            localPosition: obj.transform.localPosition // 원본 로컬 좌표 저장
        )).ToList();
        Transform playerOriginParent = null;
        if (includePlayer)
        {
            playerOriginParent = playerModel.parent;
            playerModel.SetParent(pivot.transform);
        }

        foreach (var data in originalData)
            data.obj.transform.SetParent(pivot.transform);

        var t1 = pivot.transform.DORotate(targetAngle, 1.1f)
            .SetEase(Ease.OutBack)
            .OnComplete(() =>
            {
                foreach (var data in originalData)
                {
                    data.obj.transform.SetParent(data.parent);
                    data.obj.transform.SetSiblingIndex(data.siblingIndex);

                    // 부동소수점 오차 제거 — 원본 값으로 강제 스냅
                    data.obj.transform.localPosition = data.localPosition;
                    data.obj.transform.localRotation = Quaternion.identity;
                    int x = (int)data.obj.transform.position.x, y = (int)data.obj.transform.position.y,  z = (int)data.obj.transform.position.z;
                    _tiles[x, y, z].SimpleRender(_map[x, y, z]);
                }
                if (includePlayer)
                {
                    playerModel.SetParent(playerOriginParent);
                    playerModel.position = playerPosition;
                    playerModel.rotation = Quaternion.identity;
                }
                Destroy(pivot);
            }).AsyncWaitForCompletion().AsUniTask();
        var t2 = _layers[index].DORotate(targetAngle, 1.1f)
            .SetEase(Ease.OutBack)
            .OnComplete(() =>
            {
                _layers[index].localRotation = Quaternion.identity;
            }).AsyncWaitForCompletion().AsUniTask();
        AudioManager.Instance.PlaySFX(AudioManager.SFXType.Rotation);
        await UniTask.WhenAll(t1, t2);
    }
    
    #endregion

    // 움직임이 확정된 뒤 저장
    private void SaveUndoState()
    {
        _undoStack.Push(new MapState()
        {
            FirstTileChange = _undoTileChanges.Count,
            AnswerCount = _answer.Count,
            PlayerPos = playerModel.position,
            RoadLeftCount = _roadLeftCount,
            CurrentGhostCount = _currentGhostCount,
            HasLaser = _hasLaser,
            PortalDic = _portalPairDic
        });
        _recordUndo = true;
        undoButton.interactable = true;
    }

    private void SetMapTile(int x, int y, int z, char tile)
    {
        char previous = _map[x, y, z];
        if (previous == tile)
            return;
        if (_recordUndo)
            _undoTileChanges.Add(((x * CubeSize + y) * CubeSize + z, previous));
        _map[x, y, z] = tile;
    }

    private void MovePortalLink(Vector3Int original, Vector3Int target)
    {
        if (original == target)
            return;

        // All turns without portal changes can share the same dictionary.
        if (_recordUndo && ReferenceEquals(_undoStack.Peek().PortalDic, _portalPairDic))
            _portalPairDic = new Dictionary<Vector3Int, Vector3Int>(_portalPairDic);

        var partner = _portalPairDic[original];
        _portalPairDic.Remove(original);
        _portalPairDic[target] = partner;
        _portalPairDic[partner] = target;
    }

    private void RestoreUndoTiles(int firstChange)
    {
        for (int i = _undoTileChanges.Count - 1; i >= firstChange; i--)
        {
            var (cell, previous) = _undoTileChanges[i];
            int layerRow = cell / CubeSize;
            _map[layerRow / CubeSize, layerRow % CubeSize, cell % CubeSize] = previous;
        }
        _undoTileChanges.RemoveRange(firstChange, _undoTileChanges.Count - firstChange);
    }

    public void UndoInputWrapper(InputAction.CallbackContext _) => Undo();
    public void Undo(bool doRender = true)
    {
        if (_undoStack.Count == 0 || _isMoving || _isCleared) return;
        if (!GameManager.Instance.isPlaying) return;
        
        AudioManager.Instance.PlaySFX(AudioManager.SFXType.Undo);
        var s = _undoStack.Pop();
        RestoreUndoTiles(s.FirstTileChange);
        HasLaser = false;
        playerModel.position = s.PlayerPos;
        _roadLeftCount = s.RoadLeftCount;
        CurrentGhostCount = s.CurrentGhostCount;
        _portalPairDic = s.PortalDic;

        while (_answer.Count > s.AnswerCount)
            _answer.Pop();
        Moves = (short)(_moves-1);

        if (doRender)
            Render();
        
        HasLaser = s.HasLaser;
        RepositionCamera(false);
        
        if(_undoStack.Count == 0)
            undoButton.interactable = false;
    }

    // 이동 및 아이템 사용할때 사용 -> 즉 타일이 색칠
    public async UniTask PaintWithRender(int layer, int row, int col,
        bool useSimpleRender = false)
    {
        var before = _map[layer, row, col];
        if (before == (char)TileType.Empty || TileHelper.IsPainted(before)) return;
        
        var tile = TileHelper.PaintTable[before];
        SetMapTile(layer, row, col, tile);
        //Debug.Log(tile);
        if (useSimpleRender)
        {
            _tiles[layer, row, col].SimpleRender(tile);
            return;
        }
        
        await _tiles[layer, row, col].Render(tile);
    }

    #endregion
    
    #region PlayerControl

    private async void Update()
    {
        if (qAction.action.IsPressed())
        {
            await MovePlayerQ();
        }
        if (dAction.action.IsPressed())
        {
            await MovePlayerD();
        }
        if (aAction.action.IsPressed())
        {
            await MovePlayerA();
        }
        if (eAction.action.IsPressed())
        {
            await MovePlayerE();
        }
        if (wAction.action.IsPressed())
        {
            await MovePlayerW();
        }
        if (sAction.action.IsPressed())
        {
            await MovePlayerS();
        }
    }

    public async UniTask MovePlayerQ()
    {
        if (_isMoving || _isCleared || !GameManager.Instance.isPlaying) return;
        _isMoving = true;
        try
        {
            await MovePlayer(-currentRight);
        }
        finally
        {
            _isMoving = false;
        }
    }
    
    public async UniTask MovePlayerD()
    {
        if (_isMoving || _isCleared || !GameManager.Instance.isPlaying) return;
        _isMoving = true;
        try
        {
            await MovePlayer(currentRight);
        }
        finally
        {
            _isMoving = false;
        }
    }
    
    public async UniTask MovePlayerA()
    {
        if (_isMoving || _isCleared || !GameManager.Instance.isPlaying) return;
        _isMoving = true;
        try
        {
            await MovePlayer(currentLeft);
        }
        finally
        {
            _isMoving = false;
        }
    }
    
    public async UniTask MovePlayerE()
    {
        if (_isMoving || _isCleared || !GameManager.Instance.isPlaying) return;
        _isMoving = true;
        try
        {
            await MovePlayer(-currentLeft);
        }
        finally
        {
            _isMoving = false;
        }
    }
    
    public async UniTask MovePlayerW()
    {
        if (_isMoving || _isCleared || !GameManager.Instance.isPlaying) return;
        _isMoving = true;
        try
        {
            await MovePlayer(currentUp);
        }
        finally
        {
            _isMoving = false;
        }
    }
    
    public async UniTask MovePlayerS()
    {
        if (_isMoving || _isCleared || !GameManager.Instance.isPlaying) return;
        _isMoving = true;
        try
        {
            await MovePlayer(-currentUp);
        }
        finally
        {
            _isMoving = false;
        }
    }

    public void Restart() => InitGame();
    
    #endregion

    #region CameraMove

    private void RepositionCamera(bool byPassIsMoving = true)
    {
        var pos = playerModel.position;
        var v = pos + Vector3.one;
        var rv = (int)Vector3.Dot(currentRight, v);
        var lv = (int)Vector3.Dot(currentLeft, v);
        var uv = (int)Vector3.Dot(currentUp, v);
        var rOpposite = rv == 1 || rv == -CubeSize;
        var rForward = rv == -1 || rv == CubeSize;
        var lOpposite = lv == 1 || lv == -CubeSize;
        var lForward = lv == -1 || lv == CubeSize;
        var uOpposite = uv == 1 || uv == -CubeSize;
        var uForward = uv == -1 || uv == CubeSize;
        if (rOpposite && !lForward && !uForward)
        {
            CameraRotation6(byPassIsMoving);
        }
        else if (lOpposite && !rForward && !uForward)
        {
            CameraRotation2(byPassIsMoving);
        }
        else if (uOpposite && !lForward && !rForward)
        {
            CameraRotation4(byPassIsMoving);
        }
    }

    private void TransitionTo(Vector3 corner, Vector3 up, bool byPassIsMoving = false)
    {
        var target = Quaternion.LookRotation(-corner, up);
        AudioManager.Instance.PlaySFX(AudioManager.SFXType.CamRotate);
        // Pivot 회전만 바꾸면 카메라는 자동으로 따라옴
        pivot.DORotateQuaternion(target, camMoveDuration).SetEase(camMoveEase).OnComplete(() => 
        {
            if(!byPassIsMoving)
                _isMoving = false;
        });
    }

    public void CameraRotation1InputWrapper(InputAction.CallbackContext _) => CameraRotation1();
    public void CameraRotation2InputWrapper(InputAction.CallbackContext _) => CameraRotation2();
    public void CameraRotation3InputWrapper(InputAction.CallbackContext _) => CameraRotation3();
    public void CameraRotation4InputWrapper(InputAction.CallbackContext _) => CameraRotation4();
    public void CameraRotation5InputWrapper(InputAction.CallbackContext _) => CameraRotation5();
    public void CameraRotation6InputWrapper(InputAction.CallbackContext _) => CameraRotation6();
    public void CameraRotation1(bool byPassIsMoving = false)
    {
        if (!byPassIsMoving && _isMoving || _isCleared || !GameManager.Instance.isPlaying) return;
        _isMoving = true;

        var left = currentLeft;
        var right = currentRight;

        currentLeft = -left;
        currentRight = -right;
        
        TransitionTo(currentLeft + currentRight + currentUp, currentUp, byPassIsMoving);
    }

    public void CameraRotation2(bool byPassIsMoving = false)
    {
        if (!byPassIsMoving && _isMoving || _isCleared || !GameManager.Instance.isPlaying) return;
        _isMoving = true;
        
        var left = currentLeft;
        var right = currentRight;

        currentLeft = right;
        currentRight = -left;
        
        TransitionTo(currentLeft + currentRight + currentUp, currentUp, byPassIsMoving);
    }

    public void CameraRotation3(bool byPassIsMoving = false)
    {
        if (!byPassIsMoving && _isMoving || _isCleared || !GameManager.Instance.isPlaying) return;
        _isMoving = true;
        
        var left = currentLeft;
        var right = currentRight;
        var up = currentUp;

        currentLeft = -up;
        currentRight = -left;
        currentUp = right;
        
        TransitionTo(currentLeft + currentRight + currentUp, currentUp, byPassIsMoving);
    }

    public void CameraRotation4(bool byPassIsMoving = false)
    {
        if (!byPassIsMoving && _isMoving || _isCleared || !GameManager.Instance.isPlaying) return;
        _isMoving = true;
        
        var left = currentLeft;
        var right = currentRight;
        var up = currentUp;

        currentLeft = right;
        currentRight = left;
        currentUp = -up;
        
        TransitionTo(currentLeft + currentRight + currentUp, currentUp, byPassIsMoving);
    }

    public void CameraRotation5(bool byPassIsMoving = false)
    {
        if (!byPassIsMoving && _isMoving || _isCleared || !GameManager.Instance.isPlaying) return;
        _isMoving = true;
        
        var left = currentLeft;
        var right = currentRight;
        var up = currentUp;

        currentLeft = -right;
        currentRight = -up;
        currentUp = left;
        
        TransitionTo(currentLeft + currentRight + currentUp, currentUp, byPassIsMoving);
    }

    public void CameraRotation6(bool byPassIsMoving = false)
    {
        if (!byPassIsMoving && _isMoving || _isCleared || !GameManager.Instance.isPlaying) return;
        _isMoving = true;
        
        var left = currentLeft;
        var right = currentRight;

        currentLeft = -right;
        currentRight = left;
        
        TransitionTo(currentLeft + currentRight + currentUp, currentUp, byPassIsMoving);
    }

    #endregion
}
