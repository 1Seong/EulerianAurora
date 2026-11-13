using System;
using System.Collections.Generic;
using System.Linq;

namespace EulerianAurora.Maps
{
    // 자연어에서 추출한 조건이다. UI 설정과 분리하며 null은 미지정을 뜻한다.
    // AllowedGimmicks = None은 일반 길만 허용한다는 명시적인 설정이다.
    public sealed class GenerationConditions
    {
        public MapDifficulty? Difficulty { get; set; }
        public MapGimmick? AllowedGimmicks { get; set; }
        public MapGimmick[] RequiredGimmicks { get; set; } = Array.Empty<MapGimmick>();
        public MapGimmick[] RequiredOrder { get; set; } = Array.Empty<MapGimmick>();
        public MapGimmickCount[] Counts { get; set; } = Array.Empty<MapGimmickCount>();
    }

    // UI는 허용 여부와 난이도만 다룬다. 자동일 때 수동 선택값은 보관만 한다.
    public sealed class GenerationUiSettings
    {
        public bool AutomaticGimmicks { get; set; } = true;
        public MapGimmick AllowedGimmicks { get; set; } = MapGimmick.All;
        public bool AutomaticDifficulty { get; set; } = true;
        public MapDifficulty Difficulty { get; set; } = MapDifficulty.Medium;
    }

    public sealed class GenerationPlan
    {
        public bool AutomaticDifficulty { get; }
        public MapDifficulty Difficulty { get; }
        public MapGimmick AllowedGimmicks { get; }
        public MapGimmick RequiredGimmicks { get; }
        public IReadOnlyList<MapGimmick> RequiredOrder { get; }
        public IReadOnlyDictionary<MapGimmick, int> Counts { get; }
        public GenerationBudgetPolicy Budget { get; }
        public int ReservedBudget { get; }
        public int OptionalBudget => Budget.Maximum - ReservedBudget;
        internal GenerationPlan(MapDifficulty difficulty, MapGimmick allowed, MapGimmick required,
            MapGimmick[] order, Dictionary<MapGimmick, int> counts, GenerationBudgetPolicy budget, int reserved, bool automaticDifficulty)
        {
            AutomaticDifficulty = automaticDifficulty;
            Difficulty = difficulty;
            AllowedGimmicks = allowed;
            RequiredGimmicks = required;
            RequiredOrder = Array.AsReadOnly((MapGimmick[])order.Clone());
            Counts = new System.Collections.ObjectModel.ReadOnlyDictionary<MapGimmick, int>(
                new Dictionary<MapGimmick, int>(counts));
            Budget = budget;
            ReservedBudget = reserved;
        }
    }

