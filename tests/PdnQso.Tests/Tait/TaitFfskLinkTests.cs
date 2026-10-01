using System.Threading.Channels;
using Packet.Ax25.Radio.Tait;
using Packet.Ax25.Transport;
using PdnQso.Link.Tait;
using PdnQso.Tests.Time;

namespace PdnQso.Tests.Tait;

/// <summary>
/// The FFSK Transparent link's own part: turning packet.net's transport into the station's
/// link. The radio work under it is the transport's and is tested where it lives.
/// </summary>
public class TaitFfskLinkTests
{
    [Fact]
    public async Task A_Frame_The_Transport_Delivers_Is_Raised_Whole()
    {
        var transport = new FakeTransport();
        await using var link = new TaitFfskLink(transport, new TaitTransparentTransportOptions(), "/dev/ttyUSB0");
        var heard = new List<byte[]>();
        link.FrameReceived += frame =>
        {
            lock (heard)
            {
                heard.Add(frame);
            }
        };

        transport.Deliver([0x96, 0x70, 0x9A, 0x03, 0xF0]);

        await VirtualTime.WaitForAsync(() =>
        {
            lock (heard)
            {
                return heard.Count == 1;
            }
        });
        heard[0].Should().Equal(0x96, 0x70, 0x9A, 0x03, 0xF0);
    }

    [Fact]
    public async Task A_Send_Lasts_Until_The_Transport_Says_The_Frame_Is_Off_The_Air()
    {
        var transport = new FakeTransport();
        await using var link = new TaitFfskLink(transport, new TaitTransparentTransportOptions(), "/dev/ttyUSB0");

        Task send = link.SendAsync(new byte[] { 1, 2, 3 });

        send.IsCompleted.Should().BeFalse();
        transport.Sent.Should().ContainSingle().Which.Should().Equal(1, 2, 3);
        transport.FinishTransmission();
        await send;
    }

    [Fact]
    public async Task Transparent_Mode_Has_No_Carrier_Sense_So_The_Channel_Is_Never_Busy()
    {
        await using var link = new TaitFfskLink(new FakeTransport(), new TaitTransparentTransportOptions(), "x");

        link.Busy.Should().BeFalse();
        link.Mode.Should().Be(TaitModes.Ffsk);
    }

    [Fact]
    public async Task The_Air_Time_Estimate_Is_Lead_In_Plus_The_Framed_Bytes_At_The_Over_Air_Rate()
    {
        var options = new TaitTransparentTransportOptions { FfskBaud = 1200, LeadIn = TimeSpan.FromMilliseconds(100) };
        await using var link = new TaitFfskLink(new FakeTransport(), options, "x");

        // 97 bytes and 3 of SLIP framing is 800 bits, two thirds of a second at 1200.
        link.EstimateAirTime(97).Should().BeCloseTo(TimeSpan.FromMilliseconds(766), TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task Disposing_The_Link_Disposes_The_Transport_Which_Is_What_Leaves_Transparent_Mode()
    {
        var transport = new FakeTransport();
        var link = new TaitFfskLink(transport, new TaitTransparentTransportOptions(), "x");

        await link.DisposeAsync();

        transport.Disposed.Should().BeTrue();
    }

    private sealed class FakeTransport : ITxCompletionTransport
    {
        private readonly Channel<Ax25InboundFrame> _inbound = Channel.CreateUnbounded<Ax25InboundFrame>();
        private readonly List<byte[]> _sent = [];
        private TaskCompletionSource _onAir = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<byte[]> Sent => _sent;

        public bool Disposed { get; private set; }

        public void Deliver(byte[] frame) =>
            _inbound.Writer.TryWrite(new Ax25InboundFrame(frame, 0, DateTimeOffset.UnixEpoch, null));

        public void FinishTransmission() => _onAir.TrySetResult();

        public Task SendAsync(ReadOnlyMemory<byte> ax25, CancellationToken cancellationToken = default) =>
            SendAwaitingCompletionAsync(ax25, null, cancellationToken);

        public async Task<TxCompletion> SendAwaitingCompletionAsync(
            ReadOnlyMemory<byte> ax25, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            _sent.Add(ax25.ToArray());
            await _onAir.Task.ConfigureAwait(false);
            _onAir = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return new TxCompletion(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        }

        public IAsyncEnumerable<Ax25InboundFrame> ReceiveAsync(CancellationToken cancellationToken = default) =>
            _inbound.Reader.ReadAllAsync(cancellationToken);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            _inbound.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
