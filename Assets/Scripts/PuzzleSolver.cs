using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

public static class PuzzleSolver
{
    private static readonly Vector3Int[] Directions =
    {
        Vector3Int.right, Vector3Int.left, Vector3Int.up, Vector3Int.down,
        new Vector3Int(0, 0, 1), new Vector3Int(0, 0, -1)
    };

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

        internal SolveResult(SolveStatus status, string message, int explored = 0,
            Stack<Vector3Int> path = null, IReadOnlyList<Vector3Int> inputs = null)
        {
            Status = status;
            ErrorMsg = message;
            ExploredStates = explored;
            SolutionPath = path;
            SolutionInputs = inputs;
        }
    }

    public static SolveResult Solve(char[,,] map, Dictionary<Vector3Int, Vector3Int> portalPairs,
        int rotationAxis = 0, bool[] canRotate = null, int stateLimit = 60_000,
        CancellationToken cancellationToken = default)
    {
        if (map == null || map.Length == 0 || map.GetLength(0) != map.GetLength(1) ||
            map.GetLength(0) != map.GetLength(2))
            return new SolveResult(SolveStatus.InvalidMap, "맵 크기가 올바르지 않습니다.");
        if (rotationAxis < 0 || rotationAxis > 3 ||
            rotationAxis != 0 && (canRotate == null || canRotate.Length != map.GetLength(0)))
            return new SolveResult(SolveStatus.InvalidMap, "회전 설정이 올바르지 않습니다.");
        if (stateLimit < 1)
            throw new ArgumentOutOfRangeException(nameof(stateLimit));

        var search = new Search(map.GetLength(0), rotationAxis, canRotate);
        var initial = search.ReadMap(map, portalPairs, out var error);
        if (initial == null)
            return new SolveResult(SolveStatus.InvalidMap, error);

        return search.Solve(initial, stateLimit, cancellationToken);
    }

    private sealed class State
    {
        public readonly char[] Tiles;
        public readonly int[] Portals;
        public int Position;
        public int Unpainted;
        public int Ghost;
        public bool Laser;

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
                Position = Position, Unpainted = Unpainted, Ghost = Ghost, Laser = Laser
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
                _surface[cell] = x == 0 || y == 0 || z == 0 || x == _last || y == _last || z == _last;
                if (_surface[cell])
                    surfaceCells.Add(cell);
                for (int d = 0; d < Directions.Length; d++)
                {
                    var neighbor = position + Directions[d];
                    _neighbors[cell, d] = InBounds(neighbor) ? Index(neighbor) : -1;
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

        private bool InBounds(Vector3Int p) =>
            (uint)p.x < _size && (uint)p.y < _size && (uint)p.z < _size;

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
                    !InBounds(partner) || !portalPairs.TryGetValue(partner, out var reverse) || reverse != p ||
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
            bool canUseRotationGraph = _axis >= 0 && Array.TrueForAll(initial.Tiles, tile =>
                tile == (char)TileType.Empty || tile == (char)TileType.Painted ||
                tile == (char)TileType.Road || tile == (char)TileType.Laser || IsDash(tile));

            // Include all possible contacts between layers from the first expansion.
            if (canUseRotationGraph && initial.Unpainted > 0)
                _possibleNeighbors = BuildRotationGraph(token);

            return Explore(initial, limit, canUseRotationGraph, token);
        }

        private SolveResult Explore(State initial, int limit, bool depthFirst, CancellationToken token)
        {
            int explored = 0;
            var pending = new SortedDictionary<int, Stack<Node>>();
            var seen = new HashSet<(string tiles, string portals, int position, int ghost, bool laser, int unpainted)>();
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
                if (node.State.Unpainted == 0)
                    return BuildSolution(trace, node.TraceIndex, initial, explored);
                if (explored >= limit)
                    return new SolveResult(SolveStatus.Inconclusive,
                        $"자동 탐색 한도({limit:N0}개 상태)에 도달했습니다. 해답 존재 여부는 미확정입니다.", explored);
                if (CannotFinish(node.State))
                    continue;

                children.Clear();
                for (int d = 0; d < Directions.Length; d++)
                {
                    var next = Advance(node.State, d);
                    if (next == null)
                        continue;
                    bool solved = next.Unpainted == 0;
                    if (!solved && (seen.Count >= limit || !seen.Add(Key(next))))
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
                foreach (var child in children)
                    Enqueue(child);
            }

            return _invalidTransition
                ? new SolveResult(SolveStatus.Inconclusive, "탐색 중 올바르지 않은 포탈 연결을 발견했습니다.", explored)
                : new SolveResult(SolveStatus.Unsolvable, "해답이 존재하지 않습니다.", explored);

            void Enqueue(Node node)
            {
                int priority = depthFirst ? 0 : node.State.Unpainted;
                if (!pending.TryGetValue(priority, out var bucket))
                    pending.Add(priority, bucket = new Stack<Node>());
                bucket.Push(node);
            }
        }

        private (string, string, int, int, bool, int) Key(State state)
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
            return (board, portals, state.Position, state.Ghost, state.Laser, state.Unpainted);
        }

        private SolveResult BuildSolution(List<(int parent, int direction)> trace, int traceIndex,
            State initial, int explored)
        {
            var inputs = new Stack<int>();
            for (int i = traceIndex; i >= 0; i = trace[i].parent)
                inputs.Push(trace[i].direction);

            var positions = new Stack<Vector3Int>();
            var directions = new List<Vector3Int>(inputs.Count);
            var state = initial;
            foreach (int input in inputs)
            {
                directions.Add(Directions[input]);
                state = Advance(state, input, positions);
            }

            if (Array.Exists(state.Tiles, tile => tile != (char)TileType.Empty && !TileHelper.IsPainted(tile)))
                return new SolveResult(SolveStatus.Inconclusive, "남은 칸 수와 타일의 색칠 상태가 일치하지 않습니다.", explored);
            return new SolveResult(SolveStatus.Solved, null, explored, positions, directions.AsReadOnly());
        }

        private State Advance(State initial, int direction, Stack<Vector3Int> path = null)
        {
            var state = initial;
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
                    return Rotate(state, direction, group, path);
                }

                if (state == initial)
                    state = initial.Copy();
                path?.Push(_positions[neighbor]);
                if (state.Ghost > 0)
                    state.Ghost--;
                if (state.Laser)
                {
                    FireLaser(state, neighbor, direction);
                    return state;
                }

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
                        Invert(state);
                        return state;
                    case TileType.Laser:
                        state.Laser = true;
                        return state;
                }

                if (!IsDash(tile))
                    return state;
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
                Ghost = prior.Ghost, Laser = prior.Laser
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

        private static bool IsDash(char tile) =>
            tile >= (char)TileType.DashXp && tile <= (char)TileType.DashZm;

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
