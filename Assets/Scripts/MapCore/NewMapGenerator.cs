using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using UnityEngine;
using Random = System.Random;

namespace EulerianAurora.Maps
{
    public sealed class MapGenerationLimits
    {
        public int PlacementAttempts { get; set; } = MapGenerationOptions.Default.Search.PlacementAttempts;
        public int Restarts { get; set; } = MapGenerationOptions.Default.Search.Restarts;
        public int Milliseconds { get; set; } = MapGenerationOptions.Default.Search.Milliseconds;
    }

    // 초기 배치와 입력 목록은 따로 관리한다. 매 배치 후보를 실제 전이 규칙으로 재생한다.
    internal sealed class NewMapGenerator
    {
        internal const MapGimmick Supported = MapGimmick.Portal | MapGimmick.Dash | MapGimmick.Laser | MapGimmick.Ghost | MapGimmick.Inverter | MapGimmick.Rotation;
        private static readonly MapGimmick[] Kinds =
            { MapGimmick.Portal, MapGimmick.Dash, MapGimmick.Laser, MapGimmick.Ghost, MapGimmick.Inverter, MapGimmick.Rotation };
        private readonly int _size;
        private readonly GenerationPlan _plan;
        private readonly Random _random;
        private readonly Random _layoutRandom;
        private readonly CancellationToken _token;
        private readonly int _attemptLimit, _restartLimit, _milliseconds;
        private readonly GenerationDifficultyOptions _profile;
        private readonly GenerationSearchOptions _search;
        private readonly GenerationSamplingOptions _sampling;
        private readonly GenerationLayoutOptions _layout;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<Vector3Int> _surface = new();
        private readonly int[] _regionCapacity = new int[27];
        private readonly int[] _regionOccupancy = new int[27];
        private readonly List<Vector3Int> _inputs = new();
        private readonly Dictionary<Vector3Int, Vector3Int> _links = new();
        private readonly HashSet<Vector3Int> _laserTargets = new();
        private PuzzleSolver.Simulation _simulation;
        private int _rotationAxis;
        private bool[] _rotatingLayers;
        private readonly char[,,] _board;
        private readonly int[] _rotationTriggers;
        private readonly Dictionary<MapGimmick, int> _minimum = new();
        private readonly Dictionary<MapGimmick, int> _placed = new();
        private int _spent, _target, _attempts, _backtracks, _restartAttemptCeiling;
        private bool _limited, _inputLimitReached;
        private bool _checkRoadCapacity;
        private bool _preferSpacedPlacement;
        private bool[,,] _reachableRoads;
        private Vector3Int[] _roadQueue;
        private MapCandidate _candidate;

        // 요청의 설정을 고정하고, 표면 좌표와 필수 기믹 수를 준비한다.
        internal NewMapGenerator(int size, GenerationPlan plan, int seed, MapGenerationLimits limits,
            MapGenerationOptions options, CancellationToken token)
        {
            _size = size;
            _plan = plan;
            _random = new Random(seed);
            _layoutRandom = new Random(seed ^ 0x3e51a9);
            _token = token;
            _attemptLimit = limits.PlacementAttempts;
            _restartLimit = limits.Restarts;
            _milliseconds = limits.Milliseconds;
            _profile = options.For(plan.Difficulty);
            _search = options.Search;
            _sampling = options.Sampling;
            _layout = options.Layout;
            _board = new char[size, size, size];
            _rotationTriggers = new int[size];
            for (int x = 0; x < size; x++)
            for (int y = 0; y < size; y++)
            for (int z = 0; z < size; z++)
            {
                var cell = new Vector3Int(x, y, z);
                if (!cell.IsOnSurface(size)) continue;
                _surface.Add(cell);
                _regionCapacity[Region(cell)]++;
            }
            foreach (var kind in Kinds)
            {
                int ordered = 0;
                foreach (var step in plan.RequiredOrder) if (step == kind) ordered++;
                plan.Counts.TryGetValue(kind, out int exact);
                _minimum[kind] = Math.Max(exact, Math.Max((plan.RequiredGimmicks & kind) != 0 ? 1 : 0, ordered));
                _placed[kind] = 0;
            }
        }
        // 필수 비용과 공간을 먼저 검사하고 시작점을 바꿔 가며 완성 후보를 찾는다.
        internal MapGenerationResult Run()
        {
            long minimumCost = _plan.ReservedBudget, minimumCells = 1;
            if (_size == 2 && _minimum[MapGimmick.Rotation] > 0)
                return Failure(MapGenerationStatus.NoCandidateFound, "크기 2에는 회전할 내부 레이어가 없습니다.");
            foreach (var pair in _minimum)
            {
                if (_plan.Counts.TryGetValue(pair.Key, out int exact) && exact < pair.Value)
                    return Failure(MapGenerationStatus.UnsupportedConditions, "명시한 개수보다 많은 반복 사용은 아직 지원하지 않습니다.");
                long placements = PlacementCount(pair.Key, pair.Value);
                // 후속 인버터는 기존 레이저 대상 칸을 대신 사용할 수 있다.
                if (pair.Key != MapGimmick.Inverter)
                    minimumCells += placements * (pair.Key switch
                    {
                        MapGimmick.Rotation => 1, MapGimmick.Dash or MapGimmick.Ghost => 2,
                        MapGimmick.Laser => _sampling.MinimumLaserRayLength + 2, _ => 3
                    });
            }
            if (minimumCost > _plan.Budget.Maximum || minimumCells > _surface.Count)
                return Failure(MapGenerationStatus.NoCandidateFound, "기믹 이후 연결 길까지 배치하기에는 생성 예산 또는 공간이 부족합니다.");
            for (int restart = 0; restart < _restartLimit && _attempts < _attemptLimit && !Stopped(); restart++)
            {
                for (int x = 0; x < _size; x++)
                for (int y = 0; y < _size; y++)
                for (int z = 0; z < _size; z++) _board[x, y, z] = (char)TileType.Empty;
                Array.Clear(_regionOccupancy, 0, _regionOccupancy.Length);
                _links.Clear();
                _laserTargets.Clear();
                _inputs.Clear();
                _spent = 0;
                foreach (var kind in Kinds) _placed[kind] = 0;
                var start = _surface[_random.Next(_surface.Count)];
                start = ConfigureRotation(start);
                Set(start, TileType.Player);
                int minTarget = (_plan.Budget.Maximum * _profile.TargetBudgetMinPercent + 99) / 100;
                int maxTarget = _plan.Budget.Maximum * _profile.TargetBudgetMaxPercent / 100;
                int desired = _random.Next(Math.Min(minTarget, maxTarget), maxTarget + 1);
                // 작은 보드에서 모든 표면을 채우는 목표는 불가능한 전면 방문을 강제할 수 있다.
                // 예산은 상한이며 필수 배치 외의 공간에는 여유를 남긴다.
                int ordinaryCapacity = Math.Max(1, (_surface.Count - 1) * _profile.SurfaceFillMaxPercent / 100);
                _target = Math.Max((int)minimumCost, Math.Min(desired, ordinaryCapacity));
                int remainingAttempts = _attemptLimit - _attempts;
                int share = Math.Max(1, remainingAttempts / (_restartLimit - restart));
                _checkRoadCapacity = false;
                // 배치 선호로 막힌 경우 새 시작점에서는 가능한 위치를 넓게 다시 시도한다.
                _preferSpacedPlacement = restart == 0 ||
                    (_plan.AllowedGimmicks == MapGimmick.None && restart < _restartLimit - 2);
                if (_plan.AllowedGimmicks == MapGimmick.None)
                {
                    int pathLength = RemainingRoadCount();
                    // 각 배치가 한 칸씩만 늘리는 경우, 목표 길이와 되돌림에 필요한 시도를 한 시작점에 배정한다.
                    if (pathLength > share)
                    {
                        share = pathLength + share / 2;
                        _checkRoadCapacity = true;
                    }
                    // 표면 절반 이상을 써야 하는 경로에서는 충분한 경로 길이 확보를 우선한다.
                    if (pathLength * 2 >= _surface.Count - 1) _preferSpacedPlacement = false;
                }
                _restartAttemptCeiling = _attempts + Math.Min(remainingAttempts, share);
                if (_simulation.Reset(_board, _links) && Explore(_simulation))
                {
                    string level = _plan.Difficulty switch { MapDifficulty.Easy => "쉬움", MapDifficulty.Medium => "보통", _ => "어려움" };
                    return new MapGenerationResult(MapGenerationStatus.CandidateFound, _candidate,
                        $"신규 맵 · {level} · 비용 {_candidate.GenerationCost}/{_plan.Budget.Maximum}\n" +
                        $"포탈 {_candidate.Map.PortalPairs.Length}쌍 · 대시 {CountDash(_candidate.Map.Tiles)}개 · 레이저 {CountTile(_candidate.Map.Tiles, TileType.Laser)}개 · 고스트 {CountTile(_candidate.Map.Tiles, TileType.Ghost)}개 · 인버터 {CountTile(_candidate.Map.Tiles, TileType.Inv)}개\n" +
                        (_candidate.Map.RotationAxis == 0 ? "" : "회전 사용\n") +
                        $"입력 {_candidate.Witness.SolutionInputs.Count}회 · 클리어 재생 확인", _plan);
                }
            }
            _limited |= _attempts >= _attemptLimit || _inputLimitReached;
            return Failure(_limited ? MapGenerationStatus.SearchLimitReached : MapGenerationStatus.NoCandidateFound,
                _limited ? "생성 탐색 한도에 도달했습니다. 후보를 찾지 못했으며 불가능 판정은 아닙니다. 다시 시도해 주세요."
                    : "이번 생성 시도에서 조건을 만족하는 후보를 찾지 못했습니다. 다른 시드로 다시 시도할 수 있습니다.");
        }