    public static class GenerationConditionResolver
    {
        // 발견한 충돌을 모두 반환한다. 한쪽 조건으로 다른 쪽 조건을 임의로 덮어쓰지 않는다.
        public static bool TryResolve(GenerationUiSettings settings, GenerationConditions text,
            out GenerationPlan plan, out string[] conflicts, GenerationBudgetPolicy policy = null, int seed = 0)
        {
            plan = null;
            var errors = new List<string>();
            settings ??= new GenerationUiSettings();
            var ui = new GenerationConditions
            {
                AllowedGimmicks = settings.AutomaticGimmicks ? null : settings.AllowedGimmicks,
                Difficulty = settings.AutomaticDifficulty ? null : settings.Difficulty
            };
            text ??= new GenerationConditions();
            Validate(ui, "UI");
            Validate(text, "자연어");
            if (errors.Count != 0) { conflicts = errors.ToArray(); return false; }

            if (ui.Difficulty.HasValue && text.Difficulty.HasValue && ui.Difficulty != text.Difficulty)
                errors.Add("UI와 자연어의 난이도가 다릅니다.");
            var requestedDifficulty = ui.Difficulty ?? text.Difficulty;
            var allowed = (ui.AllowedGimmicks ?? MapGimmick.All) & (text.AllowedGimmicks ?? MapGimmick.All);
            var order = text.RequiredOrder;
            var required = MapGimmick.None;
            foreach (var kind in text.RequiredGimmicks.Concat(text.RequiredOrder)) required |= kind;
            if ((required & ~allowed) != 0)
                errors.Add($"허용하지 않은 기믹이 필수 조건에 있습니다: {required & ~allowed}");
            var counts = new Dictionary<MapGimmick, int>();
            foreach (var entry in text.Counts)
            {
                counts[entry.Kind] = entry.Count;
                if (entry.Count > 0 && (allowed & entry.Kind) == 0)
                    errors.Add($"허용하지 않은 {entry.Kind}의 배치가 요청되었습니다.");
                if (entry.Count == 0 && (required & entry.Kind) != 0)
                    errors.Add($"필수 기믹 {entry.Kind}의 개수가 0입니다.");
            }
            // 자동 난이도는 보통으로 고정하지 않는다. 최소 배치 비용을 감당하는 단계만 추첨한다.
            var choices = new List<MapDifficulty>();
            foreach (var level in new[] { MapDifficulty.Easy, MapDifficulty.Medium, MapDifficulty.Hard })
            {
                if (requestedDifficulty.HasValue && requestedDifficulty.Value != level) continue;
                var candidateBudget = policy ?? GenerationBudgetPolicy.ForDifficulty(level);
                if (Reserve(candidateBudget) <= candidateBudget.Maximum) choices.Add(level);
            }
            if (choices.Count == 0) errors.Add("요청 기믹의 최소 비용이 선택 가능한 난이도의 예산을 초과합니다.");
            var difficulty = choices.Count > 0 ? choices[new Random(seed).Next(choices.Count)] : MapDifficulty.Medium;
            var budget = policy ?? GenerationBudgetPolicy.ForDifficulty(difficulty);
            long reserved = Reserve(budget);
            conflicts = errors.Distinct().ToArray();
            if (conflicts.Length != 0) return false;
            plan = new GenerationPlan(difficulty, allowed, required, order, counts, budget, (int)reserved, !requestedDifficulty.HasValue);
            return true;

            long Reserve(GenerationBudgetPolicy candidateBudget)
            {
                long total = 0;
                foreach (var rule in candidateBudget.Rules)
                {
                    if (rule.Kind == MapGimmick.None) continue;
                    int minimum = counts.TryGetValue(rule.Kind, out int count) ? count : (required & rule.Kind) != 0 ? 1 : 0;
                    total += (long)minimum * rule.Cost;
                }
                return total;
            }

            void Validate(GenerationConditions source, string label)
            {
                if (source.Difficulty.HasValue && source.Difficulty is not
                    (MapDifficulty.Easy or MapDifficulty.Medium or MapDifficulty.Hard))
                    errors.Add($"{label}: 난이도 값이 올바르지 않습니다. 미지정은 null을 사용하세요.");
                if (source.AllowedGimmicks.HasValue && (source.AllowedGimmicks.Value & ~MapGimmick.All) != 0)
                    errors.Add($"{label}: 허용 기믹 값이 올바르지 않습니다.");
                if (source.RequiredGimmicks == null || source.RequiredOrder == null || source.Counts == null)
                { errors.Add($"{label}: 조건 배열은 null일 수 없습니다."); return; }
                if (source.RequiredGimmicks.Concat(source.RequiredOrder).Any(kind => !GenerationBudgetPolicy.IsSingleKind(kind)))
                    errors.Add($"{label}: 필수 기믹 항목은 각각 하나의 기믹이어야 합니다.");
                var seen = new HashSet<MapGimmick>();
                foreach (var entry in source.Counts)
                    if (entry == null || !GenerationBudgetPolicy.IsSingleKind(entry.Kind) || entry.Count < 0 || !seen.Add(entry.Kind))
                        errors.Add($"{label}: 기믹 개수는 종류마다 0 이상의 정수로 한 번씩 지정하세요.");
            }
        }
    }
}
