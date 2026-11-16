using System;

namespace EulerianAurora.Maps
{
    [Serializable]
    public struct MapGenerationOptions
    {
        public GenerationGimmickValues Costs;
        public GenerationDifficultyOptions Easy;
        public GenerationDifficultyOptions Medium;
        public GenerationDifficultyOptions Hard;
        public GenerationSearchOptions Search;
        public GenerationSamplingOptions Sampling;

        internal const int MinimumCubeSize = 2;
        internal const int MaximumCubeSize = 32;
        internal const int MaximumValue = 1_000_000;
        // 한 배치가 재귀 탐색의 한 프레임을 점유한다. 사용자 설정으로 스택 한도를 넘지 않게 한다.
        internal const int MaximumInputs = 512;

        public static MapGenerationOptions Default => new()
        {
            Costs = new(1, 2, 3, 4, 5, 8, 10),
            Easy = GenerationDifficultyOptions.Create(24, 1, 1, 2, 10, 0),
            Medium = GenerationDifficultyOptions.Create(45, 3, 1, 4, 20, 25),
            Hard = GenerationDifficultyOptions.Create(80, 6, 2, 6, 35, 50, startInsideRotationGroup: true),
            Search = new() { PlacementAttempts = 1500, Restarts = 8, Milliseconds = 2500, InputLimit = MaximumInputs,
                PortalExitSamplesPerEntrance = 8, GhostPathsPerEntrance = 8, GhostStatesPerEntrance = 128 },
            Sampling = new() { MinimumSize = MinimumCubeSize, MaximumSize = MaximumCubeSize, DefaultSize = 10,
                MinimumLaserRayLength = 1, MaximumLaserRayLength = 3, PreferredInverterRayLength = 2,
                MaximumConsecutiveTurns = 3, OneTurnWeight = 1, TwoTurnWeight = 1, ThreeTurnWeight = 1,
                XAxisWeight = 1, YAxisWeight = 1, ZAxisWeight = 1, SecondRotationGroupLength = 2, RotationGroupGap = 1 }
        };

        internal GenerationDifficultyOptions For(MapDifficulty difficulty) => difficulty switch
        {
            MapDifficulty.Easy => Easy,
            MapDifficulty.Medium => Medium,
            MapDifficulty.Hard => Hard,
            _ => throw new ArgumentOutOfRangeException(nameof(difficulty))
        };

        public string ValidationError()
        {
            if (!Costs.Valid()) return "기믹 비용은 1~1,000,000이어야 합니다.";
            foreach (var difficulty in new[] { MapDifficulty.Easy, MapDifficulty.Medium, MapDifficulty.Hard })
                if (!For(difficulty).Valid())
                    return $"{difficulty} 설정의 예산, 가중치, 레이어 범위 또는 확률을 확인하세요.";
            if (Search.PlacementAttempts < 1 || Search.Restarts < 1 || Search.Milliseconds < 1 ||
                Search.InputLimit < 1 || Search.InputLimit > MaximumInputs || Search.PortalExitSamplesPerEntrance < 1 ||
                Search.GhostPathsPerEntrance < 1 || Search.GhostStatesPerEntrance < 1)
                return $"탐색 한도는 양수여야 하며 입력 한도의 최댓값은 {MaximumInputs}입니다.";
            if (Sampling.MinimumSize < MinimumCubeSize || Sampling.MaximumSize > MaximumCubeSize ||
                Sampling.DefaultSize < Sampling.MinimumSize || Sampling.DefaultSize > Sampling.MaximumSize)
                return "보드 크기는 2~32 범위 내에서 최소 ≤ 기본 ≤ 최대여야 합니다.";
            if (Sampling.MinimumLaserRayLength < 1 || Sampling.MaximumLaserRayLength >= MaximumCubeSize ||
                Sampling.MinimumLaserRayLength > Sampling.MaximumLaserRayLength ||
                Sampling.PreferredInverterRayLength < 1 || Sampling.PreferredInverterRayLength >= MaximumCubeSize ||
                Sampling.MaximumConsecutiveTurns is < 1 or > 3 ||
                !Positive(Sampling.OneTurnWeight) || !Positive(Sampling.TwoTurnWeight) || !Positive(Sampling.ThreeTurnWeight) ||
                !Positive(Sampling.XAxisWeight) || !Positive(Sampling.YAxisWeight) || !Positive(Sampling.ZAxisWeight) ||
                Sampling.SecondRotationGroupLength is < 1 or >= MaximumCubeSize ||
                Sampling.RotationGroupGap is < 1 or >= MaximumCubeSize)
                return "레이저 사거리, 연속 회전 한도, 회전 묶음 간격 및 가중치를 확인하세요.";
            return null;
        }

