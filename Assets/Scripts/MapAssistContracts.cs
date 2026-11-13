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

    public sealed class MapAssistRequest
    {
        // UI 값은 자연어/JSON 요청과 분리하여 충돌 검사에 사용한다.
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
