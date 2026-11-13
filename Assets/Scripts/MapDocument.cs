using UnityEngine;
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EulerianAurora.Maps
{
    public sealed class MapPortalPair
    {
        [JsonConverter(typeof(MapCoordinateConverter))]
        public Vector3Int Entrance { get; set; }
        [JsonConverter(typeof(MapCoordinateConverter))]
        public Vector3Int Exit { get; set; }
    }

    internal sealed class MapCoordinateConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType) =>
            objectType == typeof(Vector3Int) || objectType == typeof(Vector3Int?);

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            if (value is Vector3Int cell)
                new JObject { ["x"] = cell.x, ["y"] = cell.y, ["z"] = cell.z }.WriteTo(writer);
            else
                writer.WriteNull();
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue,
            JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null && objectType == typeof(Vector3Int?))
                return null;
            var coordinates = JObject.Load(reader, new JsonLoadSettings
                { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            var x = coordinates.GetValue("x", StringComparison.OrdinalIgnoreCase);
            var y = coordinates.GetValue("y", StringComparison.OrdinalIgnoreCase);
            var z = coordinates.GetValue("z", StringComparison.OrdinalIgnoreCase);
            if (coordinates.Count != 3 || x?.Type != JTokenType.Integer || y?.Type != JTokenType.Integer ||
                z?.Type != JTokenType.Integer)
                throw new JsonSerializationException("좌표에는 정수 x, y, z가 필요합니다.");
            if (!int.TryParse(x.ToString(), out int cx) || !int.TryParse(y.ToString(), out int cy) ||
                !int.TryParse(z.ToString(), out int cz))
                throw new JsonSerializationException("좌표가 정수 범위를 벗어났습니다.");
            return new Vector3Int(cx, cy, cz);
        }
    }

    public sealed class MapDocument
    {
        public const int CurrentSchemaVersion = 1;
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public int Size { get; set; }
        public string Tiles { get; set; }
        public MapPortalPair[] PortalPairs { get; set; } = Array.Empty<MapPortalPair>();
        public int RotationAxis { get; set; }
        public bool[] RotatingLayers { get; set; } = Array.Empty<bool>();

        public char[,,] CopyTiles()
        {
            if (Size < 2 || Size > 32 || Tiles == null || Tiles.Length != Size * Size * Size)
                throw new InvalidOperationException("맵 크기와 타일 데이터가 일치하지 않습니다.");

            return StringHelper.DecodeCube(Tiles, Size);
        }

        public static MapDocument FromBoard(char[,,] tiles, IReadOnlyDictionary<Vector3Int, Vector3Int> portalLinks,
            int axis, bool[] rotatingLayers)
        {
            int size = tiles.GetLength(0);
            if (size != tiles.GetLength(1) || size != tiles.GetLength(2))
                throw new ArgumentException("맵 크기가 올바르지 않습니다.", nameof(tiles));
            return new MapDocument
            {
                Size = size, Tiles = StringHelper.Encode(tiles), RotationAxis = axis,
                PortalPairs = PortalPairHelper.ReadDirectedPairs(portalLinks, tiles)
                    .OrderBy(pair => pair.entry.x).ThenBy(pair => pair.entry.y).ThenBy(pair => pair.entry.z)
                    .Select(pair => new MapPortalPair { Entrance = pair.entry, Exit = pair.exit }).ToArray(),
                RotatingLayers = rotatingLayers != null ? (bool[])rotatingLayers.Clone() : Array.Empty<bool>()
            };
        }

        internal Dictionary<Vector3Int, Vector3Int> CopyPortalLinks() =>
            PortalPairHelper.ToDict(PortalPairs.Select(pair => (pair.Entrance, pair.Exit)));

        internal string Decode(out char[,,] tiles, out Dictionary<Vector3Int, Vector3Int> links)
        {
            tiles = null;
            links = null;
            if (SchemaVersion != CurrentSchemaVersion)
                return "지원하지 않는 맵 데이터 버전입니다.";
            if (Size < 2 || Size > 32 || Tiles == null || Tiles.Length != Size * Size * Size)
                return "맵 크기와 타일 데이터가 일치하지 않습니다.";
            if (RotationAxis < 0 || RotationAxis > 3 || RotatingLayers == null ||
                RotatingLayers.Length != Size && (RotationAxis != 0 || RotatingLayers.Length != 0) ||
                RotationAxis == 0 && Array.Exists(RotatingLayers, enabled => enabled) ||
                RotationAxis != 0 && (RotatingLayers[0] || RotatingLayers[Size - 1]))
                return "회전 설정이 올바르지 않습니다.";

            tiles = CopyTiles();
            int starts = 0;
            int portalTiles = 0;
            for (int x = 0; x < Size; x++)
            for (int y = 0; y < Size; y++)
            for (int z = 0; z < Size; z++)
            {
                char tile = tiles[x, y, z];
                if (tile == (char)TileType.Empty)
                    continue;
                if (!new Vector3Int(x, y, z).IsOnSurface(Size) || !TileHelper.PaintTable.ContainsKey(tile))
                    return "맵에 유효하지 않은 타일이 있습니다.";
                if (tile == (char)TileType.Player)
                    starts++;
                if (tile is (char)TileType.PortalIn or (char)TileType.PortalOut)
                    portalTiles++;
            }
            if (starts != 1)
                return "맵에 시작점이 한 칸 있어야 합니다.";
            if (PortalPairs == null || PortalPairs.Length * 2L != portalTiles)
                return "포탈 타일과 포탈 연결이 일치하지 않습니다.";

            links = new Dictionary<Vector3Int, Vector3Int>();
            foreach (var pair in PortalPairs)
            {
                if (pair == null || !HasTile(tiles, pair.Entrance, TileType.PortalIn) ||
                    !HasTile(tiles, pair.Exit, TileType.PortalOut) ||
                    !links.TryAdd(pair.Entrance, pair.Exit) || !links.TryAdd(pair.Exit, pair.Entrance))
                    return "연결이 올바르지 않은 포탈이 있습니다.";
            }
            return null;
        }

        private bool HasTile(char[,,] tiles, Vector3Int cell, TileType tile) =>
            cell.IsOnSurface(Size) && tiles[cell.x, cell.y, cell.z] == (char)tile;
    }
}
