using System;
using System.Collections.Generic;
using System.Threading;
using EulerianAurora.Maps;
using UnityEngine;
using static TileHelper;

public static class PuzzleSolver
{
    internal const int DefaultStateLimit = 70_000;

    public enum SolveStatus
    {
        Solved,
        Unsolvable,
        Inconclusive,
        InvalidMap
    }

    public sealed class SolveResult
    {
        public SolveStatus Status { get; }
        public bool IsSolvable => Status == SolveStatus.Solved;
        public string ErrorMsg { get; }
        public Stack<Vector3Int> SolutionPath { get; }
        public IReadOnlyList<Vector3Int> SolutionInputs { get; }
        public int ExploredStates { get; }
        public MapGimmick UsedGimmicks { get; }
        public int ChoicePoints { get; }

        internal SolveResult(SolveStatus status, string message, int explored = 0,
            Stack<Vector3Int> path = null, IReadOnlyList<Vector3Int> inputs = null,
            MapGimmick used = MapGimmick.None, int choicePoints = 0)
        {
            Status = status;
            ErrorMsg = message;
            ExploredStates = explored;
            SolutionPath = path;
            SolutionInputs = inputs;
            UsedGimmicks = used;
            ChoicePoints = choicePoints;
        }
    }

    public static SolveResult Solve(char[,,] map, Dictionary<Vector3Int, Vector3Int> portalPairs,
        int rotationAxis = 0, bool[] canRotate = null, int stateLimit = DefaultStateLimit,
        CancellationToken cancellationToken = default)
    {
        if (stateLimit < 1)
            throw new ArgumentOutOfRangeException(nameof(stateLimit));
        var error = PrepareSearch(map, portalPairs, rotationAxis, canRotate, out var search, out var initial);
        return error ?? search.Solve(initial, stateLimit, cancellationToken);
    }

    public static SolveResult VerifySequence(char[,,] map, Dictionary<Vector3Int, Vector3Int> portalPairs,
        IReadOnlyList<Vector3Int> inputs, int rotationAxis = 0, bool[] canRotate = null)
        => VerifySequence(map, portalPairs, inputs, rotationAxis, canRotate, false);

    internal static SolveResult VerifyPortalInverterSequence(char[,,] map,
        Dictionary<Vector3Int, Vector3Int> portalPairs, IReadOnlyList<Vector3Int> inputs,
        int rotationAxis, bool[] canRotate, bool measureChoices = false)
        => VerifySequence(map, portalPairs, inputs, rotationAxis, canRotate, true, measureChoices);

    internal static SolveResult MeasureSequence(char[,,] map, Dictionary<Vector3Int, Vector3Int> portalPairs,
        IReadOnlyList<Vector3Int> inputs, int rotationAxis, bool[] canRotate)
        => VerifySequence(map, portalPairs, inputs, rotationAxis, canRotate, false, measureChoices: true);

    private static SolveResult VerifySequence(char[,,] map, Dictionary<Vector3Int, Vector3Int> portalPairs,
        IReadOnlyList<Vector3Int> inputs, int rotationAxis, bool[] canRotate, bool requireSequence,
        bool measureChoices = false)
    {
        if (inputs == null)
            throw new ArgumentNullException(nameof(inputs));
        var error = PrepareSearch(map, portalPairs, rotationAxis, canRotate, out var search, out var initial);
        if (error != null)
            return error;

        var directionIndices = new int[inputs.Count];
        for (int i = 0; i < inputs.Count; i++)
            directionIndices[i] = Array.IndexOf(Directions, inputs[i]);
        return search.Replay(initial, directionIndices, 0, requireSequence, measureChoices);
    }

    internal static SolveResult FindGimmickRoute(char[,,] map, Dictionary<Vector3Int, Vector3Int> portals,
        int axis, bool[] layers, MapGimmick required, bool bypass, int limit, CancellationToken token)
    {
        var error = PrepareSearch(map, portals, axis, layers, out var search, out var initial);
        return error ?? search.FindGimmickRoute(initial, required, bypass, limit, token);
    }

    internal static SolveStatus CheckPortalInverterBypass(char[,,] map,
        Dictionary<Vector3Int, Vector3Int> portalPairs, int rotationAxis, bool[] canRotate,
        int stateLimit, CancellationToken token)
    {
        if (stateLimit < 1)
            throw new ArgumentOutOfRangeException(nameof(stateLimit));
        var error = PrepareSearch(map, portalPairs, rotationAxis, canRotate, out var search, out var initial);
        return error?.Status ?? search.FindBypass(initial, stateLimit, token);
    }

