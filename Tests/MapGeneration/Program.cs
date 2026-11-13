using EulerianAurora.Maps;
int checks = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
bool Resolve(GenerationUiSettings ui, GenerationConditions text) => GenerationConditionResolver.TryResolve(ui,text,out _,out _);
var manual = new GenerationUiSettings { AutomaticGimmicks=false, AllowedGimmicks=MapGimmick.Dash, AutomaticDifficulty=false, Difficulty=MapDifficulty.Easy };
Check(!Resolve(manual,new() { Difficulty=MapDifficulty.Hard }),"difficulty conflict");
Check(!Resolve(manual,new() { RequiredOrder=new[]{MapGimmick.Portal} }),"forbidden sequence");
Check(!Resolve(manual,new() { Counts=new[]{new MapGimmickCount { Kind=MapGimmick.Portal, Count=1 }} }),"forbidden count");
Check(Resolve(new(){AutomaticGimmicks=false,AllowedGimmicks=MapGimmick.None},null),"roads only");
Check(!Resolve(null,new(){RequiredGimmicks=new[]{MapGimmick.Ghost},Counts=new[]{new MapGimmickCount { Kind=MapGimmick.Ghost, Count=0 }}}),"required zero");
Check(!Resolve(null,new(){Counts=new[]{new MapGimmickCount {Kind=MapGimmick.Inverter,Count=int.MaxValue}}}),"overflow budget");
Check(!Resolve(null,new(){RequiredGimmicks=new[]{MapGimmick.All}}),"composite kind");
Check(!Resolve(null,new(){Counts=null}),"null array");
Check(!Resolve(null,new(){Difficulty=MapDifficulty.Any}),"invalid difficulty");
Check(!Resolve(new(){AutomaticGimmicks=false,AllowedGimmicks=(MapGimmick)128},null),"invalid active setting");
var ignored=new GenerationUiSettings {AutomaticGimmicks=true,AllowedGimmicks=(MapGimmick)128,AutomaticDifficulty=true,Difficulty=(MapDifficulty)99};
Check(GenerationConditionResolver.TryResolve(ignored,new(){Difficulty=MapDifficulty.Hard,RequiredGimmicks=new[]{MapGimmick.Portal}},out var plan,out _),"ignore inactive values");
Check(plan.Difficulty==MapDifficulty.Hard && !plan.AutomaticDifficulty && plan.AllowedGimmicks==MapGimmick.All,"text overrides automatic only");
var levels=new HashSet<MapDifficulty>();
for(int seed=0;seed<40;seed++) {
    Check(GenerationConditionResolver.TryResolve(null,null,out plan,out _,seed:seed),"automatic resolve");
    levels.Add(plan.Difficulty);
    Check(plan.AutomaticDifficulty,"track automatic selection");
    GenerationConditionResolver.TryResolve(null,null,out var repeat,out _,seed:seed);
    Check(repeat.Difficulty==plan.Difficulty,"seed repeatability");
    var costly=new GenerationConditions {Counts=new[]{new MapGimmickCount{Kind=MapGimmick.Inverter,Count=5}}};
    Check(GenerationConditionResolver.TryResolve(null,costly,out var costlyPlan,out _,seed:seed) && costlyPlan.Difficulty==MapDifficulty.Hard,"automatic excludes insufficient pools");
}
Check(levels.Count==3,"automatic covers all three difficulties");
var order=new[]{MapGimmick.Portal,MapGimmick.Inverter,MapGimmick.Portal};
Check(GenerationConditionResolver.TryResolve(null,new(){Difficulty=MapDifficulty.Medium,RequiredOrder=order},out plan,out _),"order accepted");
Check(plan.ReservedBudget==13 && plan.OptionalBudget==32,"placement reservation");
order[0]=MapGimmick.Dash;
Check(plan.RequiredOrder[0]==MapGimmick.Portal,"immutable snapshot");
var easy=GenerationBudgetPolicy.ForDifficulty(MapDifficulty.Easy);
var hard=GenerationBudgetPolicy.ForDifficulty(MapDifficulty.Hard);
var all=new[]{MapGimmick.None,MapGimmick.Dash,MapGimmick.Portal,MapGimmick.Laser,MapGimmick.Ghost,MapGimmick.Rotation,MapGimmick.Inverter};
var rng=new Random(21);
Check(!hard.TryPick(rng,MapGimmick.All,0,all,out _),"exhausted");
for(int i=0;i<100;i++) {
    Check(hard.TryPick(rng,MapGimmick.None,80,all,out var kind)&&kind==MapGimmick.None,"road selection");
    Check(hard.TryPick(rng,MapGimmick.All,1,all,out kind)&&kind==MapGimmick.None,"affordable selection");
}
Console.WriteLine($"PASS {checks} checks");
