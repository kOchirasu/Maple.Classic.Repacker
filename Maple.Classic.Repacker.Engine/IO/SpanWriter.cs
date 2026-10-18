using System.Buffers.Binary;

namespace Maple.Classic.Repacker.Engine.IO;

/// <summary>
/// Writes binary fields one after another into a fixed-size span.
/// </summary>
/// <remarks>
/// Every method names the field's size and byte order (<c>WriteUInt16LittleEndian</c>, <c>WriteUInt32BigEndian</c>, ...),
/// so the written layout reads like the format's struct definition and an expression's C# type can never silently
/// change how many bytes are written (a pitfall of <see cref="BinaryWriter"/>'s overloads). Writing past the end throws,
/// and <see cref="EnsureFilled"/> catches a buffer that was sized for more fields than were written.
/// </remarks>
internal ref struct SpanWriter(Span<byte> buffer) {
    private readonly Span<byte> buffer = buffer;

    /// <summary>Number of bytes written so far.</summary>
    public int Position { get; private set; }

    public readonly int Remaining => buffer.Length - Position;

    public readonly Span<byte> Written => buffer[..Position];

    public void WriteByte(byte value) => Advance(1)[0] = value;

    public void WriteBytes(scoped ReadOnlySpan<byte> value) => value.CopyTo(Advance(value.Length));

    public void WriteUInt16LittleEndian(ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(Advance(sizeof(ushort)), value);

    public void WriteInt32LittleEndian(int value) => BinaryPrimitives.WriteInt32LittleEndian(Advance(sizeof(int)), value);

    public void WriteUInt32LittleEndian(uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Advance(sizeof(uint)), value);

    public void WriteUInt32BigEndian(uint value) => BinaryPrimitives.WriteUInt32BigEndian(Advance(sizeof(uint)), value);

    /// <summary>Throws if fewer bytes were written than the buffer holds (i.e. the buffer size and the fields disagree).</summary>
    public readonly void EnsureFilled() {
        if (Position != buffer.Length) {
            throw new InvalidOperationException($"Wrote {Position} of {buffer.Length} bytes.");
        }
    }

    private Span<byte> Advance(int count) {
        if (count > Remaining) {
            throw new InvalidOperationException($"Cannot write {count} byte(s) at position {Position}: only {Remaining} remain.");
        }
        Span<byte> target = buffer.Slice(Position, count);
        Position += count;
        return target;
    }
}