    private static SolveResult PrepareSearch(char[,,] map, Dictionary<Vector3Int, Vector3Int> portalPairs,
        int rotationAxis, bool[] canRotate, out Search search, out State initial)
    {
        search = null;
        initial = null;
        if (map == null || map.Length == 0 || map.GetLength(0) != map.GetLength(1) ||
            map.GetLength(0) != map.GetLength(2))
            return new SolveResult(SolveStatus.InvalidMap, "맵 크기가 올바르지 않습니다.");
        if (rotationAxis < 0 || rotationAxis > 3 ||
            rotationAxis != 0 && (canRotate == null || canRotate.Length != map.GetLength(0)))
            return new SolveResult(SolveStatus.InvalidMap, "회전 설정이 올바르지 않습니다.");
        search = new Search(map.GetLength(0), rotationAxis, canRotate);
        initial = search.ReadMap(map, portalPairs, out var error);
        return initial == null ? new SolveResult(SolveStatus.InvalidMap, error) : null;
    }

    private sealed class State
    {
        public readonly char[] Tiles;
        public readonly int[] Portals;
        public int Position;
        public int Unpainted;
        public int Ghost;
        public bool Laser;
        public byte SequenceProgress;
        public MapGimmick UsedGimmicks;

        public State(char[] tiles, int[] portals)
        {
            Tiles = tiles;
            Portals = portals;
        }

        public State Copy()
        {
            // Portal coordinates change only when a layer rotates.
            return new State((char[])Tiles.Clone(), Portals)
            {
                Position = Position, Unpainted = Unpainted, Ghost = Ghost, Laser = Laser,
                SequenceProgress = SequenceProgress, UsedGimmicks = UsedGimmicks
            };
        }
    }

    private readonly struct Node
    {
        public readonly State State;
        public readonly int TraceIndex;
        public readonly int Direction;

        public Node(State state, int traceIndex, int direction)
        {
            State = state;
            TraceIndex = traceIndex;
            Direction = direction;
        }
    }

    private sealed class Search
    {
        private const int RetryAfter = 12_000;
        private const int RetryStateLimit = 12_000;
        private const int InverterRetryStateLimit = 12_000;

        private readonly int _size;
        private readonly int _last;
        private readonly int _axis;
        private readonly Vector3Int[] _positions;
        private readonly bool[] _surface;
        private readonly int[] _surfaceCells;
        private readonly int[,] _neighbors;
        private readonly int[][] _rotationGroups;
        private readonly List<int>[] _layers;
        private readonly int[] _queue;
        private readonly int[] _reached;
        private int _visit;
        private int[][] _possibleNeighbors;
        private bool _invalidTransition;

        public Search(int size, int rotationAxis, bool[] canRotate)
        {
            _size = size;
            _last = size - 1;
            _axis = rotationAxis - 1;
            int volume = size * size * size;
            _positions = new Vector3Int[volume];
            _surface = new bool[volume];
            _neighbors = new int[volume, Directions.Length];
            _queue = new int[volume];
            _reached = new int[volume];
            _layers = new List<int>[size];
            _rotationGroups = new int[size][];
            var surfaceCells = new List<int>();
            for (int i = 0; i < size; i++)
                _layers[i] = new List<int>();

            for (int x = 0; x < size; x++)
            for (int y = 0; y < size; y++)
            for (int z = 0; z < size; z++)
            {
                var position = new Vector3Int(x, y, z);
                int cell = Index(position);
                _positions[cell] = position;
                _surface[cell] = position.IsOnSurface(size);
                if (_surface[cell])
                    surfaceCells.Add(cell);
                for (int d = 0; d < Directions.Length; d++)
                {
                    var neighbor = position + Directions[d];
                    _neighbors[cell, d] = neighbor.IsInBounds(size) ? Index(neighbor) : -1;
                }

                if (_axis >= 0)
                    _layers[position[_axis]].Add(cell);
            }
            _surfaceCells = surfaceCells.ToArray();

            if (_axis < 0)
                return;

            for (int layer = 0; layer < size; layer++)
            {
                if (!canRotate[layer])
                    continue;
                var group = new List<int> { layer };
                bool lower = true, upper = true;
                for (int distance = 1; lower || upper; distance++)
                {
                    int lo = layer - distance, hi = layer + distance;
                    lower = lower && lo >= 0 && canRotate[lo];
                    upper = upper && hi < size && canRotate[hi];
                    if (lower) group.Add(lo);
                    if (upper) group.Add(hi);
                }
                _rotationGroups[layer] = group.ToArray();
            }
        }

        private int Index(Vector3Int p) => (p.x * _size + p.y) * _size + p.z;