        // 필수 조건과 준비 확률에 따라 축, 시작점, 초기 회전 묶음을 정한다.
        private Vector3Int ConfigureRotation(Vector3Int start)
        {
            bool required = _minimum[MapGimmick.Rotation] > 0;
            int priorAxis = _rotationAxis;
            _rotationAxis = 0;
            _rotatingLayers = null;
            if (_size < 3 || !(required || CanPlace(MapGimmick.Rotation, 0) && _random.Next(100) < _profile.OptionalRotationPercent))
            {
                if (_simulation == null || priorAxis != 0) _simulation = new PuzzleSolver.Simulation(_size);
                return start;
            }

            int axis = PickWeightedIndex(_sampling.XAxisWeight, _sampling.YAxisWeight, _sampling.ZAxisWeight);
            for (int i = 0; i < 2 && (start[axis] == 0 || start[axis] == _size - 1); i++)
                axis = (axis + 1) % 3;
            _rotationAxis = axis + 1;
            _rotatingLayers = SampleRotationGroups(_size, start[axis], _profile, _sampling.RotationGroupGap,
                _random, out int startLayer);
            start[axis] = startLayer;
            _simulation = new PuzzleSolver.Simulation(_size, _rotationAxis, _rotatingLayers);
            return start;
        }

        // 고정 레이어 간격을 남기면서 같은 길이 규칙으로 내부 회전 묶음을 배치한다.
        internal static bool[] SampleRotationGroups(int size, int initialLayer, GenerationDifficultyOptions profile,
            int gap, Random random, out int startLayer)
        {
            int minimum = Math.Min(profile.RotationGroupMinLength, size - 2);
            var layers = new bool[size];
            var free = new List<(int First, int Last)> { (1, size - 2) };
            startLayer = PlaceGroup(0, initialLayer);
            while (free.Count > 0 && profile.AdditionalRotationGroupPercent > 0 &&
                random.Next(100) < profile.AdditionalRotationGroupPercent)
            {
                int slot = random.Next(free.Count);
                var span = free[slot];
                PlaceGroup(slot, random.Next(span.First, span.Last + 1));
            }
            return layers;

            // 한 빈 구간에 묶음을 놓고 다음 묶음을 넣을 수 있는 좌우 구간을 남긴다.
            int PlaceGroup(int slot, int anchor)
            {
                var (minLayer, maxLayer) = free[slot];
                free.RemoveAt(slot);
                int length = random.Next(minimum, Math.Min(profile.RotationGroupMaxLength, maxLayer - minLayer + 1) + 1);
                int offset = profile.StartInsideRotationGroup && length > 2
                    ? random.Next(1, length - 1) : random.Next(length);
                int first = Math.Clamp(anchor - offset, minLayer, maxLayer - length + 1);
                Array.Fill(layers, true, first, length);

                // 묶음 사이의 고정 레이어를 예약하고, 같은 최소 길이를 넣을 공간만 남긴다.
                int before = first - gap - 1;
                int after = first + length + gap;
                if (before - minLayer + 1 >= minimum) free.Add((minLayer, before));
                if (maxLayer - after + 1 >= minimum) free.Add((after, maxLayer));
                return first + offset;
            }
        }

        // 실제 사용한 묶음과 풀이에 필요한 레이어만 최종 설정에 남긴다.
        private bool[] RetainUsedRotationLayers()
        {
            int axis = _rotationAxis - 1;
            var occupied = new bool[_size];
            foreach (var cell in _surface)
                if (InitialTile(cell) != (char)TileType.Empty) occupied[cell[axis]] = true;

            var retained = new bool[_size];
            for (int first = 1; first < _size - 1;)
            {
                if (!_rotatingLayers[first]) { first++; continue; }
                int end = first;
                bool turned = false;
                int firstOccupied = -1, lastOccupied = -1;
                while (end < _size - 1 && _rotatingLayers[end])
                {
                    turned |= _rotationTriggers[end] > 0;
                    if (occupied[end])
                    {
                        if (firstOccupied < 0) firstOccupied = end;
                        lastOccupied = end;
                    }
                    end++;
                }
                // 함께 돌던 타일을 보존한다. 가운데 빈 레이어는 묶음의 전파를 이어 준다.
                if (turned && firstOccupied >= 0)
                    Array.Fill(retained, true, firstOccupied, lastOccupied - firstOccupied + 1);
                first = end;
            }
            return retained;
        }

        // 호출 스택 대신 명시적 프레임 스택으로 배치 후보를 깊이 우선 탐색한다.
        private bool Explore(PuzzleSolver.Simulation root)
        {
            // 긴 경로도 Unity 작업 스레드의 호출 스택 크기에 의존하지 않도록 탐색 상태를 보관한다.
            var frames = new Stack<SearchFrame>();
            frames.Push(new SearchFrame(root, ExploreChoices(root).GetEnumerator()));
            try
            {
                while (frames.Count > 0 && !Stopped())
                {
                    var frame = frames.Peek();
                    if (frame.Choices.MoveNext())
                    {
                        var next = TryDescend(frame.Cursor, frame.Choices.Current);
                        if (next != null) frames.Push(next);
                        continue;
                    }
                    if (_candidate != null) return true;
                    if (frames.Count > 1) _backtracks++;
                    PopFrame(frames);
                }
                return false;
            }
            finally
            {
                while (frames.Count > 0) PopFrame(frames);
            }
        }

