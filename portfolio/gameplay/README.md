# 퍼즐 게임플레이 루프와 상태 복원

**담당자:** 최성원 — 이동·타일 상태 처리, 기믹 연동, 게임 상태 복원

## 코드

**핵심 루프:** 입력 한 번을 이동 검사·색칠·기믹 처리·클리어 판정까지 순서대로 완료한 뒤 다음 입력을 받습니다. 초기화 때 원본 맵을 복사하고 시작점과 남은 타일 수를 구성합니다.

```mermaid
%%{init: {"htmlLabels": false, "themeVariables": {"fontFamily": "Arial, sans-serif"}, "flowchart": {"padding": 12, "diagramPadding": 20, "rankSpacing": 24}, "sequence": {"diagramMarginX": 24, "diagramMarginY": 16, "wrap": true}}}%%
flowchart TB
  INIT["맵 복사 · 시작점 · 남은 타일 수 설정"] --> INPUT["방향 입력"]
  INPUT --> GUARD{"입력·이동 허용?"}
  GUARD -->|아니오| INPUT
  GUARD -->|예| MOVE["Undo 저장 → 이동·색칠"]
  MOVE --> ITEM["기믹 적용 · 남은 타일 수 갱신"]
  ITEM --> CHECK{"남은 타일 0?"}
  CHECK -->|아니오| INPUT
  CHECK -->|예| CLEAR["GameManager · 모드별 클리어 처리"]
```

**PuzzlePlayer:** 입력과 이동·기믹 순서를 제어합니다. **TileHelper / PuzzleTile:** 타일 데이터 변환과 화면 표시를 나눕니다. **GameManager:** 일반 스테이지·유저 맵·에디터 수동 테스트의 클리어 후 처리를 맡습니다.

도식과 아래 코드는 기본 이동·포탈·인버터 경로를 발췌한 것입니다. 원본의 Ghost·레이저·대시는 별도 기믹 상태 샘플, 레이어 회전 분기는 회전 샘플에서 다룹니다.

```csharp
// 입력 진입 예시: 나머지 5방향도 현재 카메라의 기준 벡터를 사용한다.
private async void Update()
{
    if (dAction.action.IsPressed())
        await MoveAndPaintAsync(Vector3Int.RoundToInt(currentRight));
}

// 원본의 방향별 래퍼 + MovePlayer + ApplyItem을 기본 경로 하나로 재구성
private async UniTask MoveAndPaintAsync(Vector3Int direction)
{
    if (isMoving || isCleared || !GameManager.Instance.isPlaying) return;
    Vector3Int next = playerPosition + direction;
    if (!IsOnSurface(next)) return;
    char tile = map[next.x, next.y, next.z];
    if (tile == (char)TileType.Empty || TileHelper.IsPainted(tile)) return;

    // 이동 연출이 끝날 때까지 추가 입력이 같은 상태를 동시에 바꾸지 못하게 한다.
    isMoving = true;
    try
    {
        SaveUndoState();
        await playerModel.DOMove(next, playerMoveDuration)
            .AsyncWaitForCompletion().AsUniTask();
        playerPosition = next;

        // 색칠 전의 타일 종류로 도착한 기믹을 판정한다.
        Paint(next);
        if (tile == (char)TileType.PortalIn)
        {
            Vector3Int exit = portals[next];
            playerPosition = exit;
            playerModel.position = exit;
            Paint(exit);
        }
        else if (tile == (char)TileType.Inv)
        {
            await ApplyInverterAsync(next);
        }

        // 인버터가 남은 타일 수를 바꿀 수 있으므로 기믹 완료 뒤에 판정한다.
        CheckGameCleared();
    }
    finally
    {
        // 기믹 처리에서 예외가 나도 이동 잠금 자체는 해제한다.
        isMoving = false;
    }
}

// 타일을 색칠 상태로 바꾸고 남은 길 개수와 표시를 함께 갱신
private void Paint(Vector3Int p)
{
    char before = map[p.x, p.y, p.z];
    if (TileHelper.IsPainted(before)) return;
    map[p.x, p.y, p.z] = TileHelper.PaintTable[before];
    --roadLeftCount;
    tiles[p.x, p.y, p.z].SimpleRender(map[p.x, p.y, p.z]);
}

// 퍼즐 판정과 모드별 후처리를 분리: 수동 테스트 성공은 에디터에도 전달
private void CheckGameCleared()
{
    if (roadLeftCount != 0 || isCleared) return;
    isCleared = true;
    if (_isTesting)
    {
        MapEditor.Instance.SetValidated(true, _answer);
        GameManager.Instance.GameClearedTest(_moves);
    }
    else if (_isSingle)
        GameManager.Instance.GameClearedSingle(_moves);
    else if (_isUser)
        GameManager.Instance.GameClearedUser(_moves).Forget();
}
```

**PuzzlePlayer.SaveUndoState / Undo:** 맵뿐 아니라 포탈과 기믹 상태까지 함께 되돌립니다.

**Memento 방식의 Undo:** 변경 전 값의 사본을 스택에 보관합니다. 명령 객체를 실행·취소하는 Command 패턴이 아니라 상태 복원이며, Originator·보관 역할은 PuzzlePlayer 안에 있습니다.

