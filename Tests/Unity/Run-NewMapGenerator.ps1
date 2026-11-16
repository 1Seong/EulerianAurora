$ErrorActionPreference = 'Stop'
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$testRoot = Join-Path $projectRoot 'Temp/MapGenerationTests'
New-Item -ItemType Directory -Force "$testRoot/Assets/Editor", "$testRoot/Assets/MapCore", "$testRoot/Assets/Resources", "$testRoot/Packages", "$testRoot/ProjectSettings" | Out-Null
foreach ($name in @('PuzzleSolver','StringHelper','MapGimmick','MapDocument','MapAssistBridge','MapAssistContracts')) {
    Copy-Item -LiteralPath "$projectRoot/Assets/Scripts/$name.cs" -Destination "$testRoot/Assets/$name.cs"
}
Copy-Item "$projectRoot/Assets/Scripts/MapCore/*.cs" "$testRoot/Assets/MapCore"
Copy-Item "$projectRoot/Assets/Scripts/MapCore/MapGenerationSettings.cs.meta" "$testRoot/Assets/MapCore"
Copy-Item "$projectRoot/Assets/Resources/MapGenerationSettings.asset*" "$testRoot/Assets/Resources"
# 게임의 타일 규칙을 원본에서 추출한다. 테스트용 규칙 구현을 별도로 유지하지 않는다.
$playerSource = Get-Content -Raw "$projectRoot/Assets/Scripts/PuzzlePlayer.cs"
$begin = $playerSource.IndexOf('#region TileHelpers')
$end = $playerSource.IndexOf('#endregion', $begin)
if ($begin -lt 0 -or $end -lt 0) { throw 'TileHelpers 영역을 찾지 못했습니다.' }
$helper = "using System;`nusing System.Collections.Generic;`nusing UnityEngine;`n" + $playerSource.Substring($begin, $end - $begin) + '#endregion'
Set-Content "$testRoot/Assets/TileHelper.cs" $helper
# 내부 시뮬레이션 경계를 검사하기 위한 설정이며 실제 게임에는 추가하지 않는다.
Set-Content "$testRoot/Assets/AssemblyInfo.cs" '[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Assembly-CSharp-Editor")]'
Copy-Item "$PSScriptRoot/NewMapGeneratorSmoke.cs" "$testRoot/Assets/Editor"
Copy-Item "$projectRoot/ProjectSettings/ProjectVersion.txt" "$testRoot/ProjectSettings"
$jsonPackage = Get-ChildItem "$projectRoot/Library/PackageCache" -Directory -Filter 'com.unity.nuget.newtonsoft-json@*' | Select-Object -First 1
if (!$jsonPackage) { throw '프로젝트를 Unity에서 열어 Newtonsoft JSON 패키지를 먼저 복원해 주세요.' }
$manifest = @{ dependencies = @{
    'com.unity.modules.imgui' = '1.0.0'
    'com.unity.modules.jsonserialize' = '1.0.0'
    'com.unity.nuget.newtonsoft-json' = 'file:' + $jsonPackage.FullName.Replace('\','/')
} }
$manifest | ConvertTo-Json -Depth 4 | Set-Content "$testRoot/Packages/manifest.json"
$previousData = $env:MAP_GENERATOR_STAGE_DATA
try {
    $env:MAP_GENERATOR_STAGE_DATA = Join-Path $projectRoot 'Assets/Data'
    unity --non-interactive run $testRoot --timeout 110 --no-tail --log-file "$testRoot/test.log" -- -executeMethod NewMapGeneratorSmoke.Run -nographics
    if ($LASTEXITCODE -ne 0) { throw "Unity 검사 실패. 로그: $testRoot/test.log" }
    Select-String -Path "$testRoot/test.log" -Pattern 'NEW_MAP_GENERATOR_PASS'
}
finally { $env:MAP_GENERATOR_STAGE_DATA = $previousData }
