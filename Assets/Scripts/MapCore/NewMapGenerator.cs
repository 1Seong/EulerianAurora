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
        private readonly CancellationToken _token;
        private readonly int _attemptLimit, _restartLimit, _milliseconds;
        private readonly GenerationDifficultyOptions _profile;
        private readonly GenerationSearchOptions _search;
        private readonly GenerationSamplingOptions _sampling;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<Vector3Int> _surface = new();
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
        private bool[,,] _reachableRoads;
        private Vector3Int[] _roadQueue;
        private MapCandidate _candidate;

        internal NewMapGenerator(int size, GenerationPlan plan, int seed, MapGenerationLimits limits,
            MapGenerationOptions options, CancellationToken token)
        {
            _size = size;
            _plan = plan;
            _random = new Random(seed);
            _token = token;
            _attemptLimit = limits.PlacementAttempts;
            _restartLimit = limits.Restarts;
            _milliseconds = limits.Milliseconds;
            _profile = options.For(plan.Difficulty);
            _search = options.Search;
            _sampling = options.Sampling;
            _board = new char[size, size, size];
            _rotationTriggers = new int[size];
            for (int x = 0; x < size; x++)
            for (int y = 0; y < size; y++)
            for (int z = 0; z < size; z++)
            {
                var cell = new Vector3Int(x, y, z);
                if (cell.IsOnSurface(size)) _surface.Add(cell);
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
                if (_plan.AllowedGimmicks == MapGimmick.None)
                {
                    int pathLength = RemainingRoadCount();
                    // 각 배치가 한 칸씩만 늘리는 경우, 목표 길이와 되돌림에 필요한 시도를 한 시작점에 배정한다.
                    if (pathLength > share)
                    {
                        share = pathLength + share / 2;
                        _checkRoadCapacity = true;
                    }
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
            int length = _random.Next(Math.Min(_profile.RotationGroupMinLength, _size - 2),
                Math.Min(_profile.RotationGroupMaxLength, _size - 2) + 1);
            // 어려움에서는 묶음 안에서 출발해 여러 레이어를 지나 연결점에 도달할 기회를 늘린다.
            int offset = _profile.StartInsideRotationGroup && length > 2
                ? _random.Next(1, length - 1) : _random.Next(length);
            int first = Math.Clamp(start[axis] - offset, 1, _size - 1 - length);
            start[axis] = first + offset;
            _rotationAxis = axis + 1;
            _rotatingLayers = new bool[_size];
            for (int i = 0; i < length; i++) _rotatingLayers[first + i] = true;

            // 비회전 레이어를 사이에 둔 별도 묶음도 시도한다. 각 묶음에 필수 사용량은 두지 않는다.
            if (_profile.SecondRotationGroupPercent > 0 && _random.Next(100) < _profile.SecondRotationGroupPercent)
            {
                int second = first + length + _sampling.RotationGroupGap;
                if (second < _size - 1)
                    for (int i = second; i < Math.Min(second + _sampling.SecondRotationGroupLength, _size - 1); i++)
                        _rotatingLayers[i] = true;
                else
                {
                    int last = first - 1 - _sampling.RotationGroupGap;
                    for (int i = last; i >= Math.Max(last - _sampling.SecondRotationGroupLength + 1, 1); i--)
                        _rotatingLayers[i] = true;
                }
            }
            _simulation = new PuzzleSolver.Simulation(_size, _rotationAxis, _rotatingLayers);
            return start;
        }

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

        private void PopFrame(Stack<SearchFrame> frames)
        {
            var frame = frames.Pop();
            frame.Choices.Dispose();
            if (frames.Count > 0)
                UndoSearchChoice(frame.Placement, frames.Peek().Cursor, frame.PreviousInputCount, frame.Cost);
        }

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
                bool canReplaceRoad = _laserTargets.Count > 0 &&
                    CanPlace(MapGimmick.Inverter, orderProgress, _plan.Budget.Cost(MapGimmick.None));
                if (odd && !canReplaceRoad && cursor.CannotFinish()) yield break;
                if (odd && _placed[MapGimmick.Rotation] < _minimum[MapGimmick.Rotation])
                {
                    foreach (var choice in RotationChoices(rotations)) yield return choice;
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
                foreach (var choice in RotationChoices(rotations)) yield return choice;
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
            foreach (var direction in TileHelper.Directions)
            {
                var entry = position + direction;
                if (!Empty(entry, cursor)) continue;
                if (Affordable(MapGimmick.None)) roads.Add(new Placement(entry, direction));
                if (addInverter) inverters.Add(new Placement(MapGimmick.Inverter, entry, direction, default, default));
                if (cursor.Unpainted == 0 && CanPlace(MapGimmick.Ghost, orderProgress)) AddGhostOptions(entry, direction, ghosts, cursor);
                if (CanPlace(MapGimmick.Dash, orderProgress))
                    foreach (var onward in TileHelper.Directions)
                    {
                        var landing = entry + onward;
                        if (Empty(landing, cursor)) dashes.Add(new Placement(MapGimmick.Dash, entry, direction, landing, onward));
                    }
                if (CanPlace(MapGimmick.Laser, orderProgress))
                    foreach (var shot in TileHelper.Directions)
                    {
                        // 발사 방향의 빈 직선 길과 발사 후 이동할 별도 길을 확보한다.
                        for (int length = 1; length <= _sampling.MaximumLaserRayLength; length++)
                        {
                            if (!Empty(entry + shot * length, cursor)) break;
                            if (length < _sampling.MinimumLaserRayLength) continue;
                            if (!Affordable(MapGimmick.Laser, length)) break;
                            foreach (var escape in TileHelper.Directions)
                            {
                                if (escape == shot || !Empty(entry + escape, cursor)) continue;
                                lasers.Add(new Placement(MapGimmick.Laser, entry, direction, entry + escape, shot, length));
                            }
                        }
                    }
                if (!CanPlace(MapGimmick.Portal, orderProgress)) continue;
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
            while (!Stopped())
            {
                if (_attempts >= _restartAttemptCeiling) break;
                var feasible = new List<MapGimmick>(7);
                if (roads.Count > 0) feasible.Add(MapGimmick.None);
                if (portals.Count > 0) feasible.Add(MapGimmick.Portal);
                if (dashes.Count > 0) feasible.Add(MapGimmick.Dash);
                if (lasers.Count > 0) feasible.Add(MapGimmick.Laser);
                if (ghosts.Count > 0) feasible.Add(MapGimmick.Ghost);
                if (inverters.Count > 0) feasible.Add(MapGimmick.Inverter);
                if (rotations.Count > 0) feasible.Add(MapGimmick.Rotation);
                MapGimmick kind;
                if (rotations.Count > 0 && _placed[MapGimmick.Rotation] < _minimum[MapGimmick.Rotation])
                    kind = MapGimmick.Rotation;
                else if (inverters.Count > 0 && inverted < _minimum[MapGimmick.Inverter])
                    kind = MapGimmick.Inverter;
                else if (!_plan.Budget.TryPick(_random, Supported, _plan.Budget.Maximum - _spent, feasible, out kind))
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

        private int PickPlacement(List<Placement> options, MapGimmick kind, int inverted, PuzzleSolver.Simulation cursor)
        {
            if (kind == MapGimmick.Rotation) return PickRotation(options);
            int pick = _random.Next(options.Count);
            if (_minimum[MapGimmick.Inverter] - inverted != 1 ||
                kind is not (MapGimmick.None or MapGimmick.Laser or MapGimmick.Portal)) return pick;

            // 발사 구간 옆으로 이어지는 길을 우선 시도하면 반전 뒤 교차 경로를 만들기 쉽다.
            // 점수가 낮은 배치도 남겨 두고, 실제 연결 여부는 재생으로 확인한다.
            for (int i = 0; i < options.Count; i++)
            {
                int index = (pick + i) % options.Count;
                var option = options[index];
                if (kind == MapGimmick.Portal && Distance(cursor.CurrentCell(_simulation.Position), option.Exit) == 1)
                    return index;
                if (kind == MapGimmick.Laser && option.RayLength == _sampling.PreferredInverterRayLength)
                {
                    var parallel = option.Exit + option.Onward;
                    if (parallel == option.Entry) continue;
                    int length = 0;
                    for (; length < option.RayLength && Empty(parallel + option.Onward * length, cursor); length++) { }
                    if (length == option.RayLength) return index;
                }
                else if (kind == MapGimmick.None)
                    foreach (var direction in TileHelper.Directions)
                        if ((option.Entry + direction).IsOnSurface(_size) &&
                            _laserTargets.Contains(cursor.InitialCell(option.Entry + direction))) return index;
            }
            return pick;
        }

        private int PickRotation(List<Placement> options)
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
                if (options[i].Turns == desired && pick-- == 0) return i;
            throw new InvalidOperationException("선택한 회전 횟수의 후보가 없습니다.");
        }

        private int PickWeightedIndex(int first, int second, int third)
        {
            int draw = _random.Next(first + second + third);
            return draw < first ? 0 : draw < first + second ? 1 : 2;
        }

        private IEnumerable<SearchChoice> RotationChoices(List<Placement> options)
        {
            while (options.Count > 0 && !Stopped() && _attempts < _restartAttemptCeiling)
            {
                int pick = PickRotation(options);
                var action = options[pick];
                options.RemoveAt(pick);
                yield return new SearchChoice(action);
            }
        }

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

        private bool IsOpen(PuzzleSolver.Simulation cursor, Vector3Int cell)
        {
            if (!cell.IsOnSurface(_size)) return false;
            char tile = cursor.TileAt(cell);
            return tile != (char)TileType.Empty && !TileHelper.IsPainted(tile);
        }

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

        private bool TakeAttempt()
        {
            if (Stopped() || _attempts >= _restartAttemptCeiling) return false;
            _attempts++;
            return true;
        }

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
            int cost = PlacementCost(action.Kind, action.RayLength) -
                (action.ReplacesRoad || action.RotationOnly ? _plan.Budget.Cost(MapGimmick.None) : 0);
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

        private void UndoSearchChoice(Placement? action, PuzzleSolver.Simulation cursor, int previousInputCount, int cost)
        {
            if (action.HasValue) Undo(action.Value, cursor);
            _inputs.RemoveRange(previousInputCount, _inputs.Count - previousInputCount);
            _spent -= cost;
        }

        private int RemainingRoadCount()
        {
            int cost = _plan.Budget.Cost(MapGimmick.None);
            return (Math.Max(0, _target - _spent) + cost - 1) / cost;
        }

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

        private bool ReplayPrefix(PuzzleSolver.Simulation replay, PuzzleSolver.Simulation cursor,
            int previousInputCount, Placement action)
        {
            if (!replay.Reset(_board, _links)) return false;
            for (int i = 0; i < _inputs.Count; i++)
            {
                if (i == previousInputCount && (replay.Position != cursor.Position || !replay.SameLayerTurns(cursor) ||
                    !SameEvents(replay.Events, cursor.Events))) return false;
                int eventCount = replay.Events.Count;
                if (Stopped() || !replay.TryStep(_inputs[i])) return false;
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

        private static bool MatchesRotationCount(PuzzleSolver.Simulation cursor, int firstEvent, int expected)
        {
            int count = 0;
            for (int i = firstEvent; i < cursor.Events.Count; i++)
                if (cursor.Events[i] == MapGimmick.Rotation) count++;
            return count == expected;
        }

        private static bool SameEvents(IReadOnlyList<MapGimmick> a, IReadOnlyList<MapGimmick> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private int OrderProgress(PuzzleSolver.Simulation cursor)
        {
            int progress = 0;
            foreach (var used in cursor.Events)
                if (progress < _plan.RequiredOrder.Count && used == _plan.RequiredOrder[progress]) progress++;
            return progress;
        }

        private void AddGhostOptions(Vector3Int entry, Vector3Int input, List<Placement> options, PuzzleSolver.Simulation cursor)
        {
            // 현재 접두 경로는 모두 칠해져 있다. 효과가 지속되는 동안 기존 칸을 되짚고,
            // 마지막 이동에서만 새 길을 칠한다. 같은 칸을 다시 방문하는 이동도 허용한다.
            var moves = new Vector3Int[TileHelper.GhostCount];
            int initialCount = options.Count, examined = 0;
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

        private bool RequirementsMet(int orderProgress)
        {
            foreach (var pair in _minimum) if (_placed[pair.Key] < pair.Value) return false;
            return orderProgress == _plan.RequiredOrder.Count;
        }

        private int PlacementCost(MapGimmick kind, int rayLength = 0) => _plan.Budget.Cost(kind) +
            (kind is MapGimmick.None or MapGimmick.Inverter ? 0 :
                _plan.Budget.Cost(MapGimmick.None) * (kind == MapGimmick.Laser ?
                    Math.Max(rayLength, _sampling.MinimumLaserRayLength) + 1 : 1));

        internal static long EstimateReservation(GenerationBudgetPolicy budget, MapGimmick kind, int minimum, int ordered,
            int requiredRotationPlacements, GenerationSamplingOptions sampling)
        {
            int roads = kind == MapGimmick.Laser ? sampling.MinimumLaserRayLength + 1 :
                kind is MapGimmick.Portal or MapGimmick.Dash or MapGimmick.Ghost or MapGimmick.Rotation ? 1 : 0;
            long placements = PlacementCount(kind, Math.Max(minimum, ordered), sampling.MaximumConsecutiveTurns);
            if (kind == MapGimmick.Rotation) placements = Math.Max(placements, requiredRotationPlacements);
            return placements * (budget.Cost(kind) + roads * budget.Cost(MapGimmick.None));
        }

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

        private long PlacementCount(MapGimmick kind, int amount) => PlacementCount(kind, amount, _sampling.MaximumConsecutiveTurns);

        private static long PlacementCount(MapGimmick kind, int amount, int maxTurns) => kind == MapGimmick.Rotation
            ? (Math.Max(0L, amount) + maxTurns - 1) / maxTurns : Math.Max(0, amount);

        private bool Affordable(MapGimmick kind, int rayLength = 1, int credit = 0, int amount = 1)
        {
            long reserved = 0;
            foreach (var pair in _minimum)
                reserved += PlacementCount(pair.Key, pair.Value - _placed[pair.Key] - (kind == pair.Key ? amount : 0)) *
                    PlacementCost(pair.Key);
            return _spent + PlacementCost(kind, rayLength) - credit + reserved <= _plan.Budget.Maximum;
        }

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
                for (int i = 1; i <= action.RayLength; i++)
                {
                    var target = cursor.InitialCell(action.Entry + action.Onward * i);
                    Set(target, TileType.Road);
                    _laserTargets.Add(target);
                }
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
                for (int i = 1; i <= action.RayLength; i++)
                {
                    var target = cursor.InitialCell(action.Entry + action.Onward * i);
                    Set(target, TileType.Empty);
                    _laserTargets.Remove(target);
                }
                return;
            }
            Set(cursor.InitialCell(action.Exit + action.Onward), TileType.Empty);
            _links.Remove(entry);
            _links.Remove(exit);
        }
        private static int CountDash(string tiles)
        {
            int count = 0;
            foreach (char tile in tiles) if (TileHelper.IsDash(tile)) count++;
            return count;
        }
        private static int CountTile(string tiles, TileType kind)
        {
            int count = 0;
            foreach (char tile in tiles) if (tile == (char)kind) count++;
            return count;
        }
        private bool Stopped()
        {
            _token.ThrowIfCancellationRequested();
            if (_clock.ElapsedMilliseconds >= _milliseconds) _limited = true;
            return _limited;
        }
        private char InitialTile(Vector3Int p) => _board[p.x, p.y, p.z];
        private bool Empty(Vector3Int p, PuzzleSolver.Simulation cursor) =>
            p.IsOnSurface(_size) && cursor.TileAt(p) == (char)TileType.Empty;
        private void Set(Vector3Int p, TileType tile) => _board[p.x, p.y, p.z] = (char)tile;
        private static int Distance(Vector3Int a, Vector3Int b) => Math.Abs(a.x - b.x) + Math.Abs(a.y - b.y) + Math.Abs(a.z - b.z);
        private MapGenerationResult Failure(MapGenerationStatus status, string message) => new(status, null, message, _plan);

        private sealed class SearchFrame
        {
            internal readonly PuzzleSolver.Simulation Cursor;
            internal readonly IEnumerator<SearchChoice> Choices;
            internal readonly Placement? Placement;
            internal readonly int PreviousInputCount, Cost;

            internal SearchFrame(PuzzleSolver.Simulation cursor, IEnumerator<SearchChoice> choices,
                Placement? placement = null, int previousInputCount = 0, int cost = 0) =>
                (Cursor, Choices, Placement, PreviousInputCount, Cost) =
                (cursor, choices, placement, previousInputCount, cost);
        }

        private readonly struct SearchChoice
        {
            internal readonly Placement? Placement;
            internal readonly Vector3Int Input;

            internal SearchChoice(Placement placement) => (Placement, Input) = (placement, default);
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
            internal Placement(Vector3Int entry, Vector3Int input) =>
                this = new Placement(MapGimmick.None, entry, input, default, default);
            internal Placement(MapGimmick kind, Vector3Int entry, Vector3Int input, Vector3Int exit, Vector3Int onward,
                int rayLength = 0, Vector3Int[] ghostInputs = null, bool replacesRoad = false, bool rotationOnly = false,
                Vector3Int[] rotationInputs = null) =>
                (Entry, Input, Exit, Onward, Kind, RayLength, GhostInputs, ReplacesRoad, RotationOnly, RotationInputs) =
                (entry, input, exit, onward, kind, rayLength, ghostInputs, replacesRoad, rotationOnly, rotationInputs);
        }
    }
}
