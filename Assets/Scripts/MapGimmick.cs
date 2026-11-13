using System;

namespace EulerianAurora.Maps
{
    // PuzzleSolver와 공유하는 형식이다. 기존 직렬화 데이터의 비트 값을 유지한다.
    [Flags]
    public enum MapGimmick
    {
        None = 0, Portal = 1, Rotation = 2, Ghost = 4,
        Inverter = 8, Laser = 16, Dash = 32,
        All = Portal | Rotation | Ghost | Inverter | Laser | Dash
    }
    public enum MapDifficulty { Any, Easy, Medium, Hard }
    public enum MapFace { XMin, XMax, YMin, YMax, ZMin, ZMax }
    public sealed class MapGimmickCount
    {
        public MapGimmick Kind { get; set; }
        public int Count { get; set; }
    }
}
