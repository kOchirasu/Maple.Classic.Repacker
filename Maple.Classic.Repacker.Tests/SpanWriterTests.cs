using Maple.Classic.Repacker.Engine.IO;
using Xunit;

namespace Maple.Classic.Repacker.Tests;

public sealed class SpanWriterTests {
    [Fact]
    public void WritesFieldsSequentiallyWithExplicitByteOrder() {
        byte[] buffer = new byte[13];
        var w = new SpanWriter(buffer);
        w.WriteByte(0xAB);
        w.WriteUInt16LittleEndian(0x0102);
        w.WriteInt32LittleEndian(-2);
        w.WriteUInt32BigEndian(0x0A0B0C0D);
        w.WriteBytes([0x11, 0x22]);

        Assert.Equal(13, w.Position);
        Assert.Equal(0, w.Remaining);
        w.EnsureFilled();
        Assert.Equal(
            new byte[] { 0xAB, 0x02, 0x01, 0xFE, 0xFF, 0xFF, 0xFF, 0x0A, 0x0B, 0x0C, 0x0D, 0x11, 0x22 },
            buffer);
    }

    [Fact]
    public void WritingPastTheEndThrows() {
        byte[] buffer = new byte[3];
        var w = new SpanWriter(buffer);
        w.WriteUInt16LittleEndian(1);
        try {
            w.WriteUInt16LittleEndian(2);
            Assert.Fail("Expected an overflow.");
        } catch (InvalidOperationException ex) {
            Assert.Contains("only 1 remain", ex.Message);
        }
        Assert.Equal(2, w.Position);
    }

    [Fact]
    public void EnsureFilledThrowsWhenBufferIsLargerThanWhatWasWritten() {
        byte[] buffer = new byte[4];
        var w = new SpanWriter(buffer);
        w.WriteUInt16LittleEndian(1);
        try {
            w.EnsureFilled();
            Assert.Fail("Expected a size mismatch.");
        } catch (InvalidOperationException ex) {
            Assert.Equal("Wrote 2 of 4 bytes.", ex.Message);
        }
    }
}
