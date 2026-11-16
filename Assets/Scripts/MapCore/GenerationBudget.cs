using System;
using System.Collections.Generic;

namespace EulerianAurora.Maps
{
    public sealed class GimmickBudgetRule
    {
        // 여기서 None은 알 수 없는 기믹이 아니라 일반 길 생성을 뜻한다.
        public MapGimmick Kind { get; }
        public int Cost { get; }
        public int Weight { get; }
        // 한 종류의 비용과 상대 가중치를 검증해 보관한다.
        public GimmickBudgetRule(MapGimmick kind, int cost, int weight)
        {
            if ((kind != MapGimmick.None && !GenerationBudgetPolicy.IsSingleKind(kind)) || cost < 1 || weight < 1)
                throw new ArgumentException("기믹 비용과 가중치는 양수여야 합니다.");
            (Kind, Cost, Weight) = (kind, cost, weight);
        }
    }

    public sealed class GenerationBudgetPolicy
    {
        public int Maximum { get; }
        public IReadOnlyList<GimmickBudgetRule> Rules { get; }
        // 호출자가 나중에 배열을 바꿔도 정책에 영향이 없도록 규칙을 복사한다.
        public GenerationBudgetPolicy(int maximum, params GimmickBudgetRule[] rules)
        {
            if (maximum < 1 || rules == null || rules.Length != 7)
                throw new ArgumentException("양수 예산과 길 및 여섯 기믹의 정책이 필요합니다.");
            var kinds = new HashSet<MapGimmick>();
            foreach (var rule in rules)
                if (rule == null || !kinds.Add(rule.Kind))
                    throw new ArgumentException("중복되거나 비어 있는 기믹 정책입니다.");
            Maximum = maximum;
            Rules = Array.AsReadOnly((GimmickBudgetRule[])rules.Clone());
        }
        // 등록된 한 종류의 비용을 반환한다.
        public int Cost(MapGimmick kind)
        {
            for (int i = 0; i < Rules.Count; i++)
            {
                var rule = Rules[i];
                if (rule.Kind == kind) return rule.Cost;
            }
            throw new ArgumentOutOfRangeException(nameof(kind));
        }
        // 여러 플래그가 섞이지 않은 기믹인지 검사한다.
        public static bool IsSingleKind(MapGimmick kind) =>
            kind != MapGimmick.None && (kind & ~MapGimmick.All) == 0 &&
            (((int)kind & ((int)kind - 1)) == 0);

        // 조정용 초기값이며 실제 플레이 난이도를 측정한 값은 아니다. 비용과 선택 확률은 별도로 설정한다.
        public static GenerationBudgetPolicy ForDifficulty(MapDifficulty difficulty)
        {
            var defaults = MapGenerationOptions.Default;
            return defaults.For(difficulty).CreateBudget(defaults.Costs);
        }

        // 가중 추첨 전에 남은 예산으로 선택할 수 있는 후보만 남긴다.
        // 호출자는 현재 상태에서 실행 가능한 종류도 전달해야 한다. 예산이 충분해도 실행 가능하다는 뜻은 아니다.
        public bool TryPick(Random random, MapGimmick allowed, int remaining,
            IReadOnlyCollection<MapGimmick> feasible, out MapGimmick kind)
        {
            if (random == null || feasible == null) throw new ArgumentNullException();
            if (remaining < 0 || (allowed & ~MapGimmick.All) != 0) throw new ArgumentOutOfRangeException();
            bool roadFeasible = false;
            MapGimmick gimmicks = MapGimmick.None;
            foreach (var candidate in feasible)
            {
                if (candidate == MapGimmick.None) roadFeasible = true;
                else if (IsSingleKind(candidate)) gimmicks |= candidate;
            }
            return TryPick(random, allowed, remaining, roadFeasible, gimmicks, out kind);
        }

        // 생성기 내부에서는 이미 정리된 종류 플래그로 추첨해 임시 컬렉션을 만들지 않는다.
        internal bool TryPick(Random random, MapGimmick allowed, int remaining, bool roadFeasible,
            MapGimmick gimmicks, out MapGimmick kind)
        {
            var eligibleGimmicks = gimmicks & allowed;
            long total = 0;
            for (int i = 0; i < Rules.Count; i++)
            {
                var rule = Rules[i];
                if (Eligible(rule)) total += rule.Weight;
            }
            kind = MapGimmick.None;
            if (total == 0) return false;
            double draw = random.NextDouble() * total;
            for (int i = 0; i < Rules.Count; i++)
            {
                var rule = Rules[i];
                if (!Eligible(rule)) continue;
                draw -= rule.Weight;
                if (draw < 0) { kind = rule.Kind; return true; }
            }
            return false;
            bool Eligible(GimmickBudgetRule rule) => rule.Cost <= remaining &&
                (rule.Kind == MapGimmick.None ? roadFeasible : (eligibleGimmicks & rule.Kind) != 0);
        }
    }
}