        public State ReadMap(char[,,] map, Dictionary<Vector3Int, Vector3Int> portalPairs,
            out string error)
        {
            var tiles = new char[_positions.Length];
            var portals = portalPairs != null && portalPairs.Count != 0 ? new int[tiles.Length] : null;
            if (portals != null)
                Array.Fill(portals, -1);
            var state = new State(tiles, portals) { Position = -1 };
            for (int cell = 0; cell < tiles.Length; cell++)
            {
                var p = _positions[cell];
                char tile = map[p.x, p.y, p.z];
                tiles[cell] = tile;
                if (tile == (char)TileType.Empty)
                    continue;
                if (!_surface[cell] || TileHelper.IsPainted(tile) || !TileHelper.PaintTable.ContainsKey(tile))
                {
                    error = "맵에 유효하지 않은 타일이 있습니다.";
                    return null;
                }
                if (tile == (char)TileType.Player)
                {
                    if (state.Position >= 0)
                    {
                        error = "시작 위치가 2개 이상입니다.";
                        return null;
                    }
                    state.Position = cell;
                    tiles[cell] = (char)TileType.Painted;
                    continue;
                }

                state.Unpainted++;
                if (!IsPortal(tile))
                    continue;
                if (portalPairs == null || !portalPairs.TryGetValue(p, out var partner) ||
                    !partner.IsInBounds(_size) || !portalPairs.TryGetValue(partner, out var reverse) || reverse != p ||
                    map[partner.x, partner.y, partner.z] != (char)(tile == (char)TileType.PortalIn
                        ? TileType.PortalOut : TileType.PortalIn))
                {
                    error = "연결이 올바르지 않은 포탈이 있습니다.";
                    return null;
                }
                portals[cell] = Index(partner);
            }

            error = state.Position < 0 ? "시작 위치를 찾을 수 없습니다." : null;
            return error == null ? state : null;
        }

        public SolveResult Solve(State initial, int limit, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (_axis < 0 && Array.TrueForAll(initial.Tiles, tile =>
                    tile is (char)TileType.Empty or (char)TileType.Painted or (char)TileType.Road) &&
                !HasValidStartColor(initial))
                return new SolveResult(SolveStatus.Unsolvable,
                    "정답이 존재하지 않습니다.");

            bool canUseRotationGraph = _axis >= 0 && Array.TrueForAll(initial.Tiles, tile =>
                tile == (char)TileType.Empty || tile == (char)TileType.Painted ||
                tile == (char)TileType.Road || tile == (char)TileType.Laser || IsDash(tile));

            bool checkRotationConnectivity = _axis >= 0 &&
                Array.IndexOf(initial.Tiles, (char)TileType.Inv) < 0;
            if (initial.Unpainted > 0 && (canUseRotationGraph ||
                checkRotationConnectivity && initial.Portals == null))
            {
                var graph = BuildRotationGraph(token);
                if (checkRotationConnectivity && initial.Portals == null)
                {
                    // Ghosts can revisit occupied cells, but they cannot connect separate components.
                    int unreachable = FindUnreachableTile(initial, graph);
                    if (unreachable >= 0)
                        return new SolveResult(SolveStatus.Unsolvable,
                            $"정답이 존재하지 않습니다.");
                }
                if (canUseRotationGraph)
                    _possibleNeighbors = graph;
            }
            if (initial.Unpainted > 0 && checkRotationConnectivity && initial.Portals != null)
            {
                int unreachable = FindUnreachableTileWithPortals(initial, token);
                if (unreachable >= 0)
                    return new SolveResult(SolveStatus.Unsolvable,
                        $"정답이 존재하지 않습니다.");
            }

            // Keep the original frontier and budget while probing a different tie order.
            int retryBudget = limit > RetryAfter ? Math.Min(RetryStateLimit, int.MaxValue - limit) : 0;
            int explored = 0;
            var result = Explore(initial, limit, canUseRotationGraph, null, retryBudget, token, ref explored);
            if (result == null && limit >= DefaultStateLimit &&
                Array.IndexOf(initial.Tiles, (char)TileType.Inv) >= 0)
            {
                // Only try this order after the original search uses its full budget.
                int inverterBudget = Math.Min(InverterRetryStateLimit, int.MaxValue - explored);
                if (inverterBudget > 0)
                    result = Explore(initial, inverterBudget, false, null, 0, token, ref explored,
                        preferInverter: true);
            }
            return result ?? new SolveResult(SolveStatus.Inconclusive,
                $"탐색 한도 도달!", explored);
        }

        private bool HasValidStartColor(State state)
        {
            int startColor = Color(state.Position);
            int balance = 0;
            foreach (int cell in _surfaceCells)
                if (state.Tiles[cell] != (char)TileType.Empty)
                    balance += Color(cell) == startColor ? 1 : -1;
            return balance is 0 or 1;

            int Color(int cell)
            {
                var p = _positions[cell];
                return (p.x + p.y + p.z) & 1;
            }
        }