        internal static bool Positive(int value) => value is > 0 and <= MaximumValue;
    }

    [Serializable]
    public struct GenerationGimmickValues
    {
        public int Road, Dash, Portal, Laser, Ghost, Rotation, Inverter;

        internal GenerationGimmickValues(int road, int dash, int portal, int laser, int ghost, int rotation, int inverter) =>
            (Road, Dash, Portal, Laser, Ghost, Rotation, Inverter) = (road, dash, portal, laser, ghost, rotation, inverter);

        internal bool Valid() => MapGenerationOptions.Positive(Road) && MapGenerationOptions.Positive(Dash) &&
            MapGenerationOptions.Positive(Portal) && MapGenerationOptions.Positive(Laser) &&
            MapGenerationOptions.Positive(Ghost) && MapGenerationOptions.Positive(Rotation) && MapGenerationOptions.Positive(Inverter);
    }

    [Serializable]
    public struct GenerationDifficultyOptions
    {
        public int MaximumBudget;
        public int AutomaticDifficultyWeight;
        public GenerationGimmickValues PlacementWeights;
        public int TargetBudgetMinPercent, TargetBudgetMaxPercent, SurfaceFillMaxPercent;
        public int RotationGroupMinLength, RotationGroupMaxLength;
        public int OptionalRotationPercent, SecondRotationGroupPercent;
        public bool StartInsideRotationGroup;

        internal static GenerationDifficultyOptions Create(int budget, int heavyWeight, int minLayers, int maxLayers,
            int optionalPercent, int secondGroupPercent, bool startInsideRotationGroup = false) => new()
        {
            MaximumBudget = budget, AutomaticDifficultyWeight = 1,
            PlacementWeights = new(12, 5, 5, 3, heavyWeight, heavyWeight, heavyWeight),
            TargetBudgetMinPercent = 50, TargetBudgetMaxPercent = 100, SurfaceFillMaxPercent = 75,
            RotationGroupMinLength = minLayers, RotationGroupMaxLength = maxLayers,
            OptionalRotationPercent = optionalPercent, SecondRotationGroupPercent = secondGroupPercent,
            StartInsideRotationGroup = startInsideRotationGroup
        };

        internal GenerationBudgetPolicy CreateBudget(GenerationGimmickValues cost)
        {
            var weight = PlacementWeights;
            return new GenerationBudgetPolicy(MaximumBudget,
                new(MapGimmick.None, cost.Road, weight.Road), new(MapGimmick.Dash, cost.Dash, weight.Dash),
                new(MapGimmick.Portal, cost.Portal, weight.Portal), new(MapGimmick.Laser, cost.Laser, weight.Laser),
                new(MapGimmick.Ghost, cost.Ghost, weight.Ghost), new(MapGimmick.Rotation, cost.Rotation, weight.Rotation),
                new(MapGimmick.Inverter, cost.Inverter, weight.Inverter));
        }

        internal bool Valid() => MapGenerationOptions.Positive(MaximumBudget) &&
            AutomaticDifficultyWeight is >= 0 and <= MapGenerationOptions.MaximumValue && PlacementWeights.Valid() &&
            TargetBudgetMinPercent >= 0 && TargetBudgetMinPercent <= TargetBudgetMaxPercent &&
            TargetBudgetMaxPercent is >= 1 and <= 100 && SurfaceFillMaxPercent is >= 1 and <= 100 &&
            RotationGroupMinLength >= 1 && RotationGroupMinLength <= RotationGroupMaxLength &&
            RotationGroupMaxLength <= MapGenerationOptions.MaximumCubeSize - 2 &&
            OptionalRotationPercent is >= 0 and <= 100 && SecondRotationGroupPercent is >= 0 and <= 100;
    }

    [Serializable]
    public struct GenerationSearchOptions
    {
        public int PlacementAttempts, Restarts, Milliseconds, InputLimit;
        public int PortalExitSamplesPerEntrance, GhostPathsPerEntrance, GhostStatesPerEntrance;
    }

    [Serializable]
    public struct GenerationSamplingOptions
    {
        public int MinimumSize, MaximumSize, DefaultSize;
        public int MinimumLaserRayLength, MaximumLaserRayLength, PreferredInverterRayLength;
        public int MaximumConsecutiveTurns;
        public int OneTurnWeight, TwoTurnWeight, ThreeTurnWeight;
        public int XAxisWeight, YAxisWeight, ZAxisWeight;
        public int SecondRotationGroupLength, RotationGroupGap;
    }
}
