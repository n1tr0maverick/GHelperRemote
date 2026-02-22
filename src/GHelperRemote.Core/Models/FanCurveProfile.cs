using System.Text.Json.Serialization;

namespace GHelperRemote.Core.Models;

public class FanCurveProfile
{
    [JsonPropertyName("temps")]
    public byte[] Temperatures { get; set; } = new byte[8];

    [JsonPropertyName("speeds")]
    public byte[] Speeds { get; set; } = new byte[8];

    public static FanCurveProfile FromHexString(string hex)
    {
        var bytes = hex.Split('-').Select(b => Convert.ToByte(b, 16)).ToArray();
        if (bytes.Length != 16) throw new ArgumentException("Fan curve must be 16 bytes");
        return new FanCurveProfile
        {
            Temperatures = bytes[..8],
            Speeds = bytes[8..]
        };
    }

    public string ToHexString() =>
        string.Join("-", Temperatures.Concat(Speeds).Select(b => b.ToString("X2")));
}
