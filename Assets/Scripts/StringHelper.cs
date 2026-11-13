using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

public static class StringHelper
{
    public static string Encode(char[,,] cube) => string.Create(cube.Length, cube, (buffer, board) =>
    {
        int index = 0;
        foreach (char tile in board)
            buffer[index++] = tile;
    });
    
    public static char[,,] DecodeCube(string data)
    {
        int size = Mathf.RoundToInt(Mathf.Pow(data.Length, 1f / 3f));
        return DecodeCube(data, size);
    }

    internal static char[,,] DecodeCube(string data, int size)
    {
        var cube = new char[size, size, size];
        int i = 0;
        for (int x = 0; x < size; ++x)
            for (int y = 0; y < size; ++y)
                for (int z = 0; z < size; ++z)
                    cube[x, y, z] = data[i++];
        return cube;
    }
}

[Serializable]
public struct PortalPair
{
    [JsonProperty("in")]  public int[] In;   // [x, y, z]
    [JsonProperty("out")] public int[] Out;
    
    // 편의용 프로퍼티
    [JsonIgnore] public Vector3Int InPos  => new(In[0],  In[1],  In[2]);
    [JsonIgnore] public Vector3Int OutPos => new(Out[0], Out[1], Out[2]);
}

public static class PortalPairHelper
{
    public static string Encode(List<PortalPair> pairs)
        => JsonConvert.SerializeObject(pairs);

    public static List<PortalPair> Decode(string json)
        => JsonConvert.DeserializeObject<List<PortalPair>>(json) ?? new List<PortalPair>();
    
    // Vector3Int로 직접 생성하는 헬퍼
    public static PortalPair CreatePair(Vector3Int inPos, Vector3Int outPos) => new()
    {
        In  = new[] { inPos.x,  inPos.y,  inPos.z  },
        Out = new[] { outPos.x, outPos.y, outPos.z }
    };
    
    // List → Dictionary 변환 헬퍼
    public static Dictionary<Vector3Int, Vector3Int> ToDict(List<PortalPair> pairs) =>
        ToDict(pairs.Select(pair => (pair.InPos, pair.OutPos)));

    internal static Dictionary<Vector3Int, Vector3Int> ToDict(IEnumerable<(Vector3Int entry, Vector3Int exit)> pairs)
    {
        var dict = new Dictionary<Vector3Int, Vector3Int>();
        foreach (var (entry, exit) in pairs)
        {
            dict[entry] = exit;
            dict[exit] = entry;
        }
        return dict;
    }
    
    public static List<PortalPair> ToList(Dictionary<Vector3Int, Vector3Int> dict)
    {
        var pairs = new List<PortalPair>();
        var visited = new HashSet<Vector3Int>();

        foreach (var (inPos, outPos) in dict)
        {
            if (visited.Contains(inPos)) continue;

            pairs.Add(CreatePair(inPos, outPos));
            visited.Add(inPos);
            visited.Add(outPos); // 반대 방향 중복 방지
        }

        return pairs;
    }

    internal static IEnumerable<(Vector3Int entry, Vector3Int exit)> ReadDirectedPairs(
        IReadOnlyDictionary<Vector3Int, Vector3Int> links, char[,,] tiles)
    {
        if (links == null)
            yield break;
        foreach (var (entry, exit) in links)
            if (entry.IsInBounds(tiles.GetLength(0)) &&
                tiles[entry.x, entry.y, entry.z] == (char)TileType.PortalIn &&
                links.TryGetValue(exit, out var reverse) && reverse == entry)
                yield return (entry, exit);
    }
}

[Serializable]
public struct RotateInfo
{
    [JsonProperty("Axis")] public int Axis;
    [JsonProperty("Layers")] public bool[] Layers;
}

public static class RotateHelper
{
    public static string Encode(RotateInfo rotateInfo)
        =>JsonConvert.SerializeObject(rotateInfo);

    public static RotateInfo Decode(string json)
        => JsonConvert.DeserializeObject<RotateInfo>(json);
}