        // 프레임을 닫고 부모 상태에 맞춰 적용한 변경을 되돌린다.
        private void PopFrame(Stack<SearchFrame> frames)
        {
            var frame = frames.Pop();
            frame.Choices.Dispose();
            if (frames.Count > 0)
                UndoSearchChoice(frame.Placement, frames.Peek().Cursor, frame.PreviousInputCount, frame.Cost);
        }

        // 완성 조건을 먼저 검사하고 현재 상태에서 가능한 다음 배치와 이동을 열거한다.
        private IEnumerable<SearchChoice> ExploreChoices(PuzzleSolver.Simulation cursor, bool rotatedLast = false)
        {
            if (Stopped()) yield break;
            int orderProgress = OrderProgress(cursor);
            if (_spent >= _target && cursor.Unpainted == 0 && cursor.GhostRemaining == 0 &&
                RequirementsMet(orderProgress) && _inputs.Count > 0)
            {
                // 마지막 입력 이후 실제로 모든 칸이 칠해지는지 기존 솔버의 완전 재생으로 확인한다.
                // 자동 후보에서 회전을 쓴 경우에만 편집기에 레이어 설정을 전달한다.
                int axis = _placed[MapGimmick.Rotation] > 0 ? _rotationAxis : 0;
                var layers = axis == 0 ? null : RetainUsedRotationLayers();
                var witness = PuzzleSolver.VerifySequence(_board, _links, _inputs, axis, layers);
                if (Stopped()) yield break;
                if (witness.IsSolvable && (_plan.RequiredGimmicks & witness.UsedGimmicks) == _plan.RequiredGimmicks)
                {
                    _candidate = new MapCandidate(MapDocument.FromBoard(_board, _links, axis, layers), witness,
                        _spent, _attempts, _backtracks);
                }
                yield break;
            }
            if (_inputs.Count >= _search.InputLimit)
            {
                _inputLimitReached = true;
                yield break;
            }

            var position = cursor.Position;
            int inverted = _placed[MapGimmick.Inverter];
            bool odd = (inverted & 1) != 0;
            bool canInvert = CanPlace(MapGimmick.Inverter, orderProgress);
            var rotations = new List<Placement>();
            if (_rotationAxis != 0 && !rotatedLast)
                AddRotationOptions(cursor, orderProgress, !odd, rotations);
            if (inverted > 0 && cursor.Unpainted > 0)
            {
                bool canReplaceRoad = _laserTargets.Count > 0 && canInvert;
                if (odd && !canReplaceRoad && cursor.CannotFinish()) yield break;
                if (odd && _placed[MapGimmick.Rotation] < _minimum[MapGimmick.Rotation])
                {
                    foreach (var choice in RotationChoices(rotations, cursor)) yield return choice;
                }
                // 반전으로 열린 칸에는 방문한 길과 레이저로만 칠한 길이 모두 포함된다.
                int first = _random.Next(TileHelper.Directions.Length);
                for (int i = 0; i < TileHelper.Directions.Length; i++)
                {
                    var direction = TileHelper.Directions[(first + i) % TileHelper.Directions.Length];
                    var entry = position + direction;
                    if (canReplaceRoad && entry.IsOnSurface(_size) && _laserTargets.Contains(cursor.InitialCell(entry)) &&
                        InitialTile(cursor.InitialCell(entry)) == (char)TileType.Road && cursor.TileAt(entry) == (char)TileType.Road)
                        yield return new SearchChoice(new Placement(MapGimmick.Inverter, entry, direction, default, default, replacesRoad: true));
                }
                for (int i = 0; i < TileHelper.Directions.Length; i++)
                {
                    var direction = TileHelper.Directions[(first + i) % TileHelper.Directions.Length];
                    var entry = position + direction;
                    if (IsOpen(cursor, entry)) yield return new SearchChoice(direction);
                }
            }
            // 홀수 번째 반전 뒤에는 뒤늦게 추가한 미색칠 칸도 닫혀 있다.
            if (odd)
            {
                foreach (var choice in RotationChoices(rotations, cursor)) yield return choice;
                yield break;
            }

            var roads = new List<Placement>();
            var portals = new List<Placement>();
            var dashes = new List<Placement>();
            var lasers = new List<Placement>();
            var ghosts = new List<Placement>();
            var inverters = new List<Placement>();
            bool wantsAnotherInverter = inverted + 1 < _minimum[MapGimmick.Inverter];
            long laterRotationCost = orderProgress < _plan.RequiredOrder.Count &&
                _plan.RequiredOrder[orderProgress] == MapGimmick.Inverter
                ? PlacementCount(MapGimmick.Rotation, _minimum[MapGimmick.Rotation] - _placed[MapGimmick.Rotation]) *
                    _plan.Budget.Cost(MapGimmick.Rotation)
                : 0;
            // 마지막 인버터 뒤에는 새 칸을 이어 붙이기 어려우므로 먼저 목표 비용에 접근한다.
            bool addInverter = canInvert && (wantsAnotherInverter ? _laserTargets.Count > 0 :
                _spent + PlacementCost(MapGimmick.Inverter) + laterRotationCost >= _target);
            bool canRoad = Affordable(MapGimmick.None);
            bool canGhost = cursor.Unpainted == 0 && CanPlace(MapGimmick.Ghost, orderProgress);
            bool canDash = CanPlace(MapGimmick.Dash, orderProgress);
            bool canLaser = CanPlace(MapGimmick.Laser, orderProgress);
            bool canPortal = CanPlace(MapGimmick.Portal, orderProgress);
            foreach (var direction in TileHelper.Directions)
            {
                var entry = position + direction;
                if (!Empty(entry, cursor)) continue;
                if (canRoad) roads.Add(new Placement(entry, direction));
                if (addInverter) inverters.Add(new Placement(MapGimmick.Inverter, entry, direction, default, default));
                if (canGhost) AddGhostOptions(entry, direction, ghosts, cursor);
                if (canDash)
                    foreach (var onward in TileHelper.Directions)
                    {
                        var landing = entry + onward;
                        if (Empty(landing, cursor)) dashes.Add(new Placement(MapGimmick.Dash, entry, direction, landing, onward));
                    }
                if (canLaser)
                    foreach (var shot in TileHelper.Directions)
                    {
                        // 발사 방향의 빈 직선 길과 발사 후 이동할 별도 길을 확보한다.
                        for (int length = 1; length <= _sampling.MaximumLaserRayLength; length++)
                        {
                            if (!Empty(entry + shot * length, cursor)) break;
                            if (length < _sampling.MinimumLaserRayLength) continue;
                            foreach (var escape in TileHelper.Directions)
                            {
                                if (escape == shot || !Empty(entry + escape, cursor)) continue;
                                lasers.Add(new Placement(MapGimmick.Laser, entry, direction, entry + escape, shot, length));
                            }
                        }
                    }
                if (canPortal) AddPortalOptions(entry, direction, inverted, portals, cursor);
            }
            while (!Stopped())
            {
                if (_attempts >= _restartAttemptCeiling) break;
                MapGimmick feasible = MapGimmick.None;
                if (portals.Count > 0) feasible |= MapGimmick.Portal;
                if (dashes.Count > 0) feasible |= MapGimmick.Dash;
                if (lasers.Count > 0) feasible |= MapGimmick.Laser;
                if (ghosts.Count > 0) feasible |= MapGimmick.Ghost;
                if (inverters.Count > 0) feasible |= MapGimmick.Inverter;
                if (rotations.Count > 0) feasible |= MapGimmick.Rotation;
                MapGimmick kind;
                if (rotations.Count > 0 && _placed[MapGimmick.Rotation] < _minimum[MapGimmick.Rotation])
                    kind = MapGimmick.Rotation;
                else if (inverters.Count > 0 && inverted < _minimum[MapGimmick.Inverter])
                    kind = MapGimmick.Inverter;
                else if (!_plan.Budget.TryPick(_random, Supported, _plan.Budget.Maximum - _spent,
                    roads.Count > 0, feasible, out kind))
                    break;
                var options = kind switch { MapGimmick.Portal => portals, MapGimmick.Dash => dashes,
                    MapGimmick.Laser => lasers, MapGimmick.Ghost => ghosts, MapGimmick.Inverter => inverters,
                    MapGimmick.Rotation => rotations, _ => roads };
                int pick = PickPlacement(options, kind, inverted, cursor);
                var action = options[pick];
                options.RemoveAt(pick);
                yield return new SearchChoice(action);
            }
        }

