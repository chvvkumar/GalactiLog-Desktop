using System.Diagnostics;
using GalactiLog.Core.Io;
using GalactiLog.Core.Wbpp;
using Xunit;
using Xunit.Abstractions;

namespace GalactiLog.Core.Tests.Wbpp;

// The staging copier over a real source tree and a real staging writer, both under this test's own
// temp root with fabricated names. Fault cases wrap the real seams to throw on a chosen call.
public sealed class StagingCopierTests : IDisposable
{
    private const string Entry = "Night1";
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "staging-copier-" + Guid.NewGuid().ToString("N"));
    private readonly string _source;
    private readonly string _staging;
    private readonly ITestOutputHelper _output;
    private string? _junction;

    public StagingCopierTests(ITestOutputHelper output)
    {
        _output = output;
        _source = Path.Combine(_temp, "library", "TargetA", "2026-01-01");
        _staging = Path.Combine(_temp, "staging");
        Directory.CreateDirectory(_source);
    }

    public void Dispose()
    {
        try
        {
            // Deletes the junction itself first, never what it targets.
            if (_junction is not null)
            {
                Directory.Delete(_junction, recursive: false);
            }
            Directory.Delete(_temp, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup only.
        }
    }

    private AppWriter.StagingWriter Writer()
        => new AppWriter(Path.Combine(_temp, "appdata"), dataRootPointerPath: Path.Combine(_temp, "appdata", "datapath.json"))
            .BeginStagingCopy(_staging);

    private byte[] Source(string relative, int length, byte seed = 1)
    {
        var path = Path.Combine(_source, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = Enumerable.Range(0, length).Select(i => (byte)(i * 31 + seed)).ToArray();
        File.WriteAllBytes(path, bytes);
        return bytes;
    }

    private string Destination(string relative) => Path.Combine(_staging, Entry, relative);

    private StagingCopyRequest Request(IReadOnlyList<string>? exclusions = null, IReadOnlyList<string>? excludedFiles = null)
        => new([new CopyOperation(new DateOnly(2026, 1, 1), _source, Entry, excludedFiles ?? [])], _staging, exclusions ?? []);

    private sealed class Recorder(Action<StagingProgress>? onReport = null) : IProgress<StagingProgress>
    {
        public List<StagingProgress> Reports { get; } = [];

        public void Report(StagingProgress value)
        {
            Reports.Add(value);
            onReport?.Invoke(value);
        }
    }

    private static StagingIo FailingOnCall(StagingIo io, int call, Exception exception, Action<int>? onCall = null)
    {
        var calls = 0;
        return io with
        {
            CreateDestination = path =>
            {
                var n = Interlocked.Increment(ref calls);
                onCall?.Invoke(n);
                return n == call ? throw exception : io.CreateDestination(path);
            },
        };
    }

    // Delegates to a real stream, optionally awaiting before a read or throwing from a read or write.
    private sealed class SeamStream(
        Stream inner, Func<int, ValueTask>? beforeRead = null, Exception? readError = null, Exception? writeError = null) : Stream
    {
        private int _reads;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = ++_reads;
            if (readError is not null)
            {
                throw readError;
            }
            if (beforeRead is not null)
            {
                await beforeRead(n);
            }
            return await inner.ReadAsync(buffer, cancellationToken);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => writeError is not null ? throw writeError : inner.WriteAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private void SixFiles(int length = 4096)
    {
        for (var i = 1; i <= 6; i++)
        {
            Source($"frame{i}.fits", length, (byte)i);
        }
    }

    [Fact]
    public async Task CopiesATwoLevelTreeByteForByteUnderTheEntryName()
    {
        var a = Source(@"Light\a.fits", 3000);
        var b = Source(@"Light\sub\b.fits", 5000, 7);
        var c = Source("notes.txt", 10, 3);
        using var writer = Writer();

        var result = await new StagingCopier(StagingIo.For(writer)).RunAsync(Request(), null, CancellationToken.None);

        Assert.Equal(StagingOutcome.Completed, result.Outcome);
        Assert.Equal(3, result.Copied);
        Assert.Equal(8010, result.BytesCopied);
        Assert.Equal(a, File.ReadAllBytes(Destination(@"Light\a.fits")));
        Assert.Equal(b, File.ReadAllBytes(Destination(@"Light\sub\b.fits")));
        Assert.Equal(c, File.ReadAllBytes(Destination("notes.txt")));
    }

    [Fact]
    public async Task AnExcludedFolderAndAnExcludedFileAreNeitherCopiedNorCounted()
    {
        Source(@"Light\a.fits", 100);
        Source(@"WBPP\master.xisf", 100);
        Source(@"Light\lights_Calibrated\c.xisf", 100);
        Source(@"Light\rejected.fits", 100);
        var progress = new Recorder();
        using var writer = Writer();

        var result = await new StagingCopier(StagingIo.For(writer)).RunAsync(
            Request(exclusions: ["WBPP", "*CALIBRATED"], excludedFiles: [@"Light\rejected.fits"]), progress, CancellationToken.None);

        Assert.Equal(1, result.Copied);
        Assert.Equal(1, progress.Reports[^1].FilesTotal);
        Assert.Equal(100, progress.Reports[^1].BytesTotal);
        Assert.False(Directory.Exists(Destination("WBPP")));
        Assert.False(Directory.Exists(Destination(@"Light\lights_Calibrated")));
        Assert.False(File.Exists(Destination(@"Light\rejected.fits")));
    }

    [Fact]
    public async Task AnExistingDestinationIsSkippedBySizeAndNeverRewritten()
    {
        Source("same.fits", 100);
        Source("different.fits", 100);
        Source("new.fits", 100);
        Directory.CreateDirectory(Path.Combine(_staging, Entry));
        var sameBytes = Enumerable.Repeat((byte)0xEE, 100).ToArray();
        var differentBytes = new byte[] { 1, 2, 3 };
        File.WriteAllBytes(Destination("same.fits"), sameBytes);
        File.WriteAllBytes(Destination("different.fits"), differentBytes);
        using var writer = Writer();

        var result = await new StagingCopier(StagingIo.For(writer)).RunAsync(Request(), null, CancellationToken.None);

        Assert.Equal(StagingOutcome.Completed, result.Outcome);
        Assert.Equal(1, result.Copied);
        Assert.Empty(result.Failed);
        Assert.Contains(new StagingSkip(Destination("same.fits"), StagingSkipReason.ExistsSameSize), result.Skipped);
        Assert.Contains(new StagingSkip(Destination("different.fits"), StagingSkipReason.ExistsDifferentSize), result.Skipped);
        Assert.Equal(sameBytes, File.ReadAllBytes(Destination("same.fits")));
        Assert.Equal(differentBytes, File.ReadAllBytes(Destination("different.fits")));
    }

    [Fact]
    public async Task AReparsePointDirectoryUnderTheSourceIsSkipped()
    {
        Source("a.fits", 100);
        var outside = Path.Combine(_temp, "elsewhere");
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(outside, "foreign.fits"), new byte[] { 1 });
        var link = Path.Combine(_source, "linked");
        using var mklink = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{link}\" \"{outside}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        })!;
        mklink.WaitForExit();
        if (mklink.ExitCode != 0)
        {
            _output.WriteLine($"Skipped: the OS refused a junction, mklink /J exited {mklink.ExitCode}");
            return;
        }
        _junction = link;
        using var writer = Writer();

        var result = await new StagingCopier(StagingIo.For(writer)).RunAsync(Request(), null, CancellationToken.None);

        Assert.Equal(1, result.Copied);
        Assert.Contains(new StagingSkip(link, StagingSkipReason.ReparsePoint), result.Skipped);
        Assert.False(Directory.Exists(Destination("linked")));
    }

    [Fact]
    public async Task CancellationFinishesTheFilesInFlightAndStartsNoOther()
    {
        Source("frame1.fits", 100);
        for (var i = 2; i <= 6; i++)
        {
            Source($"frame{i}.fits", 3 * StagingCopier.BufferBytes, (byte)i);
        }
        using var cts = new CancellationTokenSource();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new Recorder(_ =>
        {
            cts.Cancel();
            gate.TrySetResult();
        });
        using var writer = Writer();
        var real = StagingIo.For(writer);
        var io = real with
        {
            // Every file but the first holds its second read until the cancel, so it is in flight then.
            OpenSource = path => Path.GetFileName(path) == "frame1.fits"
                ? real.OpenSource(path)
                : new SeamStream(real.OpenSource(path), beforeRead: n => n > 1 ? new ValueTask(gate.Task) : ValueTask.CompletedTask),
        };

        var result = await new StagingCopier(io).RunAsync(Request(), progress, cts.Token);

        Assert.Equal(StagingOutcome.Cancelled, result.Outcome);
        Assert.Empty(result.PartialPaths);
        var copies = Directory.GetFiles(Path.Combine(_staging, Entry));
        Assert.Equal(StagingCopier.PoolSize, result.Copied);
        Assert.Equal(result.Copied, copies.Length);
        foreach (var copy in copies)
        {
            Assert.Equal(File.ReadAllBytes(Path.Combine(_source, Path.GetFileName(copy))), File.ReadAllBytes(copy));
        }
    }

    [Fact]
    public async Task DiskFullOnTheThirdFileAbortsAndStartsNoFileAfterIt()
    {
        SixFiles();
        var calls = 0;
        using var writer = Writer();
        var io = FailingOnCall(
            StagingIo.For(writer), 3, new IOException("There is not enough space on the disk.", unchecked((int)0x80070070)), n => calls = n);

        var result = await new StagingCopier(io).RunAsync(Request(), null, CancellationToken.None);

        Assert.Equal(StagingOutcome.Aborted, result.Outcome);
        Assert.Equal("There is not enough space on the disk.", result.AbortReason);
        Assert.Equal(3, calls);
        Assert.InRange(result.Copied, 0, 2);
        Assert.Empty(result.Failed);
    }

    [Fact]
    public async Task AGenericErrorOnTheThirdFileIsRecordedAndTheRestCopy()
    {
        SixFiles();
        using var writer = Writer();
        var io = FailingOnCall(StagingIo.For(writer), 3, new IOException("The device is not ready."));

        var result = await new StagingCopier(io).RunAsync(Request(), null, CancellationToken.None);

        Assert.Equal(StagingOutcome.Completed, result.Outcome);
        Assert.Equal("The device is not ready.", Assert.Single(result.Failed).Message);
        Assert.Equal(5, result.Copied);
        Assert.Null(result.AbortReason);
    }

    [Fact]
    public async Task ProgressEndsAtEveryFileAndEveryByteWithASkipCountedAsDone()
    {
        SixFiles(length: 1000);
        Directory.CreateDirectory(Path.Combine(_staging, Entry));
        File.WriteAllBytes(Destination("frame6.fits"), new byte[1000]);
        var progress = new Recorder();
        using var writer = Writer();

        var result = await new StagingCopier(StagingIo.For(writer)).RunAsync(Request(), progress, CancellationToken.None);

        Assert.Single(result.Skipped);
        Assert.Equal(5, progress.Reports.Count);
        var last = progress.Reports.MaxBy(p => p.FilesDone)!;
        Assert.Equal(6, last.FilesTotal);
        Assert.Equal(last.FilesTotal, last.FilesDone);
        Assert.Equal(6000, last.BytesTotal);
        Assert.Equal(last.BytesTotal, last.BytesDone);
    }

    [Fact]
    public async Task AFileNamedLikeAPatternIsNeitherCopiedNorCounted()
    {
        Source(@"Light\a.fits", 100);
        Source(@"Light\WBPP", 100);
        var progress = new Recorder();
        using var writer = Writer();

        var result = await new StagingCopier(StagingIo.For(writer)).RunAsync(Request(exclusions: ["WBPP"]), progress, CancellationToken.None);

        Assert.Equal(1, result.Copied);
        Assert.Equal(1, progress.Reports[^1].FilesTotal);
        Assert.False(File.Exists(Destination(@"Light\WBPP")));
    }

    [Fact]
    public async Task ARefusedDestinationAbortsWithAReasonTheUserCanActOn()
    {
        SixFiles();
        using var writer = Writer();
        var io = FailingOnCall(StagingIo.For(writer), 1, new UnauthorizedPathException(Destination("frame1.fits")));

        var result = await new StagingCopier(io).RunAsync(Request(), null, CancellationToken.None);

        Assert.Equal(StagingOutcome.Aborted, result.Outcome);
        Assert.Contains("Pick another staging folder", result.AbortReason);
        Assert.Contains(Destination("frame1.fits"), result.AbortReason);
    }

    [Fact]
    public async Task DiskFullDuringAWriteAbortsListsThatFileAndStartsNoFileAfterTheAbort()
    {
        for (var i = 1; i <= 10; i++)
        {
            Source($"frame{i:00}.fits", 4096, (byte)i);
        }
        var aborted = false;
        var startedAfterAbort = 0;
        var progress = new Recorder(p =>
        {
            if (Path.GetFileName(p.CurrentFile) == "frame03.fits")
            {
                aborted = true;
            }
        });
        using var writer = Writer();
        var real = StagingIo.For(writer);
        var io = real with
        {
            CreateDestination = path =>
            {
                if (Volatile.Read(ref aborted))
                {
                    Interlocked.Increment(ref startedAfterAbort);
                }
                var stream = real.CreateDestination(path);
                return Path.GetFileName(path) == "frame03.fits"
                    ? new SeamStream(stream, writeError: new IOException("There is not enough space on the disk.", unchecked((int)0x80070070)))
                    : stream;
            },
        };

        var result = await new StagingCopier(io).RunAsync(Request(), progress, CancellationToken.None);

        Assert.Equal(StagingOutcome.Aborted, result.Outcome);
        Assert.Equal("There is not enough space on the disk.", result.AbortReason);
        Assert.Contains(Destination("frame03.fits"), result.PartialPaths);
        Assert.Equal(0, startedAfterAbort);
        Assert.InRange(result.Copied, 0, 9);
    }

    [Fact]
    public async Task HandleDiskFullOnCreateAborts()
    {
        SixFiles();
        using var writer = Writer();
        var io = FailingOnCall(StagingIo.For(writer), 3, new IOException("The disk is full.", unchecked((int)0x80070027)));

        var result = await new StagingCopier(io).RunAsync(Request(), null, CancellationToken.None);

        Assert.Equal(StagingOutcome.Aborted, result.Outcome);
        Assert.Equal("The disk is full.", result.AbortReason);
    }

    [Fact]
    public async Task AccessDeniedOnTheDestinationAborts()
    {
        SixFiles();
        using var writer = Writer();
        var io = FailingOnCall(StagingIo.For(writer), 3, new UnauthorizedAccessException("Access to the path is denied."));

        var result = await new StagingCopier(io).RunAsync(Request(), null, CancellationToken.None);

        Assert.Equal(StagingOutcome.Aborted, result.Outcome);
        Assert.Equal("Access to the path is denied.", result.AbortReason);
    }

    [Fact]
    public async Task AccessDeniedReadingASourceFailsThatFileAndTheRunContinues()
    {
        SixFiles();
        using var writer = Writer();
        var real = StagingIo.For(writer);
        var io = real with
        {
            OpenSource = path => Path.GetFileName(path) == "frame3.fits"
                ? new SeamStream(real.OpenSource(path), readError: new UnauthorizedAccessException("Access to the source is denied."))
                : real.OpenSource(path),
        };

        var result = await new StagingCopier(io).RunAsync(Request(), null, CancellationToken.None);

        Assert.Equal(StagingOutcome.Completed, result.Outcome);
        Assert.Equal("Access to the source is denied.", Assert.Single(result.Failed).Message);
        Assert.Equal(5, result.Copied);
        Assert.Contains(Destination("frame3.fits"), result.PartialPaths);
    }

    [Fact]
    public async Task ANonIoExceptionFailsThatFileAndTheRunStillReturns()
    {
        SixFiles();
        using var writer = Writer();
        var io = FailingOnCall(StagingIo.For(writer), 3, new InvalidOperationException("Unexpected state."));

        var result = await new StagingCopier(io).RunAsync(Request(), null, CancellationToken.None);

        Assert.Equal(StagingOutcome.Completed, result.Outcome);
        Assert.Equal("Unexpected state.", Assert.Single(result.Failed).Message);
        Assert.Equal(5, result.Copied);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a:b")]
    [InlineData(@"a\b")]
    [InlineData("a/b")]
    public async Task AnUnsafeEntryNameAbortsBeforeAnythingIsWritten(string entryName)
    {
        SixFiles();
        using var writer = Writer();
        var request = new StagingCopyRequest([new CopyOperation(new DateOnly(2026, 1, 1), _source, entryName, [])], _staging, []);

        var result = await new StagingCopier(StagingIo.For(writer)).RunAsync(request, null, CancellationToken.None);

        Assert.Equal(StagingOutcome.Aborted, result.Outcome);
        Assert.Contains("is not one plain folder name", result.AbortReason);
        Assert.False(Directory.Exists(_staging));
    }

    [Fact]
    public async Task AFolderAtADestinationFileFailsThatFileAndTheRunContinues()
    {
        SixFiles();
        Directory.CreateDirectory(Destination("frame2.fits"));
        using var writer = Writer();

        var result = await new StagingCopier(StagingIo.For(writer)).RunAsync(Request(), null, CancellationToken.None);

        Assert.Equal(StagingOutcome.Completed, result.Outcome);
        Assert.Contains("A folder already exists", Assert.Single(result.Failed).Message);
        Assert.Equal(5, result.Copied);
    }
}
