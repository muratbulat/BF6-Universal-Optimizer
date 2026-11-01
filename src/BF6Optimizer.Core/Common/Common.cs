using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BF6Optimizer.Core.Common;

/// <summary>A localizable message: Core emits keys and arguments, the UI resolves the text.</summary>
public sealed record Msg(string Key, params string[] Args)
{
    public override string ToString() => Args.Length == 0 ? Key : $"{Key}({string.Join(", ", Args)})";
}

/// <summary>An expected, user-explainable failure. Never shown as a raw stack trace.</summary>
public class OptimizerException(Msg message, Exception? inner = null)
    : Exception(message.ToString(), inner)
{
    public Msg UserMessage { get; } = message;
}

public static class Hashing
{
    public static string Sha256(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data));

    public static bool Same(string? a, string? b) =>
        a is not null && b is not null && a.Equals(b, StringComparison.OrdinalIgnoreCase);
}

public interface IClock
{
    DateTimeOffset Now { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.Now;
}

public static class AppInfo
{
    public const string Version = "2.0.0";
    public const string ProductName = "BF6 Universal Optimizer";
    public const string Author = "Murat Bulat";
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}