        // 특수 연결을 먼저 추가하고 일반 출구는 입구별 샘플 한도까지만 찾는다.
        private void AddPortalOptions(Vector3Int entry, Vector3Int direction, int inverted,
            List<Placement> portals, PuzzleSolver.Simulation cursor)
        {
            if (inverted < _minimum[MapGimmick.Inverter] && _placed[MapGimmick.Portal] == 0)
            {
                // 출구에서 시작 쪽으로 이어지는 이웃을 확보하면 반전 뒤 입구 쪽도 방문할 수 있다.
                foreach (var exitDirection in TileHelper.Directions)
                {
                    var exit = cursor.CurrentCell(_simulation.Position) + exitDirection;
                    if (!CanPlacePortal(entry, exit, cursor)) continue;
                    foreach (var onward in TileHelper.Directions)
                        if (Empty(exit + onward, cursor))
                            portals.Add(new Placement(MapGimmick.Portal, entry, direction, exit, onward));
                }
            }
            // 출구는 입구와 인접하지 않은 표면에서 고른다. 작은 고정 개수만 샘플링한다.
            int first = _random.Next(_surface.Count);
            int found = 0;
            for (int offset = 0; offset < _surface.Count && found < _search.PortalExitSamplesPerEntrance; offset++)
            {
                var exit = _surface[(first + offset) % _surface.Count];
                if (!CanPlacePortal(entry, exit, cursor)) continue;
                int directionStart = _random.Next(TileHelper.Directions.Length);
                for (int d = 0; d < TileHelper.Directions.Length; d++)
                {
                    var onward = TileHelper.Directions[(directionStart + d) % TileHelper.Directions.Length];
                    var tail = exit + onward;
                    if (!Empty(tail, cursor) || tail == entry) continue;
                    portals.Add(new Placement(MapGimmick.Portal, entry, direction, exit, onward));
                    found++;
                    break;
                }
            }
        }

        // 기믹 종류가 정해진 뒤 회전 횟수와 반전 준비 여부에 맞는 위치를 선택한다.
        private int PickPlacement(List<Placement> options, MapGimmick kind, int inverted, PuzzleSolver.Simulation cursor)
        {
            if (kind == MapGimmick.Rotation) return PickRotation(options, cursor);
            bool prepareInverter = _minimum[MapGimmick.Inverter] - inverted == 1 &&
                kind is MapGimmick.None or MapGimmick.Laser or MapGimmick.Portal;
            return PickSpacedPlacement(options, cursor, _random.Next(options.Count), prepareInverter);
        }

        // 필수 연결 준비를 우선하고 기존 배치와 방향 점수로 후보의 순서를 정한다.
        private int PickSpacedPlacement(List<Placement> options, PuzzleSolver.Simulation cursor, int first,
            bool prepareInverter = false, int turns = 0)
        {
            if (!_preferSpacedPlacement && !prepareInverter) return first;
            int best = first, bestScore = int.MinValue;
            bool bestConnects = false;
            for (int i = 0; i < options.Count; i++)
            {
                int index = (first + i) % options.Count;
                var action = options[index];
                if (turns > 0 && action.Turns != turns) continue;
                bool connects = prepareInverter && PreparesInverterConnection(action, cursor);
                if (bestConnects && !connects) continue;
                int score = _preferSpacedPlacement
                    ? PlacementSpacing(action, cursor) + RoadBendScore(action, cursor) +
                        _layoutRandom.Next(_layout.ScoreVariation * 100 + 1) : 0;
                if (connects == bestConnects && score <= bestScore) continue;
                (best, bestScore, bestConnects) = (index, score, connects);
            }
            return best;
        }

        // 다음 인버터가 레이저로 칠한 경로에 접근할 수 있도록 연결 후보를 구별한다.
        private bool PreparesInverterConnection(Placement action, PuzzleSolver.Simulation cursor)
        {
            // 반전 뒤 레이저 대상 구간에 접근할 수 있는 배치를 우선한다.
            if (action.Kind == MapGimmick.Portal)
                return Distance(cursor.CurrentCell(_simulation.Position), action.Exit) == 1;
            if (action.Kind == MapGimmick.Laser && action.RayLength == _sampling.PreferredInverterRayLength)
            {
                var parallel = action.Exit + action.Onward;
                if (parallel == action.Entry) return false;
                for (int i = 0; i < action.RayLength; i++)
                    if (!Empty(parallel + action.Onward * i, cursor)) return false;
                return true;
            }
            if (action.Kind == MapGimmick.None)
                foreach (var direction in TileHelper.Directions)
                {
                    var neighbor = action.Entry + direction;
                    if (neighbor.IsOnSurface(_size) && _laserTargets.Contains(cursor.InitialCell(neighbor))) return true;
                }
            return false;
        }

        // 초기 좌표에서 배치에 포함된 칸들의 공간 점수를 평균낸다.
        private int PlacementSpacing(Placement action, PuzzleSolver.Simulation cursor)
        {
            var previous = cursor.InitialCell(cursor.Position);
            if (action.Kind == MapGimmick.Rotation)
                return action.RotationOnly ? 0 : 100 * TileSpacing(action.Exit, previous);

            var entry = cursor.InitialCell(action.Entry);
            int score = TileSpacing(entry, previous);
            if (action.Kind is MapGimmick.None or MapGimmick.Inverter) return 100 * score;

            var exit = cursor.InitialCell(action.Exit);
            var approach = action.Kind == MapGimmick.Ghost
                ? cursor.InitialCell(action.Exit - action.GhostInputs[action.GhostInputs.Length - 1])
                : entry;
            score += TileSpacing(exit, action.Kind == MapGimmick.Portal ? null : approach);
            if (action.Kind == MapGimmick.Portal)
                return 100 * (score + TileSpacing(cursor.InitialCell(action.Exit + action.Onward), exit)) / 3;
            if (action.Kind == MapGimmick.Laser)
            {
                for (int i = 1; i <= action.RayLength; i++)
                    score += TileSpacing(cursor.InitialCell(action.Entry + action.Onward * i), null);
                return 100 * score / (action.RayLength + 2);
            }
            return 100 * score / 2;
        }

