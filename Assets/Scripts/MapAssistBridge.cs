using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace EulerianAurora.Maps
{
    // 유지한 UI와 연결하는 경계다. 새 생성기가 준비되지 않아도 기존 생성기를 대신 호출하지 않는다.
    public enum MapGenerationStatus { CandidateFound, UnsupportedConditions, GeneratorNotReady }
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
        internal MapCandidate(MapDocument map, PuzzleSolver.SolveResult witness) =>
            (Map, Witness) = (map, witness);
    }
    public static class MapTools
    {
        public const string RebuildMessage = "맵 생성기를 재구축 중입니다. 현재 후보 생성은 사용할 수 없습니다.";
        public static MapGenerationResult Generate(MapDocument map, MapAssistRequest request, int seed,
            CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (request == null || request.RequiredOrder == null)
                return new MapGenerationResult(MapGenerationStatus.UnsupportedConditions, null, "요청 조건이 올바르지 않습니다.");
            var order = new List<MapGimmick>();
            foreach (var step in request.RequiredOrder)
            {
                if (step == MapAssistEvent.PortalExit) order.Add(MapGimmick.Portal);
                else if (step == MapAssistEvent.InverterActivated) order.Add(MapGimmick.Inverter);
                else return new MapGenerationResult(MapGenerationStatus.UnsupportedConditions, null, "지원하지 않는 기믹 순서입니다.");
            }
            var extracted = new GenerationConditions
            {
                Difficulty = request.Difficulty == MapDifficulty.Any ? null : request.Difficulty,
                RequiredGimmicks = request.RequiredGimmicks,
                RequiredOrder = order.ToArray(),
                Counts = request.GimmickCounts
            };
            if (!GenerationConditionResolver.TryResolve(request.UiConditions, extracted, out var plan, out var conflicts, seed: seed))
                return new MapGenerationResult(MapGenerationStatus.UnsupportedConditions, null, string.Join("\n", conflicts));
            return new MapGenerationResult(MapGenerationStatus.GeneratorNotReady, null, RebuildMessage, plan);
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
