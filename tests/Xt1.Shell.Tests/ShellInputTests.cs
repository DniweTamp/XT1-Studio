using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Xt1.Shell;
using Xunit;

namespace Xt1.Shell.Tests;

public sealed class ShellInputTests
{
    [Fact]
    public void ReadAll_RejectsAStreamBeyondTheShellBudget()
    {
        using var stream = new OversizedComStream(Streams.MaxBytes + 1L);

        Assert.Throws<InvalidDataException>(() => Streams.ReadAll(stream));
    }

    [Fact]
    public void ReadFile_RejectsAnOversizedSparseFileBeforeAllocating()
    {
        var path = Path.Combine(Path.GetTempPath(), "Xt1Studio", "shell-tests", Guid.NewGuid() + ".xt1");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                file.SetLength(Streams.MaxBytes + 1L);

            Assert.Throws<InvalidDataException>(() => Streams.ReadFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class OversizedComStream(long length) : IStream, IDisposable
    {
        private readonly Stream _stream = new SparseStream(length);

        public void Read(byte[] pv, int cb, IntPtr pcbRead)
        {
            int count = _stream.Read(pv, 0, cb);
            if (pcbRead != IntPtr.Zero) Marshal.WriteInt32(pcbRead, count);
        }

        public void Seek(long dlibMove, int dwOrigin, IntPtr plibNewPosition)
        {
            long position = _stream.Seek(dlibMove, (SeekOrigin)dwOrigin);
            if (plibNewPosition != IntPtr.Zero) Marshal.WriteInt64(plibNewPosition, position);
        }

        public void Stat(out STATSTG pstatstg, int grfStatFlag) => pstatstg = new STATSTG { cbSize = length };
        public void Clone(out IStream ppstm) => throw new NotSupportedException();
        public void Commit(int grfCommitFlags) { }
        public void CopyTo(IStream pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten) => throw new NotSupportedException();
        public void LockRegion(long libOffset, long cb, int dwLockType) => throw new NotSupportedException();
        public void Revert() => throw new NotSupportedException();
        public void SetSize(long libNewSize) => throw new NotSupportedException();
        public void UnlockRegion(long libOffset, long cb, int dwLockType) => throw new NotSupportedException();
        public void Write(byte[] pv, int cb, IntPtr pcbWritten) => throw new NotSupportedException();
        public void Dispose() => _stream.Dispose();
    }

    private sealed class SparseStream(long length) : Stream
    {
        private long _position;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => _position = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = (int)Math.Min(count, length - _position);
            Array.Clear(buffer, offset, read);
            _position += read;
            return read;
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            _position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => length + offset,
                _ => _position,
            };
            return _position;
        }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
