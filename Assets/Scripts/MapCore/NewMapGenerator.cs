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
        public int PlacementAttempts { get; set; } = 1500;
        public int Restarts { get; set; } = 8;
        public int Milliseconds { get; set; } = 2500;
    }

    // 초기 배치와 입력 목록은 따로 관리한다. 매 배치 후보를 실제 전이 규칙으로 재생한다.
    internal sealed class NewMapGenerator
    {
        internal const MapGimmick Supported = MapGimmick.Portal | MapGimmick.Dash | MapGimmick.Laser | MapGimmick.Ghost | MapGimmick.Inverter;
        private static readonly MapGimmick[] Kinds =
            { MapGimmick.Portal, MapGimmick.Dash, MapGimmick.Laser, MapGimmick.Ghost, MapGimmick.Inverter };
        private readonly int _size;
        private readonly GenerationPlan _plan;
        private readonly Random _random;
        private readonly CancellationToken _token;
        private readonly int _attemptLimit, _restartLimit, _milliseconds;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<Vector3Int> _surface = new();
        private readonly List<Vector3Int> _inputs = new();
        private readonly Dictionary<Vector3Int, Vector3Int> _links = new();
        private readonly HashSet<Vector3Int> _laserTargets = new();
        private readonly PuzzleSolver.Simulation _simulation;
        private readonly char[,,] _board;
        private readonly Dictionary<MapGimmick, int> _minimum = new();
        private readonly Dictionary<MapGimmick, int> _placed = new();
        private int _spent, _target, _attempts, _backtracks, _restartAttemptCeiling;
        private bool _limited;
        private MapCandidate _candidate;

        internal NewMapGenerator(int size, GenerationPlan plan, int seed, MapGenerationLimits limits, CancellationToken token)
        {
            _size = size;
            _plan = plan;
            _random = new Random(seed);
            _token = token;
            _attemptLimit = limits.PlacementAttempts;
            _restartLimit = limits.Restarts;
            _milliseconds = limits.Milliseconds;
            _board = new char[size, size, size];
            _simulation = new PuzzleSolver.Simulation(size);
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
            long minimumCost = 0, minimumCells = 1;
            foreach (var pair in _minimum)
            {
                if (_plan.Counts.TryGetValue(pair.Key, out int exact) && exact < pair.Value)
                    return Failure(MapGenerationStatus.UnsupportedConditions, "명시한 개수보다 많은 반복 사용은 아직 지원하지 않습니다.");
                minimumCost += (long)pair.Value * PlacementCost(pair.Key);
                // 후속 인버터는 기존 레이저 대상 칸을 대신 사용할 수 있다.
                if (pair.Key != MapGimmick.Inverter)
                    minimumCells += (long)pair.Value * (pair.Key is MapGimmick.Dash or MapGimmick.Ghost ? 2 : 3);
            }
            if (minimumCost > _plan.Budget.Maximum || minimumCells > _surface.Count)
                return Failure(MapGenerationStatus.NoCandidateFound, "기믹 이후 연결 길까지 배치하기에는 생성 예산 또는 공간이 부족합니다.");
            for (int restart = 0; restart < _restartLimit && !Stopped(); restart++)
            {
                for (int x = 0; x < _size; x++)
                for (int y = 0; y < _size; y++)
                for (int z = 0; z < _size; z++) _board[x, y, z] = (char)TileType.Empty;
                _links.Clear();
                _laserTargets.Clear();
                _inputs.Clear();
                _spent = 0;
                foreach (var kind in Kinds) _placed[kind] = 0;
                _restartAttemptCeiling = Math.Min(_attemptLimit,
                    _attempts + Math.Max(1, (_attemptLimit - _attempts) / (_restartLimit - restart)));
                var start = _surface[_random.Next(_surface.Count)];
                Set(start, TileType.Player);
                int desired = _random.Next((_plan.Budget.Maximum + 1) / 2, _plan.Budget.Maximum + 1);
                // 작은 보드에서 모든 표면을 채우는 목표는 불가능한 전면 방문을 강제할 수 있다.
                // 예산은 상한이며 필수 배치 외의 공간에는 여유를 남긴다.
                int ordinaryCapacity = Math.Max(2, (_surface.Count - 1) * 3 / 4);
                _target = Math.Max((int)minimumCost, Math.Min(desired, ordinaryCapacity));
                if (_simulation.Reset(_board, _links) && Explore(_simulation))
                {
                    string level = _plan.Difficulty switch { MapDifficulty.Easy => "쉬움", MapDifficulty.Medium => "보통", _ => "어려움" };
                    return new MapGenerationResult(MapGenerationStatus.CandidateFound, _candidate,
                        $"신규 맵 · {level} · 비용 {_candidate.GenerationCost}/{_plan.Budget.Maximum}\n" +
                        $"포탈 {_candidate.Map.PortalPairs.Length}쌍 · 대시 {CountDash(_candidate.Map.Tiles)}개 · 레이저 {CountTile(_candidate.Map.Tiles, TileType.Laser)}개 · 고스트 {CountTile(_candidate.Map.Tiles, TileType.Ghost)}개 · 인버터 {CountTile(_candidate.Map.Tiles, TileType.Inv)}개\n" +
                        $"입력 {_candidate.Witness.SolutionInputs.Count}회 · 클리어 재생 확인", _plan);
                }
            }
            _limited |= _attempts >= _attemptLimit;
            return Failure(_limited ? MapGenerationStatus.SearchLimitReached : MapGenerationStatus.NoCandidateFound,
                _limited ? "생성 탐색 한도에 도달했습니다. 후보를 찾지 못했으며 불가능 판정은 아닙니다. 다시 시도해 주세요."
                    : "이번 생성 시도에서 조건을 만족하는 후보를 찾지 못했습니다. 다른 시드로 다시 시도할 수 있습니다.");
        }

        private bool Explore(PuzzleSolver.Simulation cursor)
        {
            if (Stopped()) return false;
            int orderProgress = OrderProgress(cursor);
            if (_spent >= _target && cursor.Unpainted == 0 && cursor.GhostRemaining == 0 &&
                RequirementsMet(orderProgress) && _inputs.Count > 0)
            {
                // 마지막 입력 이후 실제로 모든 칸이 칠해지는지 기존 솔버의 완전 재생으로 확인한다.
                var witness = PuzzleSolver.VerifySequence(_board, _links, _inputs);
                if (Stopped()) return false;
                if (witness.IsSolvable && (_plan.RequiredGimmicks & witness.UsedGimmicks) == _plan.RequiredGimmicks)
                {
                    _candidate = new MapCandidate(MapDocument.FromBoard(_board, _links, 0, null), witness,
                        _spent, _attempts, _backtracks);
                    return true;
                }
                return false;
            }

            var position = cursor.Position;
            int inverted = _placed[MapGimmick.Inverter];
            bool odd = (inverted & 1) != 0;
            bool canInvert = CanPlace(MapGimmick.Inverter, orderProgress);
            if (inverted > 0 && cursor.Unpainted > 0)
            {
                bool canReplaceRoad = _laserTargets.Count > 0 &&
                    CanPlace(MapGimmick.Inverter, orderProgress, _plan.Budget.Cost(MapGimmick.None));
                if (odd && !canReplaceRoad && cursor.CannotFinish()) return false;
                // 반전으로 열린 칸에는 방문한 길과 레이저로만 칠한 길이 모두 포함된다.
                int first = _random.Next(TileHelper.Directions.Length);
                for (int i = 0; i < TileHelper.Directions.Length; i++)
                {
                    var direction = TileHelper.Directions[(first + i) % TileHelper.Directions.Length];
                    var entry = position + direction;
                    if (canReplaceRoad && _laserTargets.Contains(entry) &&
                        InitialTile(entry) == (char)TileType.Road && cursor.TileAt(entry) == (char)TileType.Road &&
                        TryPlacement(new Placement(MapGimmick.Inverter, entry, direction, default, default, replacesRoad: true), cursor))
                        return true;
                }
                for (int i = 0; i < TileHelper.Directions.Length; i++)
                {
                    var direction = TileHelper.Directions[(first + i) % TileHelper.Directions.Length];
                    var entry = position + direction;
                    if (!entry.IsOnSurface(_size) || Empty(entry) || TileHelper.IsPainted(cursor.TileAt(entry)) ||
                        !TakeAttempt()) continue;
                    var next = cursor.Fork();
                    if (!next.TryStep(direction)) continue;
                    _inputs.Add(direction);
                    try
                    {
                        if (Explore(next)) return true;
                        _backtracks++;
                    }
                    finally { _inputs.RemoveAt(_inputs.Count - 1); }
                }
            }
            // 홀수 번째 반전 뒤에는 뒤늦게 추가한 미색칠 칸도 닫혀 있다.
            if (odd) return false;

            var roads = new List<Placement>();
            var portals = new List<Placement>();
            var dashes = new List<Placement>();
            var lasers = new List<Placement>();
            var ghosts = new List<Placement>();
            var inverters = new List<Placement>();
            bool wantsAnotherInverter = inverted + 1 < _minimum[MapGimmick.Inverter];
            // 마지막 인버터 뒤에는 새 칸을 이어 붙이기 어려우므로 먼저 목표 비용에 접근한다.
            bool addInverter = canInvert && (wantsAnotherInverter ? _laserTargets.Count > 0 :
                _spent + PlacementCost(MapGimmick.Inverter) >= _target);
            foreach (var direction in TileHelper.Directions)
            {
                var entry = position + direction;
                if (!Empty(entry)) continue;
                if (Affordable(MapGimmick.None)) roads.Add(new Placement(entry, direction));
                if (addInverter) inverters.Add(new Placement(MapGimmick.Inverter, entry, direction, default, default));
                if (cursor.Unpainted == 0 && CanPlace(MapGimmick.Ghost, orderProgress)) AddGhostOptions(entry, direction, ghosts);
                if (CanPlace(MapGimmick.Dash, orderProgress))
                    foreach (var onward in TileHelper.Directions)
                    {
                        var landing = entry + onward;
                        if (Empty(landing)) dashes.Add(new Placement(MapGimmick.Dash, entry, direction, landing, onward));
                    }
                if (CanPlace(MapGimmick.Laser, orderProgress))
                    foreach (var shot in TileHelper.Directions)
                    {
                        // 발사 대상은 1~3칸의 빈 직선 구간이다. 발사 후 이동할 길도 별도로 확보한다.
                        for (int length = 1; length <= 3; length++)
                        {
                            if (!Empty(entry + shot * length) || !Affordable(MapGimmick.Laser, length)) break;
                            foreach (var escape in TileHelper.Directions)
                            {
                                if (escape == shot || !Empty(entry + escape)) continue;
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
                        var exit = _simulation.Position + exitDirection;
                        if (!Empty(exit) || Distance(entry, exit) <= 1) continue;
                        foreach (var onward in TileHelper.Directions)
                            if (Empty(exit + onward))
                                portals.Add(new Placement(MapGimmick.Portal, entry, direction, exit, onward));
                    }
                }
                // 출구는 입구와 인접하지 않은 표면에서 고른다. 작은 고정 개수만 샘플링한다.
                int first = _random.Next(_surface.Count);
                int found = 0;
                for (int offset = 0; offset < _surface.Count && found < 8; offset++)
                {
                    var exit = _surface[(first + offset) % _surface.Count];
                    if (!Empty(exit) || Distance(entry, exit) <= 1) continue;
                    int directionStart = _random.Next(TileHelper.Directions.Length);
                    for (int d = 0; d < TileHelper.Directions.Length; d++)
                    {
                        var onward = TileHelper.Directions[(directionStart + d) % TileHelper.Directions.Length];
                        var tail = exit + onward;
                        if (!Empty(tail) || tail == entry) continue;
                        portals.Add(new Placement(MapGimmick.Portal, entry, direction, exit, onward));
                        found++;
                        break;
                    }
                }
            }
            while (!Stopped())
            {
                if (_attempts >= _restartAttemptCeiling) break;
                var feasible = new List<MapGimmick>(6);
                if (roads.Count > 0) feasible.Add(MapGimmick.None);
                if (portals.Count > 0) feasible.Add(MapGimmick.Portal);
                if (dashes.Count > 0) feasible.Add(MapGimmick.Dash);
                if (lasers.Count > 0) feasible.Add(MapGimmick.Laser);
                if (ghosts.Count > 0) feasible.Add(MapGimmick.Ghost);
                if (inverters.Count > 0) feasible.Add(MapGimmick.Inverter);
                MapGimmick kind;
                if (inverters.Count > 0 && inverted < _minimum[MapGimmick.Inverter])
                    kind = MapGimmick.Inverter;
                else if (!_plan.Budget.TryPick(_random, Supported, _plan.Budget.Maximum - _spent, feasible, out kind))
                    break;
                var options = kind switch { MapGimmick.Portal => portals, MapGimmick.Dash => dashes,
                    MapGimmick.Laser => lasers, MapGimmick.Ghost => ghosts, MapGimmick.Inverter => inverters, _ => roads };
                int pick = PickPlacement(options, kind, inverted);
                var action = options[pick];
                options.RemoveAt(pick);
                if (TryPlacement(action, cursor)) return true;
            }
            return false;
        }

        private int PickPlacement(List<Placement> options, MapGimmick kind, int inverted)
        {
            int pick = _random.Next(options.Count);
            if (_minimum[MapGimmick.Inverter] - inverted != 1 ||
                kind is not (MapGimmick.None or MapGimmick.Laser or MapGimmick.Portal)) return pick;

            // 발사 구간 옆으로 이어지는 길을 우선 시도하면 반전 뒤 교차 경로를 만들기 쉽다.
            // 점수가 낮은 배치도 남겨 두고, 실제 연결 여부는 재생으로 확인한다.
            for (int i = 0; i < options.Count; i++)
            {
                int index = (pick + i) % options.Count;
                var option = options[index];
                if (kind == MapGimmick.Portal && Distance(_simulation.Position, option.Exit) == 1)
                    return index;
                if (kind == MapGimmick.Laser && option.RayLength == 2)
                {
                    var parallel = option.Exit + option.Onward;
                    if (parallel != option.Entry && Empty(parallel) && Empty(parallel + option.Onward))
                        return index;
                }
                else if (kind == MapGimmick.None)
                    foreach (var direction in TileHelper.Directions)
                        if (_laserTargets.Contains(option.Entry + direction)) return index;
            }
            return pick;
        }

        private bool TakeAttempt()
        {
            if (Stopped() || _attempts >= _restartAttemptCeiling) return false;
            _attempts++;
            return true;
        }

        private bool TryPlacement(Placement action, PuzzleSolver.Simulation cursor)
        {
            if (!TakeAttempt()) return false;
            int previousInputCount = _inputs.Count;
            int cost = PlacementCost(action.Kind, action.RayLength) -
                (action.ReplacesRoad ? _plan.Budget.Cost(MapGimmick.None) : 0);
            Apply(action);
            _spent += cost;
            try
            {
                var replay = cursor.Fork(copyState: false);
                if (ReplayPrefix(replay, cursor, previousInputCount, action.Kind) && Explore(replay)) return true;
                _backtracks++;
                return false;
            }
            finally
            {
                Undo(action);
                _inputs.RemoveRange(previousInputCount, _inputs.Count - previousInputCount);
                _spent -= cost;
            }
        }

        private bool ReplayPrefix(PuzzleSolver.Simulation replay, PuzzleSolver.Simulation cursor,
            int previousInputCount, MapGimmick kind)
        {
            if (!replay.Reset(_board, _links)) return false;
            for (int i = 0; i < _inputs.Count; i++)
            {
                if (i == previousInputCount && (replay.Position != cursor.Position ||
                    !SameEvents(replay.Events, cursor.Events))) return false;
                if (Stopped() || !replay.TryStep(_inputs[i])) return false;
            }
            if (kind != MapGimmick.None)
            {
                bool used = false;
                for (int i = cursor.Events.Count; i < replay.Events.Count; i++)
                    if (replay.Events[i] == kind) used = true;
                if (!used) return false;
            }
            return replay.GhostRemaining == 0 && (_placed[MapGimmick.Inverter] > 0 || replay.Unpainted == 0);
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

        private void AddGhostOptions(Vector3Int entry, Vector3Int input, List<Placement> options)
        {
            // 현재 접두 경로는 모두 칠해져 있다. 효과가 지속되는 동안 기존 칸을 되짚고,
            // 마지막 이동에서만 새 길을 칠한다. 같은 칸을 다시 방문하는 이동도 허용한다.
            var moves = new Vector3Int[TileHelper.GhostCount];
            int initialCount = options.Count, examined = 0;
            void Walk(Vector3Int current, int depth)
            {
                if (options.Count - initialCount >= 8 || ++examined > 128 || Stopped()) return;
                int first = _random.Next(TileHelper.Directions.Length);
                for (int i = 0; i < TileHelper.Directions.Length; i++)
                {
                    var direction = TileHelper.Directions[(first + i) % TileHelper.Directions.Length];
                    var next = current + direction;
                    if (!next.IsOnSurface(_size)) continue;
                    moves[depth] = direction;
                    if (depth == moves.Length - 1)
                    {
                        if (next != entry && Empty(next))
                            options.Add(new Placement(MapGimmick.Ghost, entry, input, next, default,
                                ghostInputs: (Vector3Int[])moves.Clone()));
                    }
                    else if (next == entry || !Empty(next)) Walk(next, depth + 1);
                    if (options.Count - initialCount >= 8 || examined > 128) return;
                }
            }
            Walk(entry, 0);
        }

        private bool RequirementsMet(int orderProgress)
        {
            foreach (var pair in _minimum) if (_placed[pair.Key] < pair.Value) return false;
            return orderProgress == _plan.RequiredOrder.Count;
        }

        private int PlacementCost(MapGimmick kind, int rayLength = 1) => _plan.Budget.Cost(kind) +
            (kind is MapGimmick.None or MapGimmick.Inverter ? 0 :
                _plan.Budget.Cost(MapGimmick.None) * (kind == MapGimmick.Laser ? rayLength + 1 : 1));

        internal static long EstimateReservation(GenerationBudgetPolicy budget, MapGimmick kind, int minimum, int ordered)
        {
            int roads = kind == MapGimmick.Laser ? 2 : kind is MapGimmick.Portal or MapGimmick.Dash or MapGimmick.Ghost ? 1 : 0;
            return (long)Math.Max(minimum, ordered) * (budget.Cost(kind) + roads * budget.Cost(MapGimmick.None));
        }

        private bool Affordable(MapGimmick kind, int rayLength = 1, int credit = 0)
        {
            long reserved = 0;
            foreach (var pair in _minimum)
                reserved += (long)Math.Max(0, pair.Value - _placed[pair.Key] - (kind == pair.Key ? 1 : 0)) * PlacementCost(pair.Key);
            return _spent + PlacementCost(kind, rayLength) - credit + reserved <= _plan.Budget.Maximum;
        }

        private bool CanPlace(MapGimmick kind, int orderProgress, int credit = 0)
        {
            if ((_plan.AllowedGimmicks & kind) == 0 || !Affordable(kind, credit: credit) ||
                _plan.Counts.TryGetValue(kind, out int exact) && _placed[kind] >= exact) return false;
            // 뒤에서 필요한 기믹의 개수를 앞 구간에서 소진하지 않도록 순서를 지킨다.
            if (orderProgress < _plan.RequiredOrder.Count && _plan.RequiredOrder[orderProgress] != kind)
                for (int i = orderProgress + 1; i < _plan.RequiredOrder.Count; i++)
                    if (_plan.RequiredOrder[i] == kind) return false;
            return true;
        }

        private void Apply(Placement action)
        {
            _inputs.Add(action.Input);
            if (action.Kind == MapGimmick.None) { Set(action.Entry, TileType.Road); return; }
            _placed[action.Kind]++;
            if (action.Kind == MapGimmick.Inverter) { Set(action.Entry, TileType.Inv); return; }
            if (action.Kind == MapGimmick.Ghost)
            {
                Set(action.Entry, TileType.Ghost);
                Set(action.Exit, TileType.Road);
                _inputs.AddRange(action.GhostInputs);
                return;
            }
            if (action.Kind == MapGimmick.Dash)
            {
                Set(action.Entry, (TileType)((char)TileType.DashXp + Array.IndexOf(TileHelper.Directions, action.Onward)));
                Set(action.Exit, TileType.Road);
                // 착지점까지의 강제 이동에는 추가 입력을 넣지 않는다.
                return;
            }
            if (action.Kind == MapGimmick.Laser)
            {
                Set(action.Entry, TileType.Laser);
                for (int i = 1; i <= action.RayLength; i++)
                {
                    var target = action.Entry + action.Onward * i;
                    Set(target, TileType.Road);
                    _laserTargets.Add(target);
                }
                Set(action.Exit, TileType.Road);
                // 발사 입력에서는 플레이어가 움직이지 않는다. 다음 입력으로 별도 길에 이동한다.
                _inputs.Add(action.Onward);
                _inputs.Add(action.Exit - action.Entry);
                return;
            }
            Set(action.Entry, TileType.PortalIn);
            Set(action.Exit, TileType.PortalOut);
            Set(action.Exit + action.Onward, TileType.Road);
            _links.Add(action.Entry, action.Exit);
            _links.Add(action.Exit, action.Entry);
            _inputs.Add(action.Onward);
        }
        private void Undo(Placement action)
        {
            Set(action.Entry, action.ReplacesRoad ? TileType.Road : TileType.Empty);
            if (action.Kind == MapGimmick.None) return;
            _placed[action.Kind]--;
            if (action.Kind == MapGimmick.Inverter) return;
            Set(action.Exit, TileType.Empty);
            if (action.Kind is MapGimmick.Dash or MapGimmick.Ghost) return;
            if (action.Kind == MapGimmick.Laser)
            {
                for (int i = 1; i <= action.RayLength; i++)
                {
                    var target = action.Entry + action.Onward * i;
                    Set(target, TileType.Empty);
                    _laserTargets.Remove(target);
                }
                return;
            }
            Set(action.Exit + action.Onward, TileType.Empty);
            _links.Remove(action.Entry);
            _links.Remove(action.Exit);
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
        private bool Empty(Vector3Int p) => p.IsOnSurface(_size) && InitialTile(p) == (char)TileType.Empty;
        private void Set(Vector3Int p, TileType tile) => _board[p.x, p.y, p.z] = (char)tile;
        private static int Distance(Vector3Int a, Vector3Int b) => Math.Abs(a.x - b.x) + Math.Abs(a.y - b.y) + Math.Abs(a.z - b.z);
        private MapGenerationResult Failure(MapGenerationStatus status, string message) => new(status, null, message, _plan);

        private readonly struct Placement
        {
            internal readonly Vector3Int Entry, Input, Exit, Onward;
            internal readonly MapGimmick Kind;
            internal readonly int RayLength;
            internal readonly Vector3Int[] GhostInputs;
            internal readonly bool ReplacesRoad;
            internal Placement(Vector3Int entry, Vector3Int input) =>
                this = new Placement(MapGimmick.None, entry, input, default, default);
            internal Placement(MapGimmick kind, Vector3Int entry, Vector3Int input, Vector3Int exit, Vector3Int onward,
                int rayLength = 0, Vector3Int[] ghostInputs = null, bool replacesRoad = false) =>
                (Entry, Input, Exit, Onward, Kind, RayLength, GhostInputs, ReplacesRoad) =
                (entry, input, exit, onward, kind, rayLength, ghostInputs, replacesRoad);
        }
    }
}
