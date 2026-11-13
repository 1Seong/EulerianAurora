using System;
using Newtonsoft.Json;
using UnityEngine;
namespace EulerianAurora.Maps
{
    public enum MapAssistEvent
    {
        PortalExit,
        InverterActivated
    }

    public enum MapAssistOperation
    {
        BuildRoute,
        RelocateInverter,
        RelocateGimmick,
        ConfigureGimmicks,
        CreateMap
    }

    // 백엔드에서 전달한 구조화된 요청 데이터다. 게임 내부에서 자연어를 해석하지 않는다.
    public sealed class MapAssistRequest
    {
        // 로컬 UI 값은 백엔드 응답과 분리한다. JSON에서 덮어쓸 수 없다.
        [JsonIgnore]
        public GenerationUiSettings UiConditions { get; set; }
        public MapAssistOperation Operation { get; set; }
        public bool PreserveStart { get; set; }
        public bool PreservePortals { get; set; }
        public bool PreserveRotation { get; set; }
        [JsonProperty(ItemConverterType = typeof(MapCoordinateConverter))]
        public Vector3Int[] PreserveCells { get; set; } = Array.Empty<Vector3Int>();
        public MapFace[] PreserveFaces { get; set; } = Array.Empty<MapFace>();
        public MapGimmickCount[] GimmickCounts { get; set; } = Array.Empty<MapGimmickCount>();
        public MapGimmick[] RequiredGimmicks { get; set; } = Array.Empty<MapGimmick>();
        public MapGimmick MoveKind { get; set; }
        public MapDifficulty Difficulty { get; set; }
        public int? Size { get; set; }
        public MapAssistEvent[] RequiredOrder { get; set; } = Array.Empty<MapAssistEvent>();
    }

}