        private int FindUnreachableTile(State state, int[][] graph)
        {
            int count = 1;
            _queue[0] = state.Position;
            _reached[state.Position] = ++_visit;
            for (int i = 0; i < count; i++)
            {
                foreach (int neighbor in graph[_queue[i]])
                {
                    if (_reached[neighbor] == _visit || state.Tiles[neighbor] == (char)TileType.Empty)
                        continue;
                    _reached[neighbor] = _visit;
                    _queue[count++] = neighbor;
                }
            }
            return FirstUnreachedTile(state);
        }

        private int FindUnreachableTileWithPortals(State state, CancellationToken token)
        {
            var nextRotation = new int[state.Tiles.Length];
            var possibleTiles = new bool[state.Tiles.Length];
            var possiblePortals = new bool[state.Tiles.Length];
            var portalDestinations = new List<int>();

            foreach (int cell in _surfaceCells)
            {
                var position = _positions[cell];
                nextRotation[cell] = _rotationGroups[position[_axis]] == null
                    ? cell
                    : Index(RotatePosition(position, true, _last));
            }
            foreach (int cell in _surfaceCells)
            {
                char tile = state.Tiles[cell];
                if (tile == (char)TileType.Empty)
                    continue;
                int pose = cell;
                do
                {
                    possibleTiles[pose] = true;
                    if (IsPortal(tile) && !possiblePortals[pose])
                    {
                        possiblePortals[pose] = true;
                        portalDestinations.Add(pose);
                    }
                    pose = nextRotation[pose];
                } while (pose != cell);
            }

            // Allow all poses and even jumps between unrelated portal endpoints.
            // Any missing connection in this graph is also missing in the real game.
            int count = 0;
            ++_visit;
            Visit(state.Position);
            bool visitedPortals = false;
            for (int i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                int cell = _queue[i];
                for (int direction = 0; direction < Directions.Length; direction++)
                    Visit(_neighbors[cell, direction]);
                Visit(nextRotation[cell]);
                if (!visitedPortals && possiblePortals[cell])
                {
                    visitedPortals = true;
                    foreach (int destination in portalDestinations)
                        Visit(destination);
                }
            }
            return FirstUnreachedTile(state);

            void Visit(int cell)
            {
                if (cell < 0 || !possibleTiles[cell] || _reached[cell] == _visit)
                    return;
                _reached[cell] = _visit;
                _queue[count++] = cell;
            }
        }

        private int FirstUnreachedTile(State state)
        {
            foreach (int cell in _surfaceCells)
                if (_reached[cell] != _visit && state.Tiles[cell] != (char)TileType.Empty)
                    return cell;
            return -1;
        }

        public SolveResult FindGimmickRoute(State initial, MapGimmick required, bool bypass, int budget,
            CancellationToken token)
        {
            int explored = 0;
            return Explore(initial, budget, false, null, 0, token, ref explored, required: required, bypass: bypass)
                ?? new SolveResult(SolveStatus.Inconclusive, "기믹 검사 탐색 한도에 도달했습니다.", explored);
        }

