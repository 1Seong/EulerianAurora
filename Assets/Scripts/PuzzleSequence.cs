using System;
using System.Collections.Generic;
using UnityEngine;

// An ordered construction plan that can be deserialized independently of UI or an AI provider.
[Serializable]
public sealed class PuzzleSequence
{
    public enum Operation { Move, Portal, Dash, PlaceTile, Input }
    public enum Direction { PositiveX, NegativeX, PositiveY, NegativeY, PositiveZ, NegativeZ }

    [Serializable]
    public sealed class Step
    {
        public Operation operation;
        public Vector3Int cell;
        public Direction direction;
        public Direction dashDirection;
        public TileType tile = TileType.Road;
    }

    public int size = 10;
    public Vector3Int start;
    public int rotationAxis;
    public List<int> rotatingLayers = new();
    public List<Step> steps = new();

    public sealed class GeneratedMap
    {
        public char[,,] Tiles { get; }
        public Dictionary<Vector3Int, Vector3Int> Portals { get; }
        public int RotationAxis { get; }
        public bool[] CanRotate { get; }
        public PuzzleSolver.SolveResult Solution { get; }

        internal GeneratedMap(char[,,] tiles, Dictionary<Vector3Int, Vector3Int> portals,
            int rotationAxis, bool[] canRotate, PuzzleSolver.SolveResult solution)
        {
            Tiles = tiles;
            Portals = portals;
            RotationAxis = rotationAxis;
            CanRotate = canRotate;
            Solution = solution;
        }
    }

    public GeneratedMap Generate()
    {
        if (size < 2 || size > 32)
            throw new InvalidOperationException("맵 크기는 2~32칸이어야 합니다.");
        if (steps == null || steps.Count == 0)
            throw new InvalidOperationException("시퀀스에 입력이 필요합니다.");
        if (rotationAxis < 0 || rotationAxis > 3 || rotatingLayers == null)
            throw new InvalidOperationException("회전 설정이 올바르지 않습니다.");
        if (rotationAxis == 0 && rotatingLayers.Count > 0)
            throw new InvalidOperationException("회전할 레이어를 사용하려면 축을 지정해야 합니다.");
        var canRotate = new bool[size];
        foreach (int layer in rotatingLayers)
        {
            if (layer <= 0 || layer >= size - 1)
                throw new InvalidOperationException($"회전 레이어 {layer}는 1~{size - 2} 사이여야 합니다.");
            canRotate[layer] = true;
        }

        var tiles = new char[size, size, size];
        for (int x = 0; x < size; x++)
        for (int y = 0; y < size; y++)
        for (int z = 0; z < size; z++)
            tiles[x, y, z] = (char)TileType.Empty;

        var portals = new Dictionary<Vector3Int, Vector3Int>();
        var inputs = new List<Vector3Int>();
        var cursor = start;
        bool cursorKnown = true;
        if (!cursor.IsOnSurface(size))
            throw new InvalidOperationException($"시작점 {cursor}가 보드 겉면에 있지 않습니다.");
        tiles[cursor.x, cursor.y, cursor.z] = (char)TileType.Player;
        for (int i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            int number = i + 1;
            if (step == null)
                throw new InvalidOperationException($"{number}번째 단계가 비어 있습니다.");
            if (step.operation == Operation.PlaceTile)
            {
                if (step.tile == TileType.Player || step.tile == TileType.PortalIn ||
                    step.tile == TileType.PortalOut || step.tile != TileType.Empty &&
                    (TileHelper.IsPainted((char)step.tile) || !TileHelper.PaintTable.ContainsKey((char)step.tile)))
                    throw new InvalidOperationException($"{number}번째 단계에서 배치할 수 없는 타일입니다.");
                CheckSurface(step.cell, number);
                var previous = TileAt(step.cell);
                if (previous == TileType.Player || previous == TileType.PortalIn || previous == TileType.PortalOut)
                    throw new InvalidOperationException($"{number}번째 단계에서 시작점이나 포탈을 덮어쓸 수 없습니다.");
                Place(step.cell, step.tile, number);
                continue;
            }

            var direction = ReadDirection(step.direction, number);
            inputs.Add(direction);
            if (step.operation == Operation.Input)
            {
                cursorKnown = false;
                continue;
            }
            if (!cursorKnown)
                throw new InvalidOperationException($"{number}번째 자동 배치는 입력 전용 단계 앞에 있어야 합니다.");

            var target = cursor + direction;
            CheckSurface(target, number);
            switch (step.operation)
            {
                case Operation.Move:
                    PlaceRoad(target, number);
                    cursor = target;
                    break;
                case Operation.Portal:
                    CheckSurface(step.cell, number);
                    if (target == step.cell || TileAt(target) != TileType.Empty || TileAt(step.cell) != TileType.Empty)
                        throw new InvalidOperationException($"{number}번 포탈의 입구와 출구에는 서로 다른 빈 칸이 필요합니다.");
                    Place(target, TileType.PortalIn, number);
                    Place(step.cell, TileType.PortalOut, number);
                    portals.Add(target, step.cell);
                    portals.Add(step.cell, target);
                    cursor = step.cell;
                    break;
                case Operation.Dash:
                    if (TileAt(target) != TileType.Empty)
                        throw new InvalidOperationException($"{number}번 대시의 입구에는 빈 칸이 필요합니다.");
                    var landing = target + ReadDirection(step.dashDirection, number);
                    PlaceRoad(landing, number);
                    Place(target, (TileType)((int)TileType.DashXp + (int)step.dashDirection), number);
                    cursor = landing;
                    break;
                default:
                    throw new InvalidOperationException($"{number}번째 작업 종류가 올바르지 않습니다.");
            }
        }

        if (inputs.Count == 0)
            throw new InvalidOperationException("시퀀스에 입력이 필요합니다.");
        var solution = PuzzleSolver.VerifySequence(tiles, portals, inputs, rotationAxis, canRotate);
        if (!solution.IsSolvable)
            throw new InvalidOperationException(solution.ErrorMsg);
        return new GeneratedMap(tiles, portals, rotationAxis, canRotate, solution);

        TileType TileAt(Vector3Int p) => (TileType)tiles[p.x, p.y, p.z];

        void Place(Vector3Int p, TileType tile, int number)
        {
            CheckSurface(p, number);
            tiles[p.x, p.y, p.z] = (char)tile;
        }

        void PlaceRoad(Vector3Int p, int number)
        {
            CheckSurface(p, number);
            if (TileAt(p) != TileType.Empty && TileAt(p) != TileType.Road)
                throw new InvalidOperationException($"{number}번째 이동 칸 {p}에 다른 타일이 있습니다.");
            Place(p, TileType.Road, number);
        }
    }

    private void CheckSurface(Vector3Int p, int number)
    {
        if (!p.IsOnSurface(size))
            throw new InvalidOperationException($"{number}번째 단계의 좌표 {p}가 보드 겉면에 있지 않습니다.");
    }

    private static Vector3Int ReadDirection(Direction direction, int number)
    {
        if ((uint)direction >= TileHelper.Directions.Length)
            throw new InvalidOperationException($"{number}번째 단계의 방향이 올바르지 않습니다.");
        return TileHelper.Directions[(int)direction];
    }
}