        // 구역 점유율, 추가 이웃 수, 완성되는 2×2 덩어리로 감점을 계산한다.
        private int TileSpacing(Vector3Int cell, Vector3Int? connected)
        {
            int region = Region(cell);
            int score = -_layout.RegionPenalty * _regionOccupancy[region] / _regionCapacity[region];
            int contacts = 0;
            var directions = TileHelper.Directions;
            for (int i = 0; i < directions.Length; i++)
            {
                var neighbor = cell + directions[i];
                if (!Occupied(neighbor)) continue;
                if (neighbor != connected) contacts++;
                // 다른 축의 이웃과 대각선까지 차 있으면 이 칸이 2×2를 닫는다.
                for (int j = (i / 2 + 1) * 2; j < directions.Length; j++)
                    if (Occupied(cell + directions[j]) && Occupied(neighbor + directions[j])) score -= _layout.SquarePenalty;
            }
            if (contacts > 0) score -= _layout.ContactPenalty + (contacts - 1) * _layout.CrowdingPenalty;
            return score;
        }

        // 최근 일반 길을 따라가며 긴 직진과 한 칸마다 꺾이는 배치를 감점한다.
        private int RoadBendScore(Placement action, PuzzleSolver.Simulation cursor)
        {
            // 기믹 입력(발사/회전 등)을 직선 길로 세지 않는다. 현재 좌표계에서 일반 길만 역추적한다.
            if (action.Kind != MapGimmick.None || _inputs.Count == 0 || _layout.BendPenalty == 0) return 0;
            var direction = _inputs[_inputs.Count - 1];
            var cell = cursor.Position;
            int run = 0;
            for (int i = _inputs.Count - 1; i >= 0 && _inputs[i] == direction; i--)
            {
                if (!cell.IsInBounds(_size) || InitialTile(cursor.InitialCell(cell)) != (char)TileType.Road) break;
                cell -= direction;
                run++;
            }
            if (run == 0) return 0;
            int penalty = action.Input == direction ? Math.Min(3, Math.Max(0, run - _layout.StraightRunLength + 1))
                : run == 1 ? 1 : 0;
            return -100 * _layout.BendPenalty * penalty;
        }

        // 초기 좌표에 실제 타일이 있는지 범위를 포함해 검사한다.
        private bool Occupied(Vector3Int cell) => cell.IsInBounds(_size) && InitialTile(cell) != (char)TileType.Empty;

        // 표면 좌표를 축마다 3등분한 구역 번호로 변환한다.
        private int Region(Vector3Int cell) =>
            (cell.x * 3 / _size * 3 + cell.y * 3 / _size) * 3 + cell.z * 3 / _size;

        // 요청한 횟수 또는 공통 가중치로 연속 회전 수를 고른 뒤 배치를 선택한다.
        private int PickRotation(List<Placement> options, PuzzleSolver.Simulation cursor)
        {
            Span<int> counts = stackalloc int[4];
            counts.Clear();
            foreach (var option in options) counts[option.Turns]++;
            int desired = Math.Min(_sampling.MaximumConsecutiveTurns, _minimum[MapGimmick.Rotation] - _placed[MapGimmick.Rotation]);
            if (desired <= 0 || counts[desired] == 0 || !_plan.Counts.ContainsKey(MapGimmick.Rotation))
            {
                // 회전 횟수 가중치는 모든 난이도가 공유한다.
                desired = 1 + PickWeightedIndex(counts[1] > 0 ? _sampling.OneTurnWeight : 0,
                    counts[2] > 0 ? _sampling.TwoTurnWeight : 0, counts[3] > 0 ? _sampling.ThreeTurnWeight : 0);
            }
            int pick = _random.Next(counts[desired]);
            for (int i = 0; i < options.Count; i++)
                if (options[i].Turns == desired && pick-- == 0)
                    return PickSpacedPlacement(options, cursor, i, turns: desired);
            throw new InvalidOperationException("선택한 회전 횟수의 후보가 없습니다.");
        }

        // 세 선택지의 상대 가중치 비율로 인덱스를 하나 뽑는다.
        private int PickWeightedIndex(int first, int second, int third)
        {
            int draw = _random.Next(first + second + third);
            return draw < first ? 0 : draw < first + second ? 1 : 2;
        }

        // 일반 배치 대신 회전만 가능한 상태에서 회전 후보를 차례로 제공한다.
        private IEnumerable<SearchChoice> RotationChoices(List<Placement> options, PuzzleSolver.Simulation cursor)
        {
            while (options.Count > 0 && !Stopped() && _attempts < _restartAttemptCeiling)
            {
                int pick = PickRotation(options, cursor);
                var action = options[pick];
                options.RemoveAt(pick);
                yield return new SearchChoice(action);
            }
        }

        // 시뮬레이션을 돌려 본 뒤 실제로 연결할 수 있는 출구를 후보로 만든다.
        private void AddRotationOptions(PuzzleSolver.Simulation cursor, int orderProgress, bool allowNewRoad,
            List<Placement> options)
        {
            int axis = _rotationAxis - 1;
            if (!_rotatingLayers[cursor.Position[axis]] || cursor.GhostRemaining != 0) return;
            bool useExistingRoad = cursor.Unpainted > 0;
            int credit = useExistingRoad ? _plan.Budget.Cost(MapGimmick.None) : 0;
            int limit = _sampling.MaximumConsecutiveTurns;
            if (_plan.Counts.TryGetValue(MapGimmick.Rotation, out int exact))
            {
                int first = orderProgress;
                while (first < _plan.RequiredOrder.Count && _plan.RequiredOrder[first] == MapGimmick.Rotation) first++;
                int later = 0;
                for (int i = first; i < _plan.RequiredOrder.Count; i++)
                    if (_plan.RequiredOrder[i] == MapGimmick.Rotation) later++;
                limit = Math.Min(limit, exact - _placed[MapGimmick.Rotation] - later);
            }
            if (limit <= 0 || !CanPlace(MapGimmick.Rotation, orderProgress, credit, amount: limit)) return;

            var inputs = new Vector3Int[limit];
            AddTurns(cursor, previous: null, depth: 0);

            // 한 번 더 돌려 본 뒤 그 상태에서 나갈 수 있는 길과 연속 회전 후보를 찾는다.
            void AddTurns(PuzzleSolver.Simulation before, Vector3Int? previous, int depth)
            {
                foreach (var input in TileHelper.Directions)
                {
                    if (input[axis] != 0) continue;
                    var turned = before.Fork();
                    if (!turned.TryStep(input, rotationPreview: true) ||
                        !MatchesRotationCount(turned, before.Events.Count, 1) ||
                        turned.Position == previous || turned.Position[axis] != cursor.Position[axis]) continue;
                    inputs[depth] = input;
                    int amount = depth + 1;
                    var turns = inputs.AsSpan(0, amount).ToArray();
                    if (allowNewRoad && Affordable(MapGimmick.Rotation, amount: amount))
                    {
                        foreach (var onward in TileHelper.Directions)
                        {
                            var exit = turned.Position + onward;
                            if (onward[axis] != 0 && Empty(exit, turned) && !_rotatingLayers[exit[axis]])
                                options.Add(new Placement(MapGimmick.Rotation, default, default, exit, onward, rotationInputs: turns));
                        }
                    }
                    if (useExistingRoad && Affordable(MapGimmick.Rotation, credit: credit, amount: amount))
                        foreach (var onward in TileHelper.Directions)
                            if (IsOpen(turned, turned.Position + onward))
                            {
                                options.Add(new Placement(MapGimmick.Rotation, default, default, default, default,
                                    rotationOnly: true, rotationInputs: turns));
                                break;
                            }
                    // 방향을 되돌리면 직전 회전이 상쇄된다. 같은 방향으로 최대 세 번까지 이어 돌린다.
                    if (amount < limit) AddTurns(turned, before.Position, amount);
                }
            }
        }