        private SolveResult Explore(State initial, int budget, bool depthFirst, System.Random tieOrder,
            int retryBudget, CancellationToken token, ref int explored, bool preferInverter = false,
            MapGimmick required = MapGimmick.None, bool bypass = false)
        {
            int visited = 0;
            var pending = new SortedDictionary<int, Stack<Node>>();
            var seen = new HashSet<(string, string, int, int, bool, int, MapGimmick)>();
            bool trackGimmicks = required != MapGimmick.None;
            // Replay uses only the parent index and input, so expanded boards can be collected.
            var trace = new List<(int parent, int direction)>();
            var root = new Node(initial, -1, -1);
            Enqueue(root);
            seen.Add(Key(initial));
            var children = new List<Node>(Directions.Length);
            while (pending.Count != 0)
            {
                token.ThrowIfCancellationRequested();
                var first = pending.GetEnumerator();
                first.MoveNext();
                var bucket = first.Current;
                first.Dispose();
                var node = bucket.Value.Pop();
                if (bucket.Value.Count == 0)
                    pending.Remove(bucket.Key);

                explored++;
                visited++;
                if (node.State.Unpainted == 0)
                {
                    if (Accepts(node.State))
                        return BuildSolution(trace, node.TraceIndex, initial, explored);
                    continue;
                }
                if (visited >= budget)
                    return null;
                if (retryBudget > 0 && visited == RetryAfter)
                {
                    bool originalInvalidTransition = _invalidTransition;
                    var retry = Explore(initial, retryBudget, depthFirst, new System.Random(1), 0, token, ref explored);
                    if (retry != null && retry.Status != SolveStatus.Inconclusive)
                        return retry;
                    _invalidTransition = originalInvalidTransition;
                }
                if (!trackGimmicks && CannotFinish(node.State))
                    continue;

                children.Clear();
                for (int d = 0; d < Directions.Length; d++)
                {
                    var next = Advance(node.State, d, trackGimmicks: trackGimmicks);
                    if (next == null)
                        continue;
                    if (trackGimmicks)
                        next.UsedGimmicks &= required;
                    bool solved = next.Unpainted == 0;
                    if (bypass && (next.UsedGimmicks & required) == required || solved && !Accepts(next))
                        continue;
                    if (!solved && (seen.Count >= budget || !seen.Add(Key(next))))
                        continue;

                    int traceIndex = trace.Count;
                    trace.Add((node.TraceIndex, d));
                    if (solved)
                        return BuildSolution(trace, traceIndex, initial, explored);
                    children.Add(new Node(next, traceIndex, d));
                }
                if (depthFirst)
                    children.Sort((a, b) =>
                    {
                        int progress = b.State.Unpainted.CompareTo(a.State.Unpainted);
                        return progress != 0 ? progress : b.Direction.CompareTo(a.Direction);
                    });
                if (tieOrder != null)
                    ShuffleTies(children, depthFirst, tieOrder);
                foreach (var child in children)
                    Enqueue(child);
            }

            return _invalidTransition
                ? new SolveResult(SolveStatus.Inconclusive, "탐색 중 올바르지 않은 포탈 연결을 발견했습니다.", explored)
                : new SolveResult(SolveStatus.Unsolvable, "해답이 존재하지 않습니다.", explored);

            bool Accepts(State state) => !trackGimmicks || ((state.UsedGimmicks & required) == required) != bypass;

            void Enqueue(Node node)
            {
                int priority = depthFirst ? 0 : node.State.Unpainted;
                if (trackGimmicks && !bypass && (node.State.UsedGimmicks & required) != required)
                    priority += _surfaceCells.Length;
                if (preferInverter)
                    priority = InverterPriority(node, trace);
                if (!pending.TryGetValue(priority, out var bucket))
                    pending.Add(priority, bucket = new Stack<Node>());
                bucket.Push(node);
            }
        }

        private int InverterPriority(Node node, List<(int parent, int direction)> trace)
        {
            var position = _positions[node.State.Position];
            int distance = int.MaxValue;
            foreach (int cell in _surfaceCells)
            {
                if (node.State.Tiles[cell] != (char)TileType.Inv)
                    continue;
                var target = _positions[cell];
                distance = Math.Min(distance, Math.Abs(position.x - target.x) +
                    Math.Abs(position.y - target.y) + Math.Abs(position.z - target.z));
            }
            if (distance == int.MaxValue)
                return node.State.Unpainted;

            int inputs = 0;
            for (int i = node.TraceIndex; i >= 0; i = trace[i].parent)
                inputs++;

            // States after the inversion take priority over states still approaching it.
            return _surfaceCells.Length + inputs + distance;
        }

        private static void ShuffleTies(List<Node> children, bool depthFirst, System.Random order)
        {
            for (int start = 0; start < children.Count;)
            {
                int end = start + 1;
                // Greedy search separates priorities when it enqueues children.
                while (end < children.Count && (!depthFirst ||
                    children[end].State.Unpainted == children[start].State.Unpainted))
                    end++;
                for (int i = end - 1; i > start; i--)
                {
                    int j = order.Next(start, i + 1);
                    (children[i], children[j]) = (children[j], children[i]);
                }
                start = end;
            }
        }

        private (string, string, int, int, bool, int, MapGimmick) Key(State state)
        {
            var tiles = state.Tiles;
            string portals = null;
            if (_axis >= 0 && state.Portals != null)
            {
                // Only occupied portal positions can be read by a later move or rotation.
                var links = new List<char>();
                foreach (int i in _surfaceCells)
                    if (IsPortal(tiles[i]))
                    {
                        int partner = state.Portals[i];
                        links.Add((char)partner);
                        links.Add((char)(partner >> 16));
                    }
                if (links.Count != 0)
                    portals = new string(links.ToArray());
            }

            // Interior cells stay empty. TileType codes fit in a byte, so store two per char.
            var board = string.Create((_surfaceCells.Length + 1) / 2, (tiles, cells: _surfaceCells),
                static (key, source) =>
                {
                    for (int i = 0; i < source.cells.Length; i += 2)
                    {
                        int packed = source.tiles[source.cells[i]];
                        if (i + 1 < source.cells.Length)
                            packed |= source.tiles[source.cells[i + 1]] << 8;
                        key[i / 2] = (char)packed;
                    }
                });
            return (board, portals, state.Position, state.Ghost, state.Laser, state.Unpainted, state.UsedGimmicks);
        }