```mermaid
%%{init: {"htmlLabels": false, "themeVariables": {"fontFamily": "Arial, sans-serif"}, "flowchart": {"padding": 20, "diagramPadding": 20}, "sequence": {"diagramMarginX": 24, "diagramMarginY": 16, "wrap": true}}}%%
flowchart LR
  P["이동 전 상태"] -->|깊은 복사| M["Snapshot"]
  M -->|Push| S["Undo 스택"]
  S -->|Pop| R["맵과 플레이어 및 기믹 복원"]
  R --> V["다시 표시 후 입력 대기"]
```

```csharp
private readonly Stack<(
    char[,,] Map, Vector3Int Position, int Roads, int Ghost, bool Laser,
    Dictionary<Vector3Int, Vector3Int> Portals)> undo = new();

// 유효한 이동을 확정한 시점에 변경 전 상태를 깊은 복사로 보관
private void SaveUndoState()
{
    undo.Push(((char[,,])map.Clone(), playerPosition, roadLeftCount,
        currentGhostCount, hasLaser,
        new Dictionary<Vector3Int, Vector3Int>(portals)));
}

// 최근 이동 취소: 데이터 복원 후 그 상태로 전체 타일 다시 표시
public void Undo()
{
    if (undo.Count == 0 || isMoving || isCleared
        || !GameManager.Instance.isPlaying) return;
    var previous = undo.Pop();
    map = previous.Map;
    playerPosition = previous.Position;
    playerModel.position = playerPosition;
    roadLeftCount = previous.Roads;
    currentGhostCount = previous.Ghost;
    hasLaser = previous.Laser;
    portals = previous.Portals;
    Render();
}
```

**PuzzlePlayer.InverterEffectAsync:** 타일 상태를 반전하고, 발동 위치에서 가까운 순서대로 파동을 보여줍니다.

```csharp
// 인버터: 데이터는 즉시 반전, 연출은 맨해튼 거리별로 순차 재생
private async UniTask ApplyInverterAsync(Vector3Int origin)
{
    var waves = new SortedDictionary<int, List<Vector3Int>>();
    // 반전 뒤 남은 길 수는 이전 값과 달라지므로 새 상태에서 다시 센다.
    roadLeftCount = 0;
    int size = map.GetLength(0);

    for (int x = 0; x < size; ++x)
    for (int y = 0; y < size; ++y)
    for (int z = 0; z < size; ++z)
    {
        var p = new Vector3Int(x, y, z);
        if (!IsOnSurface(p) || p == origin
            || map[x, y, z] == (char)TileType.Empty) continue;

        int distance = Mathf.Abs(x - origin.x) + Mathf.Abs(y - origin.y)
            + Mathf.Abs(z - origin.z);
        if (!waves.TryGetValue(distance, out var group))
            waves[distance] = group = new List<Vector3Int>();
        group.Add(p);

        map[x, y, z] = TileHelper.InvTable[map[x, y, z]];
        if (!TileHelper.IsPainted(map[x, y, z])) ++roadLeftCount;
    }

    var animations = new List<UniTask>();
    // SortedDictionary의 거리 오름차순으로 가까운 타일부터 연출한다.
    foreach (var group in waves.Values)
    {
        foreach (var p in group)
        {
            tiles[p.x, p.y, p.z].SimpleRender(map[p.x, p.y, p.z]);
            animations.Add(tiles[p.x, p.y, p.z].Pop(false));
        }
        await UniTask.Delay(TimeSpan.FromSeconds(waveInterval));
    }
    await UniTask.WhenAll(animations);
}
```

## 생략 및 재구성한 내용

- 원본: `Assets/Scripts/PuzzlePlayer.cs`, `PuzzleTile.cs`. 기준 커밋: `66882730c36e4c3f4e7d59f019a835ea749f39b8`.
- 루프 도식은 기본 이동·포탈·인버터 경로입니다. 원본의 방향별 이동 래퍼·MovePlayer·ApplyItem을 MoveAndPaintAsync로 합치고, 필드명과 색칠 함수를 간소화했습니다. Ghost·레이저·대시 및 회전 분기는 별도 샘플로 분리했습니다.
- 입력 코드는 6방향 중 D 방향만 발췌했습니다. InitGame 구현, 이동 불가 연출, 카메라 처리, 이동 횟수·경로 기록 갱신은 생략했습니다. CheckGameCleared의 모드 분기는 원본을 유지하되 중복 완료 방어를 추가했습니다. `_moves`·`_answer`는 원본에서 관리하는 기록이며 이 발췌에서는 갱신 코드를 표시하지 않습니다. 테스트는 플레이어의 수동 플레이 검증이며 자동 테스트가 아닙니다.
- Undo의 원본 struct를 tuple로 묶고 맵·플레이어·남은 길 수·Ghost·Laser·포탈 상태를 유지했습니다. 카메라·이동 기록·UI와 일시적 이펙트 해제 처리는 생략했습니다.
- 인버터의 그룹화와 데이터 변환을 한 번의 순회로 합치고, 거리순 정렬은 SortedDictionary로 바꿨습니다. 사운드와 전체 화면 파동 이펙트는 생략했습니다.
- 레이어 회전은 별도 샘플로 분리했습니다. 필드 초기화, `IsOnSurface`·`Render`, `TileType`·`TileHelper`, Unity·UniTask·DOTween과 타일 뷰는 원본 의존성으로 단독 실행되지 않습니다.
