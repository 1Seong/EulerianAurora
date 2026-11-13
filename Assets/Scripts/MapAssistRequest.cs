using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using EulerianAurora.Maps;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;

internal static class MapAssistRequestInterpreter
{
    internal const string Example = "시작점과 포탈 위치는 그대로 두고, 포탈 출구 이후 인버터를 밟아야 하는 구간을 만들어 줘.";
    internal const string RelocationExample = "기믹 하나를 다른 곳으로 옮겨줘.";
    internal const string CreationExample = "회전과 포탈, 레이저 5개가 들어간 어려운 맵을 만들어줘.";
    private const string GimmickNames = "인버터|기믹|포탈|포털|회전|고스트|레이저|대시";

    internal static bool TryInterpret(string message, out MapAssistRequest request)
    {
        request = default;
        if (string.IsNullOrWhiteSpace(message))
            return false;
        var text = message.Trim();
        if (text.StartsWith("{", StringComparison.Ordinal))
        {
            try
            {
                var document = JObject.Parse(text, new JsonLoadSettings
                    { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                var serializer = JsonSerializer.Create(new JsonSerializerSettings
                {
                    MissingMemberHandling = MissingMemberHandling.Error,
                    Converters = { new StringEnumConverter { AllowIntegerValues = false } }
                });
                request = document.ToObject<MapAssistRequest>(serializer);
                return request != null;
            }
            catch (JsonException)
            {
                return false;
            }
        }
        var relocation = Regex.Match(text,
            @"\A(?:(?:기존|여러)\s+)?(" + GimmickNames + @")(?:\s+(?:위치|(?:중\s+)?하나))?(?:를|을)?\s*(?:다른\s*(?:곳|칸|위치)(?:으)?로\s*)?(?:옮겨\s*줘|이동해\s*줘)[.!]?\z");
        if (relocation.Success)
        {
            request = new MapAssistRequest
            {
                Operation = relocation.Groups[1].Value == "인버터"
                    ? MapAssistOperation.RelocateInverter : MapAssistOperation.RelocateGimmick,
                MoveKind = Kind(relocation.Groups[1].Value),
                PreserveStart = true
            };
            return true;
        }
        if (TryCounts(text, out request))
            return true;

        // Match the whole message so unsupported conditions are never silently dropped.
        if (!Regex.IsMatch(text,
            @"\A(?:시작점|시작 위치)(?:과|이랑|과 함께) (?:포탈|포털)(?: 위치)?(?:는|를) (?:그대로 두고|유지하고|고정하고)\s*,?\s*(?:포탈|포털) 출구 (?:이후|뒤에|이후에)\s*(?:인버터를 (?:밟아야 하는|밝아야 하는|밟는)|인버터가 발동하는) 구간을 (?:만들어\s*줘|생성해\s*줘)[.!]?\z"))
            return false;

        request = new MapAssistRequest
        {
            PreserveStart = true,
            PreservePortals = true,
            RequiredOrder = new[] { MapAssistEvent.PortalExit, MapAssistEvent.InverterActivated }
        };
        return true;
    }

    private static bool TryCounts(string text, out MapAssistRequest request)
    {
        request = null;
        var edit = Regex.Match(text,
            @"\A(?:([XYZxyz])\s*=\s*(0|끝)\s*면을 유지하고\s*,?\s*)?(" + GimmickNames +
            @")(?:을|를)?\s+(\d+)\s*(?:개|쌍|층)(?:으)?로\s*(?:맞춰\s*줘|바꿔\s*줘)[.!]?\z");
        if (edit.Success && int.TryParse(edit.Groups[4].Value, out int amount))
        {
            var kind = Kind(edit.Groups[3].Value);
            if (kind == MapGimmick.None)
                return false;
            request = new MapAssistRequest
            {
                Operation = MapAssistOperation.ConfigureGimmicks,
                GimmickCounts = new[] { new MapGimmickCount { Kind = kind, Count = amount } },
                PreserveFaces = edit.Groups[1].Success
                    ? new[] { (MapFace)(("XYZ".IndexOf(edit.Groups[1].Value.ToUpperInvariant()[0]) * 2) +
                        (edit.Groups[2].Value == "끝" ? 1 : 0)) }
                    : Array.Empty<MapFace>()
            };
            return true;
        }

        var create = Regex.Match(text,
            @"\A(?:(.*?(?:들어간|필수인))\s+)?(?:(쉬운|보통|어려운)\s+)?(?:새\s+)?맵을\s*(?:만들어(?:\s*줘)?|생성해\s*줘)[.!]?\z");
        if (!create.Success)
            return false;
        var prefix = create.Groups[1].Value.Trim();
        bool required = Regex.IsMatch(prefix, @"(?:이|가)\s*필수인\z");
        if (prefix.Length > 0 && !required && !Regex.IsMatch(prefix, @"(?:이|가)\s*들어간\z"))
            return false;
        prefix = Regex.Replace(prefix, @"(?:이|가)\s*(?:들어간|필수인)\z", "").Trim();
        var counts = new List<MapGimmickCount>();
        var requiredKinds = new List<MapGimmick>();
        if (prefix.Length > 0)
        {
            foreach (string piece in Regex.Split(prefix, @"\s*(?:,|와|과|그리고)\s*"))
            {
                if (piece.Length == 0)
                    continue;
                var item = Regex.Match(piece, @"\A(" + GimmickNames + @")(?:\s+(\d+)\s*(?:개|쌍|층))?\z");
                if (!item.Success || Kind(item.Groups[1].Value) == MapGimmick.None)
                    return false;
                var kind = Kind(item.Groups[1].Value);
                int count = 1;
                if (item.Groups[2].Success && !int.TryParse(item.Groups[2].Value, out count))
                    return false;
                counts.Add(new MapGimmickCount { Kind = kind, Count = count });
                if (required)
                    requiredKinds.Add(kind);
            }
        }
        request = new MapAssistRequest
        {
            Operation = MapAssistOperation.CreateMap,
            GimmickCounts = counts.ToArray(),
            RequiredGimmicks = requiredKinds.ToArray(),
            Difficulty = create.Groups[2].Value switch
            {
                "쉬운" => MapDifficulty.Easy,
                "보통" => MapDifficulty.Medium,
                "어려운" => MapDifficulty.Hard,
                _ => MapDifficulty.Any
            }
        };
        return true;
    }

    private static MapGimmick Kind(string name) => name switch
    {
        "포탈" or "포털" => MapGimmick.Portal,
        "회전" => MapGimmick.Rotation,
        "고스트" => MapGimmick.Ghost,
        "인버터" => MapGimmick.Inverter,
        "레이저" => MapGimmick.Laser,
        "대시" => MapGimmick.Dash,
        _ => MapGimmick.None
    };
}