        private SolveResult BuildSolution(List<(int parent, int direction)> trace, int traceIndex,
            State initial, int explored)
        {
            var inputs = new Stack<int>();
            for (int i = traceIndex; i >= 0; i = trace[i].parent)
                inputs.Push(trace[i].direction);
            return Replay(initial, inputs, explored);
        }

        public SolveStatus FindBypass(State initial, int limit, CancellationToken token)
        {
            var pending = new Stack<State>();
            var seen = new HashSet<((string, string, int, int, bool, int, MapGimmick), byte)>
                { (Key(initial), initial.SequenceProgress) };
            pending.Push(initial);
            bool truncated = false;
            while (pending.Count != 0)
            {
                token.ThrowIfCancellationRequested();
                var state = pending.Pop();
                if (state.Unpainted == 0)
                {
                    if (Array.TrueForAll(state.Tiles, tile => tile == (char)TileType.Empty || TileHelper.IsPainted(tile)))
                        return SolveStatus.Solved;
                    truncated = true;
                    continue;
                }
                for (int direction = 0; direction < Directions.Length; direction++)
                {
                    var next = Advance(state, direction, trackSequence: true);
                    if (next == null || next.SequenceProgress == 2)
                        continue;
                    var key = (Key(next), next.SequenceProgress);
                    if (seen.Contains(key))
                        continue;
                    if (seen.Count == limit)
                    {
                        truncated = true;
                        continue;
                    }
                    seen.Add(key);
                    pending.Push(next);
                }
            }
            return truncated || _invalidTransition ? SolveStatus.Inconclusive : SolveStatus.Unsolvable;
        }

        public SolveResult Replay(State initial, IReadOnlyCollection<int> inputs, int explored,
            bool requireSequence = false, bool measureChoices = false)
        {
            var positions = new Stack<Vector3Int>();
            var directions = new List<Vector3Int>(inputs.Count);
            var state = initial;
            int choices = 0;
            foreach (int input in inputs)
            {
                int step = directions.Count + 1;
                if (input < 0 || state.Unpainted == 0)
                    return new SolveResult(SolveStatus.Inconclusive,
                        $"{step}번째 입력이 유효하지 않거나 이미 클리어했습니다.", explored);
                if (measureChoices)
                {
                    for (int alternative = 0; alternative < Directions.Length; alternative++)
                    {
                        if (alternative == input || Advance(state, alternative) == null)
                            continue;
                        choices++;
                        break;
                    }
                }
                state = Advance(state, input, positions, requireSequence, trackGimmicks: true);
                if (state == null)
                    return new SolveResult(SolveStatus.Inconclusive, $"{step}번째 입력으로 이동할 수 없습니다.", explored);
                directions.Add(Directions[input]);
            }

            if (state.Unpainted != 0)
                return new SolveResult(SolveStatus.Inconclusive, $"입력 종료 후 미도색 칸이 {state.Unpainted}개 남았습니다.", explored);
            if (Array.Exists(state.Tiles, tile => tile != (char)TileType.Empty && !TileHelper.IsPainted(tile)))
                return new SolveResult(SolveStatus.Inconclusive, "남은 칸 수와 타일의 색칠 상태가 일치하지 않습니다.", explored);
            if (requireSequence && state.SequenceProgress != 2)
                return new SolveResult(SolveStatus.Inconclusive, "포탈 출구 도착 후 인버터가 발동하는 순서를 확인하지 못했습니다.", explored);
            return new SolveResult(SolveStatus.Solved, null, explored, positions, directions.AsReadOnly(),
                state.UsedGimmicks, choices);
        }

