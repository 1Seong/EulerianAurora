using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace EulerianAurora.Maps
{
    // UI 및 구조화된 백엔드 요청과 생성·검증을 연결한다.
    public enum MapGenerationStatus { CandidateFound, UnsupportedConditions, GeneratorNotReady, NoCandidateFound, SearchLimitReached }
    public sealed class MapGenerationResult
    {
        public MapGenerationStatus Status { get; }
        public MapCandidate Candidate { get; }
        public string Message { get; }
        public GenerationPlan Plan { get; }
        public MapGenerationResult(MapGenerationStatus status, MapCandidate candidate, string message, GenerationPlan plan = null) =>
            (Status, Candidate, Message, Plan) = (status, candidate, message, plan);
    }
    public sealed class MapCandidate
    {
        public MapDocument Map { get; }
        public PuzzleSolver.SolveResult Witness { get; }
        public int GenerationCost { get; }
        public int PlacementAttempts { get; }
        public int Backtracks { get; }
        internal MapCandidate(MapDocument map, PuzzleSolver.SolveResult witness,
            int generationCost = 0, int placementAttempts = 0, int backtracks = 0) =>
            (Map, Witness, GenerationCost, PlacementAttempts, Backtracks) =
                (map, witness, generationCost, placementAttempts, backtracks);
    }
    public static class MapTools
    {
        public const string GenerationHint = "신규 맵 생성 · 길·포탈·대시·레이저·고스트·인버터·회전";
        public static MapGenerationResult Generate(MapDocument map, MapAssistRequest request, int seed,
            CancellationToken token = default, MapGenerationLimits limits = null, MapGenerationOptions? options = null)
        {
            token.ThrowIfCancellationRequested();
            if (request == null || request.RequiredOrder == null)
                return new MapGenerationResult(MapGenerationStatus.UnsupportedConditions, null, "요청 조건이 올바르지 않습니다.");
            if (request.Operation != MapAssistOperation.CreateMap || request.PreserveStart || request.PreservePortals ||
                request.PreserveRotation || request.MoveKind != MapGimmick.None || request.PreserveCells == null ||
                request.PreserveFaces == null || request.PreserveCells.Length != 0 || request.PreserveFaces.Length != 0)
                return new MapGenerationResult(MapGenerationStatus.UnsupportedConditions, null,
                    "현재는 보존·수정 조건이 없는 신규 맵 생성만 지원합니다.");
            var tuning = options ?? MapGenerationOptions.Default;
            string tuningError = tuning.ValidationError();
            if (tuningError != null)
                return new MapGenerationResult(MapGenerationStatus.UnsupportedConditions, null, "맵 생성 설정: " + tuningError);
            int size = request.Size ?? map?.Size ?? tuning.Sampling.DefaultSize;
            if (size < tuning.Sampling.MinimumSize || size > tuning.Sampling.MaximumSize)
                return new MapGenerationResult(MapGenerationStatus.UnsupportedConditions, null,
                    $"맵 크기는 {tuning.Sampling.MinimumSize}~{tuning.Sampling.MaximumSize}칸이어야 합니다.");
            limits ??= new MapGenerationLimits { PlacementAttempts = tuning.Search.PlacementAttempts,
                Restarts = tuning.Search.Restarts, Milliseconds = tuning.Search.Milliseconds };
            if (limits.PlacementAttempts < 1 || limits.Restarts < 1 || limits.Milliseconds < 1)
                throw new ArgumentOutOfRangeException(nameof(limits), "생성 시도와 시간 한도는 양수여야 합니다.");
            var order = new List<MapGimmick>();
            foreach (var step in request.RequiredOrder)
            {
                if (step == MapAssistEvent.PortalExit) order.Add(MapGimmick.Portal);
                else if (step == MapAssistEvent.DashUsed) order.Add(MapGimmick.Dash);
                else if (step == MapAssistEvent.LaserFired) order.Add(MapGimmick.Laser);
                else if (step == MapAssistEvent.GhostUsed) order.Add(MapGimmick.Ghost);
                else if (step == MapAssistEvent.InverterActivated) order.Add(MapGimmick.Inverter);
                else if (step == MapAssistEvent.RotationUsed) order.Add(MapGimmick.Rotation);
                else return new MapGenerationResult(MapGenerationStatus.UnsupportedConditions, null, "지원하지 않는 기믹 순서입니다.");
            }
            var extracted = new GenerationConditions
            {
                Difficulty = request.Difficulty == MapDifficulty.Any ? null : request.Difficulty,
                RequiredGimmicks = request.RequiredGimmicks,
                RequiredOrder = order.ToArray(),
                Counts = request.GimmickCounts
            };
            int rotationPlacements = NewMapGenerator.RequiredRotationPlacements(order, tuning.Sampling.MaximumConsecutiveTurns);
            if (!GenerationConditionResolver.TryResolve(request.UiConditions, extracted, out var plan, out var conflicts,
                seed: seed, estimateReservation: (budget, kind, minimum, ordered) =>
                    NewMapGenerator.EstimateReservation(budget, kind, minimum, ordered, rotationPlacements, tuning.Sampling),
                options: tuning))
                return new MapGenerationResult(MapGenerationStatus.UnsupportedConditions, null, string.Join("\n", conflicts));
            var unsupported = plan.RequiredGimmicks & ~NewMapGenerator.Supported;
            foreach (var entry in plan.Counts)
                if (entry.Value > 0) unsupported |= entry.Key & ~NewMapGenerator.Supported;
            if (unsupported != MapGimmick.None)
                return new MapGenerationResult(MapGenerationStatus.UnsupportedConditions, null,
                    $"필수 또는 개수 조건에 아직 생성하지 못하는 기믹이 있습니다: {unsupported}.", plan);
            return new NewMapGenerator(size, plan, seed, limits, tuning, token).Run();
        }
        public static PuzzleSolver.SolveResult Solve(MapDocument map,
            int stateLimit = PuzzleSolver.DefaultStateLimit, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (map == null)
                return Invalid("맵 데이터가 필요합니다.");
            var error = map.Decode(out var tiles, out var links);
            return error == null
                ? PuzzleSolver.Solve(tiles, links, map.RotationAxis, map.RotatingLayers, stateLimit, token)
                : Invalid(error);
        }
        public static PuzzleSolver.SolveResult Verify(MapDocument map, IReadOnlyList<Vector3Int> inputs)
        {
            if (map == null)
                return Invalid("맵 데이터가 필요합니다.");
            var error = map.Decode(out var tiles, out var links);
            if (error != null || inputs == null)
                return Invalid(error ?? "검증할 입력이 필요합니다.");
            return PuzzleSolver.VerifySequence(tiles, links, inputs, map.RotationAxis, map.RotatingLayers);
        }
        private static PuzzleSolver.SolveResult Invalid(string message) =>
            new(PuzzleSolver.SolveStatus.InvalidMap, message);
    }
}