        // 회전과 반전이 적용된 현재 보드에서 칠하지 않은 길인지 확인한다.
        private bool IsOpen(PuzzleSolver.Simulation cursor, Vector3Int cell)
        {
            if (!cell.IsOnSurface(_size)) return false;
            char tile = cursor.TileAt(cell);
            return tile != (char)TileType.Empty && !TileHelper.IsPainted(tile);
        }

        // 출구가 입구와 구분되고 순간이동을 안전하게 구성할 수 있는지 확인한다.
        private bool CanPlacePortal(Vector3Int entry, Vector3Int exit, PuzzleSolver.Simulation cursor)
        {
            if (!Empty(exit, cursor) || Distance(entry, exit) <= 1) return false;
            if (_rotationAxis == 0) return true;
            var a = cursor.InitialCell(entry);
            var b = cursor.InitialCell(exit);
            if (Distance(a, b) <= 1 || cursor.SharePortalOrbit(a, b)) return false;
            // 게임의 포탈 연결 갱신 중 같은 회전 궤도의 좌표를 덮어쓰지 않도록 한다.
            foreach (var endpoint in _links.Keys)
                if (cursor.SharePortalOrbit(a, endpoint) || cursor.SharePortalOrbit(b, endpoint)) return false;
            return true;
        }

        // 전체 및 현재 시작점의 제한 안에서 탐색 시도를 한 번 차감한다.
        private bool TakeAttempt()
        {
            if (Stopped() || _attempts >= _restartAttemptCeiling) return false;
            _attempts++;
            return true;
        }

        // 배치를 적용해 입력을 검증하고, 실패하면 변경을 복원한다.
        private SearchFrame TryDescend(PuzzleSolver.Simulation cursor, SearchChoice choice)
        {
            if (!TakeAttempt()) return null;
            int previousInputCount = _inputs.Count;
            if (!choice.Placement.HasValue)
            {
                var next = cursor.Fork();
                if (!next.TryStep(choice.Input) || !MatchesRotationCount(next, cursor.Events.Count, 0)) return null;
                _inputs.Add(choice.Input);
                return new SearchFrame(next, ExploreChoices(next).GetEnumerator(), previousInputCount: previousInputCount);
            }

            var action = choice.Placement.Value;
            int cost = PlacementCost(action.Kind) -
                (action.RotationOnly ? _plan.Budget.Cost(MapGimmick.None) : 0);
            Apply(action, cursor);
            _spent += cost;
            SearchFrame frame = null;
            try
            {
                if (_inputs.Count > _search.InputLimit)
                {
                    _inputLimitReached = true;
                    return null;
                }
                bool roadsOnly = _plan.AllowedGimmicks == MapGimmick.None;
                if (_checkRoadCapacity && !HasRoomForRoads(action.Entry))
                {
                    _backtracks++;
                    return null;
                }
                var replay = cursor.Fork(copyState: roadsOnly);
                bool valid = roadsOnly ? replay.TryAppendRoad(action.Input) : ReplayPrefix(replay, cursor, previousInputCount, action);
                if (valid &&
                    (action.Kind != MapGimmick.Rotation || action.RotationOnly || replay.Position == action.Exit))
                    frame = new SearchFrame(replay, ExploreChoices(replay, action.RotationOnly).GetEnumerator(),
                        action, previousInputCount, cost);
                else _backtracks++;
                return frame;
            }
            finally
            {
                if (frame == null) UndoSearchChoice(action, cursor, previousInputCount, cost);
            }
        }

        // 선택 전의 배치, 입력 수와 소비 비용을 함께 복구한다.
        private void UndoSearchChoice(Placement? action, PuzzleSolver.Simulation cursor, int previousInputCount, int cost)
        {
            if (action.HasValue) Undo(action.Value, cursor);
            _inputs.RemoveRange(previousInputCount, _inputs.Count - previousInputCount);
            _spent -= cost;
        }

        // 목표 비용을 일반 길로 채우는 데 필요한 추가 칸 수를 계산한다.
        private int RemainingRoadCount()
        {
            int cost = _plan.Budget.Cost(MapGimmick.None);
            return (Math.Max(0, _target - _spent) + cost - 1) / cost;
        }

        // 도달 가능한 빈 표면 칸의 수로 긴 경로의 불가능한 분기를 걸러낸다.
        private bool HasRoomForRoads(Vector3Int end)
        {
            int required = RemainingRoadCount();
            if (required == 0) return true;
            // 길이 목표를 채울 수 없는 고립된 빈 공간에서는 더 깊이 탐색하지 않는다.
            _reachableRoads ??= new bool[_size, _size, _size];
            _roadQueue ??= new Vector3Int[_surface.Count];
            Array.Clear(_reachableRoads, 0, _reachableRoads.Length);
            _roadQueue[0] = end;
            int tail = 1;
            for (int head = 0; head < tail; head++)
            foreach (var direction in TileHelper.Directions)
            {
                var cell = _roadQueue[head] + direction;
                if (!cell.IsOnSurface(_size) || InitialTile(cell) != (char)TileType.Empty ||
                    _reachableRoads[cell.x, cell.y, cell.z]) continue;
                if (--required == 0) return true;
                _reachableRoads[cell.x, cell.y, cell.z] = true;
                _roadQueue[tail++] = cell;
            }
            return false;
        }

        // 변경된 초기 맵에서 정답 접두부를 재생하고 기존 사건과 회전 상태가 유지되는지 확인한다.
        private bool ReplayPrefix(PuzzleSolver.Simulation replay, PuzzleSolver.Simulation cursor,
            int previousInputCount, Placement action)
        {
            if (!replay.Reset(_board, _links)) return false;
            for (int i = 0; i < _inputs.Count; i++)
            {
                if (i == previousInputCount && (replay.Position != cursor.Position || !replay.SameLayerTurns(cursor) ||
                    !SameEvents(replay.Events, cursor.Events))) return false;
                int eventCount = replay.Events.Count;
                if (Stopped() || !replay.TryReplayStep(_inputs[i])) return false;
                if (i >= previousInputCount && i - previousInputCount < action.Turns &&
                    (replay.Events.Count != eventCount + 1 || replay.Events[eventCount] != MapGimmick.Rotation ||
                     replay.InitialCell(replay.Position) != cursor.InitialCell(cursor.Position))) return false;
            }
            var kind = action.Kind;
            if (kind != MapGimmick.None)
            {
                bool used = false;
                for (int i = cursor.Events.Count; i < replay.Events.Count; i++)
                    if (replay.Events[i] == kind) used = true;
                if (!used) return false;
            }
            return MatchesRotationCount(replay, cursor.Events.Count, action.Turns) &&
                replay.GhostRemaining == 0 && (_placed[MapGimmick.Inverter] > 0 || replay.Unpainted == 0);
        }

        // 새로 생긴 이벤트 구간의 실제 회전 횟수를 검사한다.
        private static bool MatchesRotationCount(PuzzleSolver.Simulation cursor, int firstEvent, int expected)
        {
            int count = 0;
            for (int i = firstEvent; i < cursor.Events.Count; i++)
                if (cursor.Events[i] == MapGimmick.Rotation) count++;
            return count == expected;
        }

