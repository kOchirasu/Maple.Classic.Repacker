using MessagePack;
using MessagePack.Resolvers;

namespace Maple.Classic.Repacker.UI.Rpc;

internal static class RpcSerializer {
    public static readonly MessagePackSerializerOptions Options =
        ContractlessStandardResolver.Options.WithResolver(
            CompositeResolver.Create(
                DynamicEnumAsStringIgnoreCaseResolver.Instance,
                ContractlessStandardResolver.Instance));

    public static byte[] Encode<T>(T value) =>
        MessagePackSerializer.Serialize(value, Options);

    public static T DecodeParams<T>(ReadOnlyMemory<byte> rawParams) where T : new() =>
        rawParams.IsEmpty ? new T() : MessagePackSerializer.Deserialize<T>(rawParams, Options);

    public static (long Id, string Method, ReadOnlyMemory<byte> RawParams) ReadRequestFrame(ReadOnlyMemory<byte> frame) {
        var reader = new MessagePackReader(frame);
        int count = reader.ReadArrayHeader();
        if (count < 2) {
            return (0, string.Empty, default);
        }
        long id = reader.ReadInt64();
        string method = reader.ReadString() ?? string.Empty;
        ReadOnlyMemory<byte> rawParams = count >= 3
            ? frame.Slice((int) reader.Consumed)
            : default;
        return (id, method, rawParams);
    }
}

public sealed record RpcResponseEnvelope(
    string Type,
    long Id,
    bool Ok,
    object? Data);
