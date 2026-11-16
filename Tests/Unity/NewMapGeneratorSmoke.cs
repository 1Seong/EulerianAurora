#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using EulerianAurora.Maps;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

// 격리된 Unity 프로젝트의 Assets/Editor에서 실행한다. 실행 방법은 같은 폴더의 README를 참조한다.
public static class NewMapGeneratorSmoke
{
    private static int _checks;
    private static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception(name);
        _checks++;
    }
    private static MapAssistRequest Request(int size, MapDifficulty difficulty, int? portals = null) => new()
    {
        Size = size, Difficulty = difficulty, Operation = MapAssistOperation.CreateMap,
        UiConditions = new GenerationUiSettings { AutomaticGimmicks = false, AllowedGimmicks = (portals == 0 ? MapGimmick.None : MapGimmick.Portal) | (size == 2 && portals >= 2 ? MapGimmick.None : MapGimmick.Dash) },
        GimmickCounts = portals.HasValue ? new[] { new MapGimmickCount { Kind = MapGimmick.Portal, Count = portals.Value } } : Array.Empty<MapGimmickCount>()
    };
    public static void Run()
    {
        try
        {
            var total = Stopwatch.StartNew();
            // 수동 UI는 선택한 기믹 모두의 실제 사용을 요구한다. 배열로 필수 조건을 따로 주지 않는다.
            var supported = MapGimmick.Portal | MapGimmick.Dash | MapGimmick.Laser | MapGimmick.Ghost;
            foreach (var selection in new[] { MapGimmick.None, MapGimmick.Portal, MapGimmick.Dash, MapGimmick.Laser, MapGimmick.Ghost, supported })
            for (int seed = 0; seed < 6; seed++)
            {
                var selected = Request(10, MapDifficulty.Easy);
                selected.UiConditions.AllowedGimmicks = selection;
                var result = MapTools.Generate(null, selected, seed);
                Check(result.Candidate != null, "manual use selection: " + result.Message);
                Check(result.Plan.RequiredGimmicks == selection, "selected mask becomes required");
                Check(result.Candidate.Witness.UsedGimmicks == selection, "every selected kind used, unselected excluded");
                Check(MapTools.Verify(result.Candidate.Map, result.Candidate.Witness.SolutionInputs).IsSolvable, "manual selection witness");
                if (selection == supported) Check(result.Plan.ReservedBudget == 19, "manual connector budget");
            }
            var selectedZero = Request(10, MapDifficulty.Easy);
            selectedZero.UiConditions.AllowedGimmicks = MapGimmick.Portal;
            selectedZero.GimmickCounts = new[] { new MapGimmickCount { Kind = MapGimmick.Portal, Count = 0 } };
            Check(MapTools.Generate(null, selectedZero, 1).Status == MapGenerationStatus.UnsupportedConditions, "UI selected versus zero count");
            selectedZero.UiConditions.AutomaticGimmicks = true;
            Check(MapTools.Generate(null, selectedZero, 1).Candidate != null, "automatic ignores stored selected kind");
            var unsupportedSelection = Request(10, MapDifficulty.Hard);
            unsupportedSelection.UiConditions.AllowedGimmicks = MapGimmick.Rotation;
            Check(MapTools.Generate(null, unsupportedSelection, 1).Status == MapGenerationStatus.UnsupportedConditions, "unsupported selection not silently dropped");
            long maxMs = 0;
            int generated = 0, backtracks = 0;
            var boards = new HashSet<string>();
            foreach (int size in new[] { 2, 3, 10, 32 })
            foreach (var difficulty in new[] { MapDifficulty.Easy, MapDifficulty.Medium, MapDifficulty.Hard })
            for (int seed = 0; seed < 8; seed++)
            {
                int count = seed % 3;
                var request = Request(size, difficulty, count);
                var watch = Stopwatch.StartNew();
                var result = MapTools.Generate(null, request, seed);
                maxMs = Math.Max(maxMs, watch.ElapsedMilliseconds);
                Check(result.Candidate != null, $"generate size={size} difficulty={difficulty} seed={seed}: {result.Message}");
                var candidate = result.Candidate;
                Check(candidate.GenerationCost <= result.Plan.Budget.Maximum, "budget cap");
                Check(candidate.Map.PortalPairs.Length == count, "exact portal count");
                Check(MapTools.Verify(candidate.Map, candidate.Witness.SolutionInputs).IsSolvable, "final replay");
                Check(candidate.Map.Tiles.Count(c => c == (char)TileType.Player) == 1, "one start");
                foreach (var pair in candidate.Map.PortalPairs)
                {
                    var delta = pair.Exit - pair.Entrance;
                    Check(Math.Abs(delta.x) + Math.Abs(delta.y) + Math.Abs(delta.z) > 1, "nonadjacent exit");
                }
                Check(MapTools.Solve(candidate.Map, 10000).IsSolvable, "independent solver");
                boards.Add(candidate.Map.Tiles);
                generated++;
                backtracks += candidate.Backtracks;
            }
            Check(boards.Count > 40, "seed diversity: " + boards.Count);
            // 조건을 고정하고 시드만 바꿔도 서로 다른 초기 맵이 생성되는지 확인한다.
            var varied = new HashSet<string>();
            for (int seed = 0; seed < 10; seed++) varied.Add(MapTools.Generate(null, Request(10, MapDifficulty.Medium, 2), seed).Candidate.Map.Tiles);
            Check(varied.Count >= 8, "same conditions diverse seeds");
            var sample = Request(10, MapDifficulty.Medium, 3);
            sample.RequiredOrder = new[] { MapAssistEvent.PortalExit, MapAssistEvent.PortalExit };
            var first = MapTools.Generate(null, sample, 31);
            var second = MapTools.Generate(null, sample, 31);
            Check(first.Candidate != null && second.Candidate != null, "ordered portal generation");
            Check(first.Candidate.Map.Tiles == second.Candidate.Map.Tiles &&
                first.Candidate.Witness.SolutionInputs.SequenceEqual(second.Candidate.Witness.SolutionInputs), "same seed and repeated candidate accepted");
            Check(first.Candidate.Map.PortalPairs.Length == 3, "ordered count");
            var source = first.Candidate.Map;
            var before = source.Tiles;
            MapTools.Generate(source, Request(10, MapDifficulty.Easy), 5);
            Check(source.Tiles == before, "source not mutated");

            CheckInverterGeneration();
            var dashDirections = new HashSet<char>();
            int dashMaps = 0;
            foreach (int size in new[] { 3, 10, 32 })
            foreach (var difficulty in new[] { MapDifficulty.Easy, MapDifficulty.Medium, MapDifficulty.Hard })
            for (int seed = 0; seed < 6; seed++)
            {
                var mixed = Request(size, difficulty);
                mixed.GimmickCounts = new[] {
                    new MapGimmickCount { Kind = MapGimmick.Portal, Count = seed % 2 },
                    new MapGimmickCount { Kind = MapGimmick.Dash, Count = 1 + seed % 3 }
                };
                mixed.UiConditions.AllowedGimmicks = MapGimmick.Dash | (seed % 2 == 0 ? MapGimmick.None : MapGimmick.Portal);
                mixed.RequiredGimmicks = new[] { MapGimmick.Dash };
                var result = MapTools.Generate(null, mixed, seed);
                Check(result.Candidate != null, $"dash mixed size={size} level={difficulty} seed={seed}: {result.Message}");
                var candidate = result.Candidate;
                Check(candidate.Map.Tiles.Count(TileHelper.IsDash) == 1 + seed % 3, "exact dash count");
                Check((candidate.Witness.UsedGimmicks & MapGimmick.Dash) != 0, "dash actually used");
                Check(candidate.GenerationCost <= result.Plan.Budget.Maximum, "dash budget");
                int actualCost = candidate.Map.Tiles.Count(c => c == (char)TileType.Road) * result.Plan.Budget.Cost(MapGimmick.None)
                    + candidate.Map.PortalPairs.Length * result.Plan.Budget.Cost(MapGimmick.Portal)
                    + candidate.Map.Tiles.Count(TileHelper.IsDash) * result.Plan.Budget.Cost(MapGimmick.Dash);
                Check(candidate.GenerationCost == actualCost, "rollback leaves exact final placement cost");
                Check(MapTools.Verify(candidate.Map, candidate.Witness.SolutionInputs).IsSolvable, "dash replay");
                foreach (char tile in candidate.Map.Tiles) if (TileHelper.IsDash(tile)) dashDirections.Add(tile);
                var simulation = new PuzzleSolver.Simulation(size);
                var pairs = candidate.Map.CopyPortalLinks();
                Check(simulation.Reset(candidate.Map.CopyTiles(), pairs), "dash simulation reset");
                int dashEvents = 0;
                foreach (var input in candidate.Witness.SolutionInputs)
                {
                    int oldEvents = simulation.Events.Count;
                    var previous = simulation.Position;
                    Check(simulation.TryStep(input), "dash simulation step");
                    if (simulation.Events.Skip(oldEvents).Contains(MapGimmick.Dash))
                    {
                        dashEvents++;
                        var delta = simulation.Position - previous;
                        Check(Math.Abs(delta.x) + Math.Abs(delta.y) + Math.Abs(delta.z) == 2, "one input performs entry and forced movement");
                    }
                }
                Check(dashEvents == 1 + seed % 3, "all placed dash tiles used");
                dashMaps++;
            }
            Check(dashDirections.Count == 6, "six dash orientations");
            foreach (var sequence in new[] {
                new[] {MapAssistEvent.DashUsed,MapAssistEvent.PortalExit,MapAssistEvent.DashUsed},
                new[] {MapAssistEvent.PortalExit,MapAssistEvent.DashUsed,MapAssistEvent.PortalExit}
            })
            {
                var ordered = Request(10, MapDifficulty.Hard);
                ordered.RequiredOrder = sequence;
                ordered.GimmickCounts = new[] {
                    new MapGimmickCount {Kind=MapGimmick.Portal,Count=sequence.Count(e=>e==MapAssistEvent.PortalExit)},
                    new MapGimmickCount {Kind=MapGimmick.Dash,Count=sequence.Count(e=>e==MapAssistEvent.DashUsed)}
                };
                for(int seed=0;seed<8;seed++)
                {
                    var result = MapTools.Generate(null,ordered,seed);
                    Check(result.Candidate!=null,"mixed required order: "+result.Message);
                    var simulation=new PuzzleSolver.Simulation(10);
                    simulation.Reset(result.Candidate.Map.CopyTiles(),result.Candidate.Map.CopyPortalLinks());
                    foreach(var input in result.Candidate.Witness.SolutionInputs) Check(simulation.TryStep(input),"ordered replay");
                    Check(simulation.Events.SequenceEqual(sequence.Select(e=>e==MapAssistEvent.DashUsed?MapGimmick.Dash:MapGimmick.Portal)),"exact mixed event order");
                }
            }
            var noDash=Request(10,MapDifficulty.Hard);
            noDash.UiConditions.AllowedGimmicks=MapGimmick.Portal;
            noDash.GimmickCounts=new[]{new MapGimmickCount{Kind=MapGimmick.Dash,Count=0}};
            Check(!MapTools.Generate(null,noDash,3).Candidate.Map.Tiles.Any(TileHelper.IsDash),"zero dash exclusion");
            noDash.RequiredGimmicks=new[]{MapGimmick.Dash};
            Check(MapTools.Generate(null,noDash,3).Status==MapGenerationStatus.UnsupportedConditions,"required zero dash conflict");
            var blockedDash=Request(10,MapDifficulty.Easy);
            blockedDash.RequiredGimmicks=new[]{MapGimmick.Dash};
            blockedDash.UiConditions=new GenerationUiSettings{AutomaticGimmicks=false,AllowedGimmicks=MapGimmick.Portal};
            Check(MapTools.Generate(null,blockedDash,1).Status==MapGenerationStatus.UnsupportedConditions,"UI forbids required dash");
            Debug.Log($"DASH_GENERATOR_PASS maps={dashMaps} orientations={dashDirections.Count}");            var firingDirections = new HashSet<Vector3Int>();
            var beamLengths = new HashSet<int>();
            int laserMaps = 0;
            foreach (int size in new[] { 3, 10, 32 })
            foreach (var difficulty in new[] { MapDifficulty.Easy, MapDifficulty.Medium, MapDifficulty.Hard })
            for (int seed = 0; seed < 6; seed++)
            {
                var request = Request(size, difficulty);
                request.UiConditions = new GenerationUiSettings { AutomaticGimmicks = false, AllowedGimmicks = MapGimmick.Portal | MapGimmick.Dash | MapGimmick.Laser };
                int amount = 1 + seed % 2;
                request.GimmickCounts = new[] {
                    new MapGimmickCount {Kind=MapGimmick.Laser,Count=amount},
                    new MapGimmickCount {Kind=MapGimmick.Portal,Count=seed%2},
                    new MapGimmickCount {Kind=MapGimmick.Dash,Count=seed%2}
                };
                request.UiConditions.AllowedGimmicks = MapGimmick.Laser | (seed % 2 == 0 ? MapGimmick.None : MapGimmick.Portal | MapGimmick.Dash);
                request.RequiredGimmicks = new[]{MapGimmick.Laser};
                var result = MapTools.Generate(null,request,seed);
                Check(result.Candidate != null,$"laser size={size} difficulty={difficulty} seed={seed}: {result.Message}");
                var candidate = result.Candidate;
                Check(candidate.Map.Tiles.Count(c=>c==(char)TileType.Laser)==amount,"laser count");
                Check((candidate.Witness.UsedGimmicks & MapGimmick.Laser)!=0,"actual laser firing");
                Check(MapTools.Verify(candidate.Map,candidate.Witness.SolutionInputs).IsSolvable,"laser final replay");
                int actualCost = candidate.Map.Tiles.Count(c=>c==(char)TileType.Road)
                    + candidate.Map.Tiles.Count(TileHelper.IsDash)*2 + candidate.Map.PortalPairs.Length*3 + amount*4;
                Check(candidate.GenerationCost==actualCost && actualCost<=result.Plan.Budget.Maximum,"laser exact budget and rollback");
                var sim=new PuzzleSolver.Simulation(size);
                sim.Reset(candidate.Map.CopyTiles(),candidate.Map.CopyPortalLinks());
                int fires=0;
                foreach(var input in candidate.Witness.SolutionInputs)
                {
                    int beforeEvents=sim.Events.Count, remaining=sim.Unpainted;
                    var position=sim.Position;
                    Check(sim.TryStep(input),"laser input replay");
                    if(sim.Events.Skip(beforeEvents).Contains(MapGimmick.Laser))
                    {
                        Check(sim.Position==position,"firing does not move player");
                        Check(remaining>sim.Unpainted,"firing paints targets");
                        beamLengths.Add(remaining-sim.Unpainted);
                        firingDirections.Add(input);
                        fires++;
                    }
                }
                Check(fires==amount,"all laser tiles fired");
                laserMaps++;
            }
            Check(firingDirections.Count==6,"six laser directions");
            Check(beamLengths.Contains(1) && beamLengths.Contains(2) && beamLengths.Contains(3),"varied beam length");
            MapGimmick EventKind(MapAssistEvent e) => e==MapAssistEvent.LaserFired?MapGimmick.Laser:e==MapAssistEvent.DashUsed?MapGimmick.Dash:MapGimmick.Portal;
            foreach(var sequence in new[]{
                new[]{MapAssistEvent.LaserFired,MapAssistEvent.PortalExit,MapAssistEvent.DashUsed},
                new[]{MapAssistEvent.PortalExit,MapAssistEvent.DashUsed,MapAssistEvent.LaserFired},
                new[]{MapAssistEvent.DashUsed,MapAssistEvent.LaserFired,MapAssistEvent.PortalExit}
            })
            for(int seed=0;seed<8;seed++)
            {
                var ordered=Request(10,MapDifficulty.Hard);
                ordered.UiConditions = new GenerationUiSettings { AutomaticGimmicks = false, AllowedGimmicks = MapGimmick.Portal | MapGimmick.Dash | MapGimmick.Laser };
                ordered.RequiredOrder=sequence;
                ordered.GimmickCounts=new[]{new MapGimmickCount{Kind=MapGimmick.Portal,Count=1},new MapGimmickCount{Kind=MapGimmick.Dash,Count=1},new MapGimmickCount{Kind=MapGimmick.Laser,Count=1}};
                var result=MapTools.Generate(null,ordered,seed);
                Check(result.Candidate!=null,"laser mixed order: "+result.Message);
                var sim=new PuzzleSolver.Simulation(10);
                sim.Reset(result.Candidate.Map.CopyTiles(),result.Candidate.Map.CopyPortalLinks());
                foreach(var input in result.Candidate.Witness.SolutionInputs) Check(sim.TryStep(input),"three gimmick replay");
                Check(sim.Events.SequenceEqual(sequence.Select(EventKind)),"laser mixed event order");
            }
            for(int seed=0;seed<6;seed++)
            {
                var many=Request(10,MapDifficulty.Hard); many.UiConditions = null;
                many.GimmickCounts=new[]{new MapGimmickCount{Kind=MapGimmick.Laser,Count=5}};
                var result=MapTools.Generate(null,many,seed);
                Check(result.Candidate!=null,"five laser generation: "+result.Message);
                Check(result.Candidate.Map.Tiles.Count(c=>c==(char)TileType.Laser)==5,"five exact laser count");
                Check(MapTools.Verify(result.Candidate.Map,result.Candidate.Witness.SolutionInputs).IsSolvable,"five laser replay");
            }            var zeroLaser=Request(10,MapDifficulty.Medium); zeroLaser.UiConditions = new GenerationUiSettings { AutomaticGimmicks = false, AllowedGimmicks = MapGimmick.Portal | MapGimmick.Dash | MapGimmick.Laser };
            zeroLaser.UiConditions.AllowedGimmicks = MapGimmick.Portal | MapGimmick.Dash;
            zeroLaser.GimmickCounts=new[]{new MapGimmickCount{Kind=MapGimmick.Laser,Count=0}};
            Check(!MapTools.Generate(null,zeroLaser,1).Candidate.Map.Tiles.Contains((char)TileType.Laser),"zero laser");
            var blockedLaser=Request(10,MapDifficulty.Easy);
            blockedLaser.RequiredGimmicks=new[]{MapGimmick.Laser};
            Check(MapTools.Generate(null,blockedLaser,1).Status==MapGenerationStatus.UnsupportedConditions,"UI laser forbidden");
            var expensive=Request(10,MapDifficulty.Easy); expensive.UiConditions = null;
            expensive.GimmickCounts=new[]{new MapGimmickCount{Kind=MapGimmick.Laser,Count=5}};
            Check(MapTools.Generate(null,expensive,1).Status==MapGenerationStatus.UnsupportedConditions,"laser targets and escape reserve");
            for(int seed=0;seed<6;seed++)
            {
                var automatic=Request(10,MapDifficulty.Any); automatic.UiConditions = null;
                automatic.GimmickCounts=new[]{new MapGimmickCount{Kind=MapGimmick.Laser,Count=5}};
                var result=MapTools.Generate(null,automatic,seed);
                Check(result.Candidate!=null,"automatic laser connector budget: "+result.Message);
                Check(result.Plan.AutomaticDifficulty && result.Plan.Difficulty!=MapDifficulty.Easy && result.Plan.ReservedBudget==30,"automatic avoids insufficient connector budget");
            }
            var repeated=Request(10,MapDifficulty.Any); repeated.UiConditions = null;
            repeated.RequiredOrder=Enumerable.Repeat(MapAssistEvent.LaserFired,5).ToArray();
            var repeatedResult=MapTools.Generate(null,repeated,2);
            Check(repeatedResult.Candidate!=null && repeatedResult.Plan.ReservedBudget==30,"repeated event construction reserve");            // 뒤에 더한 길이 앞선 발사 범위를 늘리면 기존 이동 입력이 실패하는 것을 확인한다.
            var changed=new char[5,5,5];
            for(int x=0;x<5;x++)for(int y=0;y<5;y++)for(int z=0;z<5;z++)changed[x,y,z]=(char)TileType.Empty;
            changed[0,0,0]=(char)TileType.Player; changed[1,0,0]=(char)TileType.Laser;
            foreach(var cell in new[]{new Vector3Int(2,0,0),new Vector3Int(1,1,0),new Vector3Int(2,1,0),new Vector3Int(3,1,0)}) changed[cell.x,cell.y,cell.z]=(char)TileType.Road;
            var prefix=new[]{Vector3Int.right,Vector3Int.right,Vector3Int.up,Vector3Int.right,Vector3Int.right};
            var checkSim=new PuzzleSolver.Simulation(5);
            checkSim.Reset(changed,null);
            foreach(var input in prefix)Check(checkSim.TryStep(input),"base beam prefix");
            Check(checkSim.Unpainted==0,"base beam completion");
            changed[3,0,0]=(char)TileType.Road; changed[4,1,0]=(char)TileType.Road;
            checkSim.Reset(changed,null);
            foreach(var input in prefix)Check(checkSim.TryStep(input),"extended beam prefix");
            Check(checkSim.Unpainted == 1,"unpainted road remains after extended beam");
            Check(!checkSim.TryStep(Vector3Int.down),"extension cannot revisit a laser-painted future road");
            Debug.Log($"LASER_GENERATOR_PASS maps={laserMaps} orientations={firingDirections.Count} beamLengths={string.Join(",",beamLengths)}");
            int ghostMaps = 0;
            foreach (int size in new[] { 3, 10, 32 })
            foreach (var difficulty in new[] { MapDifficulty.Easy, MapDifficulty.Medium, MapDifficulty.Hard })
            for (int seed = 0; seed < 6; seed++)
            {
                var request = Request(size, difficulty);
                request.UiConditions = null;
                int amount = 1 + seed % 2;
                request.GimmickCounts = new[] { new MapGimmickCount { Kind = MapGimmick.Ghost, Count = amount } };
                request.RequiredGimmicks = new[] { MapGimmick.Ghost };
                var result = MapTools.Generate(null, request, seed);
                Check(result.Candidate != null, $"ghost generation {size}/{difficulty}/{seed}: {result.Message}");
                var candidate = result.Candidate;
                Check(candidate.Map.Tiles.Count(c => c == (char)TileType.Ghost) == amount, "exact ghost count");
                Check(MapTools.Verify(candidate.Map, candidate.Witness.SolutionInputs).IsSolvable, "ghost final replay");
                Check((candidate.Witness.UsedGimmicks & MapGimmick.Ghost) != 0, "ghost actually used");
                int expectedCost = candidate.Map.Tiles.Count(c => c == (char)TileType.Road) +
                    candidate.Map.Tiles.Count(c => TileHelper.IsDash(c)) * 2 + candidate.Map.PortalPairs.Length * 3 +
                    candidate.Map.Tiles.Count(c => c == (char)TileType.Laser) * 4 + amount * 5 +
                    candidate.Map.Tiles.Count(c => c == (char)TileType.Inv) * 10;
                Check(candidate.GenerationCost == expectedCost, "ghost cost and reused cells");
                var sim = new PuzzleSolver.Simulation(size);
                Check(sim.Reset(candidate.Map.CopyTiles(), candidate.Map.CopyPortalLinks()), "ghost reset");
                int activeMoves = 0, revisits = 0;
                var visited = new HashSet<Vector3Int> { sim.Position };
                foreach (var input in candidate.Witness.SolutionInputs)
                {
                    int ghost = sim.GhostRemaining, unpainted = sim.Unpainted;
                    Check(sim.TryStep(input), "ghost step");
                    if (ghost > 0)
                    {
                        activeMoves++;
                        Check(sim.GhostRemaining == ghost - 1, "ghost countdown");
                        if (visited.Contains(sim.Position)) revisits++;
                        if (ghost > 1) Check(sim.Unpainted == unpainted, "ghost traversal does not paint");
                    }
                    visited.Add(sim.Position);
                }
                Check(activeMoves == amount * TileHelper.GhostCount && revisits >= amount, "bounded painted-cell backtracking");
                Check(sim.Events.Count(e => e == MapGimmick.Ghost) == amount, "one ghost event per activation");
                Check(sim.GhostRemaining == 0 && sim.Unpainted == 0, "ghost ended before clear");
                ghostMaps++;
            }
            foreach (var order in new[] {
                new[] { MapAssistEvent.GhostUsed, MapAssistEvent.PortalExit, MapAssistEvent.LaserFired, MapAssistEvent.DashUsed },
                new[] { MapAssistEvent.LaserFired, MapAssistEvent.GhostUsed, MapAssistEvent.DashUsed, MapAssistEvent.PortalExit },
                new[] { MapAssistEvent.GhostUsed, MapAssistEvent.GhostUsed }
            })
            for (int seed = 0; seed < 8; seed++)
            {
                var request = Request(10, MapDifficulty.Hard);
                request.UiConditions = null;
                request.RequiredOrder = order;
                MapGimmick Kind(MapAssistEvent e) => e == MapAssistEvent.GhostUsed ? MapGimmick.Ghost : EventKind(e);
                request.GimmickCounts = new[] { MapGimmick.Ghost, MapGimmick.Portal, MapGimmick.Laser, MapGimmick.Dash, MapGimmick.Inverter }
                    .Select(k => new MapGimmickCount { Kind = k, Count = order.Count(e => Kind(e) == k) }).ToArray();
                var result = MapTools.Generate(null, request, seed);
                Check(result.Candidate != null, "ghost ordered generation: " + result.Message);
                var sim = new PuzzleSolver.Simulation(10);
                sim.Reset(result.Candidate.Map.CopyTiles(), result.Candidate.Map.CopyPortalLinks());
                foreach (var input in result.Candidate.Witness.SolutionInputs) Check(sim.TryStep(input), "ghost mixed replay");
                Check(sim.Events.SequenceEqual(order.Select(Kind)), "ghost mixed event order");
            }
            var forbiddenGhost = Request(10, MapDifficulty.Easy);
            forbiddenGhost.RequiredGimmicks = new[] { MapGimmick.Ghost };
            Check(MapTools.Generate(null, forbiddenGhost, 0).Status == MapGenerationStatus.UnsupportedConditions, "UI ghost forbidden");
            var autoGhost = Request(10, MapDifficulty.Any);
            autoGhost.UiConditions = null;
            autoGhost.RequiredOrder = Enumerable.Repeat(MapAssistEvent.GhostUsed, 5).ToArray();
            var autoGhostResult = MapTools.Generate(null, autoGhost, 2);
            Check(autoGhostResult.Candidate != null && autoGhostResult.Plan.ReservedBudget == 30 &&
                autoGhostResult.Plan.Difficulty != MapDifficulty.Easy, "ghost connector budget reservation");
            Debug.Log($"GHOST_GENERATOR_PASS maps={ghostMaps} ordered=24");
            // 고스트 도중에는 아직 칠하지 않은 기믹도 발동하지 않고 통과한다.
            // 생성기의 기존 칸 되짚기 전략과 별개로 실제 전이 규칙의 경계도 검사한다.
            foreach (var tile in new[] { TileType.DashXp, TileType.Laser, TileType.Inv, TileType.Ghost, TileType.PortalIn })
            {
                var board = new char[8, 8, 8];
                for (int x = 0; x < 8; x++) for (int y = 0; y < 8; y++) for (int z = 0; z < 8; z++)
                    board[x, y, z] = (char)TileType.Empty;
                board[0, 0, 0] = (char)TileType.Player;
                board[1, 0, 0] = (char)TileType.Ghost;
                board[2, 0, 0] = (char)tile;
                for (int x = 3; x <= 6; x++) board[x, 0, 0] = (char)TileType.Road;
                var links = new Dictionary<Vector3Int, Vector3Int>();
                if (tile == TileType.PortalIn)
                {
                    var entrance = new Vector3Int(2, 0, 0);
                    var exit = new Vector3Int(0, 1, 0);
                    board[0, 1, 0] = (char)TileType.PortalOut;
                    links.Add(entrance, exit); links.Add(exit, entrance);
                }
                var sim = new PuzzleSolver.Simulation(8);
                Check(sim.Reset(board, links), "ghost restriction fixture");
                Check(sim.TryStep(Vector3Int.right) && sim.GhostRemaining == TileHelper.GhostCount, "ghost activated");
                int remaining = sim.Unpainted;
                Check(sim.TryStep(Vector3Int.right) && sim.Position == new Vector3Int(2, 0, 0) &&
                    sim.GhostRemaining == TileHelper.GhostCount - 1 && sim.Unpainted == remaining, "ghost skips " + tile);
                for (int x = 3; x <= 6; x++) Check(sim.TryStep(Vector3Int.right), "ghost expiry movement");
                Check(sim.GhostRemaining == 0 && sim.Unpainted == remaining - 1, "only expiry tile painted");
                Check(sim.Events.SequenceEqual(new[] { MapGimmick.Ghost }), "skipped gimmicks not reported");
            }
            var zeroGhost = Request(10, MapDifficulty.Medium);
            zeroGhost.UiConditions = null;
            zeroGhost.GimmickCounts = new[] { new MapGimmickCount { Kind = MapGimmick.Ghost, Count = 0 } };
            var zeroGhostResult = MapTools.Generate(null, zeroGhost, 1);
            Check(zeroGhostResult.Candidate != null && !zeroGhostResult.Candidate.Map.Tiles.Contains((char)TileType.Ghost), "zero ghost");
            var preserve = Request(10, MapDifficulty.Easy); preserve.PreserveStart = true;
            Check(MapTools.Generate(source, preserve, 0).Status == MapGenerationStatus.UnsupportedConditions, "preservation deferred");
            preserve.PreserveStart = false; preserve.Operation = MapAssistOperation.RelocateGimmick;
            Check(MapTools.Generate(source, preserve, 0).Status == MapGenerationStatus.UnsupportedConditions, "editing deferred");
            var forbidden = Request(10, MapDifficulty.Easy, 1);
            forbidden.UiConditions = new GenerationUiSettings { AutomaticGimmicks = false, AllowedGimmicks = MapGimmick.None };
            Check(MapTools.Generate(null, forbidden, 0).Status == MapGenerationStatus.UnsupportedConditions, "UI conflict");
            var exhausted = MapTools.Generate(null, Request(10, MapDifficulty.Hard), 2,
                limits: new MapGenerationLimits { PlacementAttempts = 1 });
            Check(exhausted.Status == MapGenerationStatus.SearchLimitReached && exhausted.Candidate == null, "attempt limit");
            var space = MapTools.Generate(null, Request(2, MapDifficulty.Hard, 3), 1);
            Check(space.Status == MapGenerationStatus.NoCandidateFound, "space shortage");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            bool threw = false;
            try { MapTools.Generate(null, sample, 1, cancelled.Token); }
            catch (OperationCanceledException) { threw = true; }
            Check(threw, "cancel before generation");
        int stageCount = 0;
        foreach (var path in System.IO.Directory.EnumerateFiles(System.Environment.GetEnvironmentVariable("MAP_GENERATOR_STAGE_DATA") ?? "Assets/Data", "*.asset", System.IO.SearchOption.AllDirectories))
        {
            var lines = System.IO.File.ReadAllLines(path);
            string Field(string key) => lines.FirstOrDefault(l => l.StartsWith("  " + key + ": "))?.Split(": ", 2)[1];
            string Decode(string key, string fallback)
            {
                var value = Field(key);
                return string.IsNullOrWhiteSpace(value) ? fallback : Newtonsoft.Json.JsonConvert.DeserializeObject<string>(value.Trim((char)39));
            }
            var tiles = Field("data");
            if (tiles == null || tiles.Length != 1000 || Field("portalPairs") == null)
                continue;
            var portals = Newtonsoft.Json.Linq.JArray.Parse(Decode("portalPairs", "[]"));
            var rotation = Newtonsoft.Json.Linq.JObject.Parse(Decode("rotationInfo", "{\"Axis\":0,\"Layers\":[]}"));
            Vector3Int Cell(Newtonsoft.Json.Linq.JToken xyz) => new((int)xyz[0], (int)xyz[1], (int)xyz[2]);
            var rawBoard = new MapDocument { Size = 10, Tiles = tiles }.CopyTiles();
            var legacyLinks = new Dictionary<Vector3Int, Vector3Int>();
            foreach (var pair in portals)
            {
                var a = Cell(pair["in"]);
                var b = Cell(pair["out"]);
                legacyLinks.Add(a, b);
                legacyLinks.Add(b, a);
            }
            var stage = MapDocument.FromBoard(rawBoard, legacyLinks, (int)rotation["Axis"],
                rotation["Layers"].ToObject<bool[]>());
            var solved = MapTools.Solve(stage);
            Check(solved.IsSolvable, $"{System.IO.Path.GetFileName(path)} {solved.Status}: {solved.ErrorMsg}");
            Check(MapTools.Verify(stage, solved.SolutionInputs).IsSolvable,
                $"{System.IO.Path.GetFileName(path)} replay");
            var simulation = new PuzzleSolver.Simulation(10, stage.RotationAxis, stage.RotatingLayers);
            Check(simulation.Reset(rawBoard, legacyLinks), "simulation reset");
            foreach (var input in solved.SolutionInputs) Check(simulation.TryStep(input), "simulation existing stage");
            Check(simulation.Unpainted == 0 && simulation.Position == solved.SolutionPath.Peek(), "simulation matches solver state");
            stageCount++;
        }
        Check(stageCount == 23, $"stages {stageCount}");

            Debug.Log($"NEW_MAP_GENERATOR_PASS checks={_checks} maps={generated} unique={boards.Count} backtracks={backtracks} maxMs={maxMs} totalMs={total.ElapsedMilliseconds}");
            EditorApplication.Exit(0);
        }
        catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
    }

    private static void CheckInverterGeneration()
    {
        foreach (int size in new[] { 2, 3, 10, 32 })
        for (int seed = 0; seed < 4; seed++)
        {
            var request = Request(size, MapDifficulty.Easy);
            request.UiConditions.AllowedGimmicks = MapGimmick.Inverter;
            var result = MapTools.Generate(null, request, seed);
            Check(result.Candidate != null, $"인버터 단독 생성 {size}/{seed} {result.Message}");
            var candidate = result.Candidate;
            Check(candidate.Witness.UsedGimmicks == MapGimmick.Inverter, "인버터만 사용");
            Check(MapTools.Verify(candidate.Map, candidate.Witness.SolutionInputs).IsSolvable, "인버터 단독 재생");
        }

        foreach (int size in new[] { 10, 32 })
        for (int seed = 0; seed < 4; seed++)
        {
            var request = Request(size, MapDifficulty.Medium, 1);
            request.UiConditions.AllowedGimmicks = MapGimmick.Portal | MapGimmick.Inverter;
            request.RequiredOrder = new[] { MapAssistEvent.PortalExit, MapAssistEvent.InverterActivated };
            var result = MapTools.Generate(null, request, seed);
            Check(result.Candidate != null, $"포탈→인버터 {size}/{seed} {result.Message}");
            var pair = result.Candidate.Map.PortalPairs.Single();
            var distance = pair.Exit - pair.Entrance;
            Check(Math.Abs(distance.x) + Math.Abs(distance.y) + Math.Abs(distance.z) > 1, "반전 조합의 비인접 포탈");
            Check(MapTools.Verify(result.Candidate.Map, result.Candidate.Witness.SolutionInputs).IsSolvable, "포탈·인버터 완전 재생");
        }

        foreach (int count in new[] { 1, 2 })
        for (int seed = 0; seed < 8; seed++)
        {
            var request = Request(10, MapDifficulty.Medium);
            request.UiConditions.AllowedGimmicks = MapGimmick.Laser | MapGimmick.Inverter;
            request.RequiredOrder = count == 1
                ? new[] { MapAssistEvent.LaserFired, MapAssistEvent.InverterActivated }
                : new[] { MapAssistEvent.LaserFired, MapAssistEvent.InverterActivated, MapAssistEvent.InverterActivated };
            request.GimmickCounts = new[] {
                new MapGimmickCount { Kind = MapGimmick.Laser, Count = 1 },
                new MapGimmickCount { Kind = MapGimmick.Inverter, Count = count }
            };
            var result = MapTools.Generate(null, request, seed);
            Check(result.Candidate != null, $"레이저→인버터 {count}/{seed} {result.Message}");
            var candidate = result.Candidate;
            var map = candidate.Map;
            Check(map.Tiles.Count(t => t == (char)TileType.Inv) == count, "인버터 정확한 개수");
            int cost = map.Tiles.Count(t => t == (char)TileType.Road) + 4 + 10 * count;
            Check(cost == candidate.GenerationCost && cost <= result.Plan.Budget.Maximum, "인버터 비용과 교체한 길 비용");
            Check(MapTools.Verify(map, candidate.Witness.SolutionInputs).IsSolvable, "레이저·인버터 완전 재생");

            var sim = new PuzzleSolver.Simulation(map.Size);
            Check(sim.Reset(map.CopyTiles(), map.CopyPortalLinks()), "복합 맵 초기화");
            var visited = new HashSet<Vector3Int> { sim.Position };
            var shot = new HashSet<Vector3Int>();
            bool enteredShotTile = false, activatedShotInverter = false;
            foreach (var input in candidate.Witness.SolutionInputs)
            {
                var position = sim.Position;
                int events = sim.Events.Count;
                Check(sim.TryStep(input), "복합 맵 입력");
                if (sim.Events.Count > events && sim.Events[events] == MapGimmick.Laser)
                {
                    for (var cell = position + input;
                        cell.IsOnSurface(map.Size) && TileHelper.IsPainted(sim.TileAt(cell)) && !visited.Contains(cell);
                        cell += input)
                        shot.Add(cell);
                }
                if (visited.Add(sim.Position) && shot.Contains(sim.Position) && sim.Events.Contains(MapGimmick.Inverter))
                {
                    enteredShotTile = true;
                    activatedShotInverter |= sim.Events.Count > events && sim.Events[events] == MapGimmick.Inverter;
                }
            }
            Check(sim.Unpainted == 0 && enteredShotTile, "레이저로만 칠했던 칸을 반전 후 처음 방문");
            Check(sim.Events.SequenceEqual(new[] { MapGimmick.Laser }.Concat(Enumerable.Repeat(MapGimmick.Inverter, count))),
                "레이저 이후 정확한 횟수만큼 인버터 발동");
            if (count == 2) Check(activatedShotInverter, "레이저에 맞았던 두 번째 인버터가 발동");
        }
    }
}
#endif