        private State Advance(State initial, int direction, Stack<Vector3Int> path = null, bool trackSequence = false,
            bool trackGimmicks = false)
        {
            var state = initial;
            bool dashing = false;
            while (true)
            {
                int neighbor = _neighbors[state.Position, direction];
                if (neighbor < 0 || !_surface[neighbor])
                    return state == initial ? null : state;

                char tile = state.Tiles[neighbor];
                if (tile == (char)TileType.Empty || state.Ghost == 0 && TileHelper.IsPainted(tile))
                {
                    int[] group = _axis >= 0 ? _rotationGroups[_positions[state.Position][_axis]] : null;
                    if (state.Ghost > 0 || group == null || Directions[direction][_axis] != 0)
                        return state == initial ? null : state;
                    var rotated = Rotate(state, direction, group, path);
                    if (rotated != null && trackGimmicks)
                        rotated.UsedGimmicks |= MapGimmick.Rotation;
                    return rotated;
                }

                if (state == initial)
                    state = initial.Copy();
                path?.Push(_positions[neighbor]);
                if (state.Ghost > 0)
                {
                    if (trackGimmicks)
                        state.UsedGimmicks |= MapGimmick.Ghost;
                    state.Ghost--;
                }
                if (state.Laser)
                {
                    if (trackGimmicks)
                        state.UsedGimmicks |= MapGimmick.Laser;
                    FireLaser(state, neighbor, direction);
                    return state;
                }

                if (dashing && trackGimmicks)
                    state.UsedGimmicks |= MapGimmick.Dash;
                state.Position = neighbor;
                if (state.Ghost > 0)
                    return state;
                Paint(state, neighbor);

                switch ((TileType)tile)
                {
                    case TileType.PortalIn:
                        int exit = state.Portals[neighbor];
                        if (exit < 0)
                        {
                            _invalidTransition = true;
                            return null;
                        }
                        state.Position = exit;
                        if (trackGimmicks)
                            state.UsedGimmicks |= MapGimmick.Portal;
                        if (trackSequence && state.SequenceProgress == 0)
                            state.SequenceProgress = 1;
                        path?.Push(_positions[exit]);
                        if (!TileHelper.IsPainted(state.Tiles[exit]))
                        {
                            if (state.Tiles[exit] == (char)TileType.Empty)
                                state.Unpainted--; // Matches PuzzlePlayer's portal exit counter.
                            else
                                Paint(state, exit);
                        }
                        return state;
                    case TileType.Ghost:
                        state.Ghost = TileHelper.GhostCount;
                        return state;
                    case TileType.Inv:
                        if (trackGimmicks)
                            state.UsedGimmicks |= MapGimmick.Inverter;
                        if (trackSequence && state.SequenceProgress == 1)
                            state.SequenceProgress = 2;
                        Invert(state);
                        return state;
                    case TileType.Laser:
                        state.Laser = true;
                        return state;
                }

                if (!IsDash(tile))
                    return state;
                dashing = true;
                direction = tile - (char)TileType.DashXp;
            }
        }

        private static void Paint(State state, int cell)
        {
            char tile = state.Tiles[cell];
            if (TileHelper.IsPainted(tile))
                return;
            state.Tiles[cell] = TileHelper.PaintTable[tile];
            state.Unpainted--;
        }

        private void FireLaser(State state, int cell, int direction)
        {
            state.Laser = false;
            do
            {
                state.Tiles[cell] = TileHelper.InvTable[state.Tiles[cell]];
                state.Unpainted--;
                cell = _neighbors[cell, direction];
            } while (cell >= 0 && state.Tiles[cell] != (char)TileType.Empty &&
                     (state.Ghost > 0 || !TileHelper.IsPainted(state.Tiles[cell])));
        }

        private void Invert(State state)
        {
            state.Unpainted = 0;
            for (int i = 0; i < state.Tiles.Length; i++)
            {
                if (i == state.Position || state.Tiles[i] == (char)TileType.Empty)
                    continue;
                state.Tiles[i] = TileHelper.InvTable[state.Tiles[i]];
                if (!TileHelper.IsPainted(state.Tiles[i]))
                    state.Unpainted++;
            }
        }

        private State Rotate(State prior, int direction, int[] group, Stack<Vector3Int> path)
        {
            var position = _positions[prior.Position];
            var d = Directions[direction];
            bool clockwise = _axis switch
            {
                0 => position.z == 0 && d.y > 0 || position.z == _last && d.y < 0 ||
                     position.y == 0 && d.z < 0 || position.y == _last && d.z > 0,
                1 => position.x == 0 && d.z > 0 || position.x == _last && d.z < 0 ||
                     position.z == 0 && d.x < 0 || position.z == _last && d.x > 0,
                _ => position.x == 0 && d.y < 0 || position.x == _last && d.y > 0 ||
                     position.y == 0 && d.x > 0 || position.y == _last && d.x < 0
            };

            var tiles = (char[])prior.Tiles.Clone();
            var portals = (int[])prior.Portals?.Clone();
            foreach (int layer in group)
            foreach (int cell in _layers[layer])
            {
                int target = Index(RotatePosition(_positions[cell], clockwise, _last));
                char tile = prior.Tiles[cell];
                if (IsPortal(tile))
                {
                    // Use the same layer order and portal updates as PuzzlePlayer.ApplyRotate.
                    int partner = portals[cell];
                    if (partner < 0)
                    {
                        _invalidTransition = true;
                        return null;
                    }
                    portals[cell] = -1;
                    portals[target] = partner;
                    portals[partner] = target;
                }

                if (IsDash(tile) || tile >= (char)TileType.DashXpPainted && tile <= (char)TileType.DashZmPainted)
                {
                    char first = IsDash(tile) ? (char)TileType.DashXp : (char)TileType.DashXpPainted;
                    var rotated = RotatePosition(Directions[tile - first], clockwise, 0);
                    tile = (char)(first + Array.IndexOf(Directions, rotated));
                }

                tiles[target] = tile;
            }

            var rotatedPosition = RotatePosition(position, clockwise, _last);
            path?.Push(rotatedPosition);
            return new State(tiles, portals)
            {
                Position = Index(rotatedPosition), Unpainted = prior.Unpainted,
                Ghost = prior.Ghost, Laser = prior.Laser,
                UsedGimmicks = prior.UsedGimmicks, SequenceProgress = prior.SequenceProgress
            };
        }