        // 두 입력 구간이 같은 순서로 같은 기믹을 발동했는지 검사한다.
        private static bool SameEvents(IReadOnlyList<MapGimmick> a, IReadOnlyList<MapGimmick> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // 실제 발동 이벤트가 충족한 필수 순서의 길이를 구한다.
        private int OrderProgress(PuzzleSolver.Simulation cursor)
        {
            int progress = 0;
            foreach (var used in cursor.Events)
                if (progress < _plan.RequiredOrder.Count && used == _plan.RequiredOrder[progress]) progress++;
            return progress;
        }

        // 이동 횟수와 후보 탐색 한도를 지키며 마지막에 새 길로 나가는 경로를 수집한다.
        private void AddGhostOptions(Vector3Int entry, Vector3Int input, List<Placement> options, PuzzleSolver.Simulation cursor)
        {
            // 현재 접두 경로는 모두 칠해져 있다. 효과가 지속되는 동안 기존 칸을 되짚고,
            // 마지막 이동에서만 새 길을 칠한다. 같은 칸을 다시 방문하는 이동도 허용한다.
            var moves = new Vector3Int[TileHelper.GhostCount];
            int initialCount = options.Count, examined = 0;
            // 고스트 효과가 끝나는 깊이까지 기존 타일을 따라 이동한다.
            void Walk(Vector3Int current, int depth)
            {
                if (options.Count - initialCount >= _search.GhostPathsPerEntrance ||
                    ++examined > _search.GhostStatesPerEntrance || Stopped()) return;
                int first = _random.Next(TileHelper.Directions.Length);
                for (int i = 0; i < TileHelper.Directions.Length; i++)
                {
                    var direction = TileHelper.Directions[(first + i) % TileHelper.Directions.Length];
                    var next = current + direction;
                    if (!next.IsOnSurface(_size)) continue;
                    moves[depth] = direction;
                    if (depth == moves.Length - 1)
                    {
                        if (next != entry && Empty(next, cursor))
                            options.Add(new Placement(MapGimmick.Ghost, entry, input, next, default,
                                ghostInputs: (Vector3Int[])moves.Clone()));
                    }
                    else if (next == entry || !Empty(next, cursor)) Walk(next, depth + 1);
                    if (options.Count - initialCount >= _search.GhostPathsPerEntrance ||
                        examined > _search.GhostStatesPerEntrance) return;
                }
            }
            Walk(entry, 0);
        }

        // 필수 개수와 작동 순서를 모두 만족했는지 검사한다.
        private bool RequirementsMet(int orderProgress)
        {
            foreach (var pair in _minimum) if (_placed[pair.Key] < pair.Value) return false;
            return orderProgress == _plan.RequiredOrder.Count;
        }

        // 기믹 비용에 필요한 일반 연결 길의 비용을 합친다.
        private int PlacementCost(MapGimmick kind) => PlacementCost(_plan.Budget, kind);

        // 발사 대상 칸은 고정 레이저 비용에 포함하고, 발사 후 직접 이동하는 연결 길만 추가한다.
        // 기믹 비용에 필요한 일반 연결 길의 비용을 합친다.
        private static int PlacementCost(GenerationBudgetPolicy budget, MapGimmick kind) => budget.Cost(kind) +
            (kind is MapGimmick.None or MapGimmick.Inverter ? 0 : budget.Cost(MapGimmick.None));

        // 계획 단계에서 필수 사용에 필요한 비용을 추정한다.
        internal static long EstimateReservation(GenerationBudgetPolicy budget, MapGimmick kind, int minimum, int ordered,
            int requiredRotationPlacements, GenerationSamplingOptions sampling)
        {
            long placements = PlacementCount(kind, Math.Max(minimum, ordered), sampling.MaximumConsecutiveTurns);
            if (kind == MapGimmick.Rotation) placements = Math.Max(placements, requiredRotationPlacements);
            return placements * PlacementCost(budget, kind);
        }

        // 필수 순서의 연속 회전을 가능한 묶음으로 나눠 배치 수를 계산한다.
        internal static int RequiredRotationPlacements(IReadOnlyList<MapGimmick> order, int maxTurns)
        {
            int placements = 0, consecutive = 0;
            foreach (var kind in order)
            {
                if (kind != MapGimmick.Rotation) { consecutive = 0; continue; }
                if (consecutive % maxTurns == 0) placements++;
                consecutive++;
            }
            return placements;
        }

        // 실제 사용 횟수를 예산을 부과할 배치 수로 환산한다.
        private long PlacementCount(MapGimmick kind, int amount) => PlacementCount(kind, amount, _sampling.MaximumConsecutiveTurns);

        // 실제 사용 횟수를 예산을 부과할 배치 수로 환산한다.
        private static long PlacementCount(MapGimmick kind, int amount, int maxTurns) => kind == MapGimmick.Rotation
            ? (Math.Max(0L, amount) + maxTurns - 1) / maxTurns : Math.Max(0, amount);

        // 다른 필수 기믹의 예약 비용을 남기면서 후보를 추가할 수 있는지 검사한다.
        private bool Affordable(MapGimmick kind, int credit = 0, int amount = 1)
        {
            long reserved = 0;
            foreach (var pair in _minimum)
                reserved += PlacementCount(pair.Key, pair.Value - _placed[pair.Key] - (kind == pair.Key ? amount : 0)) *
                    PlacementCost(pair.Key);
            return _spent + PlacementCost(kind) - credit + reserved <= _plan.Budget.Maximum;
        }

        // 허용 범위, 정확한 개수, 필수 순서와 예산을 함께 검사한다.
        private bool CanPlace(MapGimmick kind, int orderProgress, int credit = 0, int amount = 1)
        {
            if ((_plan.AllowedGimmicks & kind) == 0 || !Affordable(kind, credit: credit, amount: amount) ||
                _plan.Counts.TryGetValue(kind, out int exact) && exact - _placed[kind] < amount) return false;
            // 뒤에서 필요한 기믹의 개수를 앞 구간에서 소진하지 않도록 순서를 지킨다.
            if (orderProgress < _plan.RequiredOrder.Count && _plan.RequiredOrder[orderProgress] != kind)
                for (int i = orderProgress + 1; i < _plan.RequiredOrder.Count; i++)
                    if (_plan.RequiredOrder[i] == kind) return false;
            return true;
        }

        // 후보에 필요한 초기 타일, 연결, 사용 횟수와 정답 입력을 반영한다.
        private void Apply(Placement action, PuzzleSolver.Simulation cursor)
        {
            if (action.Kind == MapGimmick.Rotation)
            {
                _placed[MapGimmick.Rotation] += action.Turns;
                _rotationTriggers[cursor.Position[_rotationAxis - 1]]++;
                _inputs.AddRange(action.RotationInputs);
                if (!action.RotationOnly)
                {
                    Set(action.Exit, TileType.Road);
                    _inputs.Add(action.Onward);
                }
                return;
            }
            _inputs.Add(action.Input);
            var entry = cursor.InitialCell(action.Entry);
            if (action.Kind == MapGimmick.None) { Set(entry, TileType.Road); return; }
            _placed[action.Kind]++;
            if (action.Kind == MapGimmick.Inverter) { Set(entry, TileType.Inv); return; }
            var exit = cursor.InitialCell(action.Exit);
            if (action.Kind == MapGimmick.Ghost)
            {
                Set(entry, TileType.Ghost);
                Set(exit, TileType.Road);
                _inputs.AddRange(action.GhostInputs);
                return;
            }
            if (action.Kind == MapGimmick.Dash)
            {
                var direction = cursor.InitialDirection(action.Entry, action.Onward);
                Set(entry, (TileType)((char)TileType.DashXp + Array.IndexOf(TileHelper.Directions, direction)));
                Set(exit, TileType.Road);
                // 착지점까지의 강제 이동에는 추가 입력을 넣지 않는다.
                return;
            }
            if (action.Kind == MapGimmick.Laser)
            {
                Set(entry, TileType.Laser);
                SetLaserTargets(action, cursor, active: true);
                Set(exit, TileType.Road);
                // 발사 입력에서는 플레이어가 움직이지 않는다. 다음 입력으로 별도 길에 이동한다.
                _inputs.Add(action.Onward);
                _inputs.Add(action.Exit - action.Entry);
                return;
            }
            Set(entry, TileType.PortalIn);
            Set(exit, TileType.PortalOut);
            Set(cursor.InitialCell(action.Exit + action.Onward), TileType.Road);
            _links.Add(entry, exit);
            _links.Add(exit, entry);
            _inputs.Add(action.Onward);
        }
        // 후보가 바꾼 초기 타일, 연결과 사용 횟수를 복원한다.
        private void Undo(Placement action, PuzzleSolver.Simulation cursor)
        {
            if (action.Kind == MapGimmick.Rotation)
            {
                _placed[MapGimmick.Rotation] -= action.Turns;
                _rotationTriggers[cursor.Position[_rotationAxis - 1]]--;
                if (!action.RotationOnly) Set(action.Exit, TileType.Empty);
                return;
            }
            var entry = cursor.InitialCell(action.Entry);
            Set(entry, action.ReplacesRoad ? TileType.Road : TileType.Empty);
            if (action.Kind == MapGimmick.None) return;
            _placed[action.Kind]--;
            if (action.Kind == MapGimmick.Inverter) return;
            var exit = cursor.InitialCell(action.Exit);
            Set(exit, TileType.Empty);
            if (action.Kind is MapGimmick.Dash or MapGimmick.Ghost) return;
            if (action.Kind == MapGimmick.Laser)
            {
                SetLaserTargets(action, cursor, active: false);
                return;
            }
            Set(cursor.InitialCell(action.Exit + action.Onward), TileType.Empty);
            _links.Remove(entry);
            _links.Remove(exit);
        }

        // 레이저 대상의 초기 타일과 반전 탐색용 인덱스를 함께 배치하거나 복원한다.
        private void SetLaserTargets(Placement action, PuzzleSolver.Simulation cursor, bool active)
        {
            for (int i = 1; i <= action.RayLength; i++)
            {
                var target = cursor.InitialCell(action.Entry + action.Onward * i);
                Set(target, active ? TileType.Road : TileType.Empty);
                if (active) _laserTargets.Add(target);
                else _laserTargets.Remove(target);
            }
        }
        // 초기 보드에 포함된 모든 방향의 대시 타일을 센다.
        private static int CountDash(string tiles)
        {
            int count = 0;
            foreach (char tile in tiles) if (TileHelper.IsDash(tile)) count++;
            return count;
        }
        // 초기 보드에서 지정한 타일 종류의 수를 센다.
        private static int CountTile(string tiles, TileType kind)
        {
            int count = 0;
            foreach (char tile in tiles) if (tile == (char)kind) count++;
            return count;
        }
        // 취소 요청을 전파하거나 시간이 다 됐으면 탐색 중단을 기록한다.
        private bool Stopped()
        {
            _token.ThrowIfCancellationRequested();
            if (_clock.ElapsedMilliseconds >= _milliseconds) _limited = true;
            return _limited;
        }
        // 회전 전 초기 보드의 타일을 읽는다.
        private char InitialTile(Vector3Int p) => _board[p.x, p.y, p.z];
        // 현재 시뮬레이션에서 표면의 빈 칸인지 검사한다.
        private bool Empty(Vector3Int p, PuzzleSolver.Simulation cursor) =>
            p.IsOnSurface(_size) && cursor.TileAt(p) == (char)TileType.Empty;
        // 초기 타일을 수정하고 구역별 점유 수를 함께 갱신한다.
        private void Set(Vector3Int p, TileType tile)
        {
            bool empty = tile == TileType.Empty;
            bool wasEmpty = InitialTile(p) == (char)TileType.Empty;
            if (empty != wasEmpty) _regionOccupancy[Region(p)] += empty ? -1 : 1;
            _board[p.x, p.y, p.z] = (char)tile;
        }
        // 두 칸 사이의 맨해튼 거리를 구한다.
        private static int Distance(Vector3Int a, Vector3Int b) => Math.Abs(a.x - b.x) + Math.Abs(a.y - b.y) + Math.Abs(a.z - b.z);
        // 생성 계획을 포함한 실패 결과를 만들고 후보는 비워 둔다.
        private MapGenerationResult Failure(MapGenerationStatus status, string message) => new(status, null, message, _plan);

        private sealed class SearchFrame
        {
            internal readonly PuzzleSolver.Simulation Cursor;
            internal readonly IEnumerator<SearchChoice> Choices;
            internal readonly Placement? Placement;
            internal readonly int PreviousInputCount, Cost;

            // 한 탐색 깊이에서 남은 후보와 되돌림에 필요한 이전 값을 보관한다.
            internal SearchFrame(PuzzleSolver.Simulation cursor, IEnumerator<SearchChoice> choices,
                Placement? placement = null, int previousInputCount = 0, int cost = 0) =>
                (Cursor, Choices, Placement, PreviousInputCount, Cost) =
                (cursor, choices, placement, previousInputCount, cost);
        }

        private readonly struct SearchChoice
        {
            internal readonly Placement? Placement;
            internal readonly Vector3Int Input;

            // 새 배치 또는 기존 타일로 이동하는 입력 하나를 표현한다.
            internal SearchChoice(Placement placement) => (Placement, Input) = (placement, default);
            // 새 배치 또는 기존 타일로 이동하는 입력 하나를 표현한다.
            internal SearchChoice(Vector3Int input) => (Placement, Input) = (null, input);
        }

        private readonly struct Placement
        {
            internal readonly Vector3Int Entry, Input, Exit, Onward;
            internal readonly MapGimmick Kind;
            internal readonly int RayLength;
            internal readonly Vector3Int[] GhostInputs, RotationInputs;
            internal int Turns => RotationInputs?.Length ?? 0;
            internal readonly bool ReplacesRoad, RotationOnly;
            // 반영할 타일과 입력을 하나의 배치 후보로 보관한다.
            internal Placement(Vector3Int entry, Vector3Int input) =>
                this = new Placement(MapGimmick.None, entry, input, default, default);
            // 반영할 타일과 입력을 하나의 배치 후보로 보관한다.
            internal Placement(MapGimmick kind, Vector3Int entry, Vector3Int input, Vector3Int exit, Vector3Int onward,
                int rayLength = 0, Vector3Int[] ghostInputs = null, bool replacesRoad = false, bool rotationOnly = false,
                Vector3Int[] rotationInputs = null) =>
                (Entry, Input, Exit, Onward, Kind, RayLength, GhostInputs, ReplacesRoad, RotationOnly, RotationInputs) =
                (entry, input, exit, onward, kind, rayLength, ghostInputs, replacesRoad, rotationOnly, rotationInputs);
        }
    }
}
