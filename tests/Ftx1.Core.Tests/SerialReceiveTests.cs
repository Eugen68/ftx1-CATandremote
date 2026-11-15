using Ftx1.Core.Cat;
using Xunit;
namespace Ftx1.Core.Tests;
public class SerialReceiveTests
{
    [Fact]
    public void ClosingPortCancelsPendingReadWithoutEscapingCatThread()
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        int stop = 0, failures = 0, callbacks = 0; Exception? escaped = null;
        var thread = new Thread(() =>
        {
            try { SerialReceiveLoop.Run(_ => { entered.Set(); release.Wait(); throw new OperationCanceledException("SerialStream.EndRead"); },
                () => Volatile.Read(ref stop) != 0, _ => Interlocked.Increment(ref callbacks), () => Interlocked.Increment(ref failures)); }
            catch (Exception error) { escaped = error; }
        });
        thread.IsBackground = true; thread.Start();
        try { Assert.True(entered.Wait(3000)); Volatile.Write(ref stop, 1); }
        finally { release.Set(); }
        Assert.True(thread.Join(3000)); Assert.Null(escaped); Assert.Equal(0, failures); Assert.Equal(0, callbacks);
    }
    [Theory]
    [InlineData("cancel")] [InlineData("io")] [InlineData("closed")] [InlineData("access")]
    public void UnexpectedDriverFailureNotifiesDisconnectionExactlyOnce(string kind)
    {
        Exception error = kind switch { "cancel" => new OperationCanceledException(), "io" => new IOException(), "closed" => new ObjectDisposedException("port"), _ => new UnauthorizedAccessException() };
        int failures = 0; var events = new List<byte[]?>();
        SerialReceiveLoop.Run(_ => throw error, () => false, events.Add, () => failures++);
        Assert.Equal(1, failures); Assert.Null(Assert.Single(events));
    }
    [Fact]
    public void FreshReadLoopWorksAfterCancellationAndTimeoutIsNotDisconnection()
    {
        for (int restart = 0; restart < 5; restart++)
        {
            bool stop = false; int reads = 0, failures = 0; var events = new List<byte[]?>();
            SerialReceiveLoop.Run(buffer =>
            {
                if (++reads == 1) throw new TimeoutException();
                if (reads == 2) { buffer[0] = (byte)'I'; buffer[1] = (byte)'D'; return 2; }
                stop = true; throw new OperationCanceledException();
            }, () => stop, events.Add, () => failures++);
            Assert.Equal(3, reads); Assert.Equal(0, failures); Assert.Equal(new byte[] { 73, 68 }, Assert.Single(events));
        }
    }
}