        private Vector3Int RotatePosition(Vector3Int p, bool clockwise, int last) => _axis switch
        {
            0 => clockwise ? new Vector3Int(p.x, last - p.z, p.y) : new Vector3Int(p.x, p.z, last - p.y),
            1 => clockwise ? new Vector3Int(p.z, p.y, last - p.x) : new Vector3Int(last - p.z, p.y, p.x),
            _ => clockwise ? new Vector3Int(last - p.y, p.x, p.z) : new Vector3Int(p.y, last - p.x, p.z)
        };

        private static bool IsPortal(char tile) =>
            tile >= (char)TileType.PortalIn && tile <= (char)TileType.PortalOutPainted;

        private bool CannotFinish(State state)
        {
            if (_axis >= 0 && _possibleNeighbors == null)
                return false;
            var tiles = state.Tiles;
            if (state.Ghost > 0 || Array.IndexOf(tiles, (char)TileType.Ghost) >= 0 ||
                Array.IndexOf(tiles, (char)TileType.Inv) >= 0)
                return false;

            bool portal = Array.IndexOf(tiles, (char)TileType.PortalIn) >= 0;
            bool laser = state.Laser || Array.IndexOf(tiles, (char)TileType.Laser) >= 0;
            int reached = 1, leaves = 0, unpaintedReached = 0;
            _queue[0] = state.Position;
            _reached[state.Position] = ++_visit;
            for (int i = 0; i < reached; i++)
            {
                int cell = _queue[i];
                if (!TileHelper.IsPainted(tiles[cell]))
                    unpaintedReached++;
                int degree = 0;
                if (_possibleNeighbors == null)
                {
                    for (int d = 0; d < Directions.Length; d++)
                        if (Visit(_neighbors[cell, d]))
                            degree++;
                }
                else
                {
                    foreach (int neighbor in _possibleNeighbors[cell])
                        if (Visit(neighbor))
                            degree++;
                }
                if (!portal && !laser && cell != state.Position && degree == 1 && ++leaves > 1)
                    return true;
                if (portal && IsPortal(tiles[cell]))
                    Visit(state.Portals[cell]);
            }

            return unpaintedReached < state.Unpainted;

            bool Visit(int cell)
            {
                if (cell < 0)
                    return false;
                char tile = tiles[cell];
                if (cell != state.Position && (tile == (char)TileType.Empty ||
                    TileHelper.IsPainted(tile) && !(portal && tile == (char)TileType.PortalOutPainted)))
                    return false;
                if (_reached[cell] != _visit)
                {
                    _reached[cell] = _visit;
                    _queue[reached++] = cell;
                }
                return true;
            }
        }

        private int[][] BuildRotationGraph(CancellationToken token)
        {
            var orbits = new Vector3Int[_positions.Length][];
            var groups = new int[_positions.Length];
            var graph = new int[_positions.Length][];
            for (int i = 0; i < _positions.Length; i++)
            {
                var position = _positions[i];
                var group = _rotationGroups[position[_axis]];
                groups[i] = -1;
                if (group != null)
                {
                    groups[i] = group[0];
                    foreach (int layer in group)
                        groups[i] = Math.Min(groups[i], layer);
                }
                orbits[i] = new Vector3Int[group == null ? 1 : 4];
                orbits[i][0] = position;
                for (int turn = 1; turn < orbits[i].Length; turn++)
                    orbits[i][turn] = RotatePosition(orbits[i][turn - 1], true, _last);
            }

            for (int i = 0; i < graph.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                if (!_surface[i])
                    continue;
                var neighbors = new List<int>();
                for (int j = 0; j < graph.Length; j++)
                    if (i != j && _surface[j] && CanTouch(i, j))
                        neighbors.Add(j);
                graph[i] = neighbors.ToArray();
            }

            return graph;

            bool CanTouch(int i, int j)
            {
                // A contiguous group rotates together, preserving its internal adjacency.
                if (groups[i] == groups[j])
                    return Adjacent(_positions[i], _positions[j]);
                foreach (var a in orbits[i])
                foreach (var b in orbits[j])
                    if (Adjacent(a, b))
                        return true;
                return false;
            }

            static bool Adjacent(Vector3Int a, Vector3Int b) =>
                Math.Abs(a.x - b.x) + Math.Abs(a.y - b.y) + Math.Abs(a.z - b.z) == 1;
        }
    }
}
