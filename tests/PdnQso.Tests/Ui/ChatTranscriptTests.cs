using Packet.SoundModem.Modems;
using PdnQso.Link.Chat;
using PdnQso.Tests.Chat;
using PdnQso.Ui;

namespace PdnQso.Tests.Ui;

/// <summary>
/// The Chat pane's model: the transcript and its delivery ticks.
/// </summary>
/// <remarks>
/// Half of these drive the model from two real stations over an <see cref="PdnQso.Link.Audio.AudioLink"/>,
/// because the claim worth pinning is not "the record holds what it was told" but "what the ARQ
/// actually did reaches the screen": a line that took three goes says three, and a line nobody
/// heard says failed. The other half are the model on its own, where the ring buffer and the
/// ticket bookkeeping live.
/// </remarks>
public class ChatTranscriptTests
{
    private const string FastMode = "qpsk2400";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task A_Line_Sent_Shows_As_Sending_And_Then_As_Delivered_With_Its_Attempts()
    {
        await using ChatRig rig = ChatRig.Create(FastMode, Options());
        var transcript = new ChatTranscript();
        rig.StartAll();

        int ticket = transcript.AddOutgoing(rig.StationA.Callsign, "good evening", DateTimeOffset.Now);

        transcript.Rows.Should().ContainSingle();
        transcript.Rows[0].State.Should().Be(ChatRowState.Sending);
        transcript.Rows[0].Render().Should().Contain("M0LTE-7").And.Contain("[sending]");

        ChatDelivery delivery = await rig.A.SendAsync("good evening");
        transcript.Complete(ticket, delivery).Should().BeTrue();

        delivery.IsDelivered.Should().BeTrue();
        transcript.Rows[0].State.Should().Be(ChatRowState.Delivered);
        transcript.Rows[0].Attempts.Should().Be(1);
        transcript.Rows[0].Render().Should().Contain("[ok, 1 try,");
    }

    [Fact]
    public async Task A_Line_Nobody_Heard_Shows_As_Failed_With_What_It_Cost()
    {
        ChatOptions options = Options() with
        {
            AckTimeout = TimeSpan.FromMilliseconds(300),
            MaxRetries = 2,
        };
        await using ChatRig rig = ChatRig.Create(FastMode, options);
        rig.Link.Channel = ChatRig.Dead;
        var transcript = new ChatTranscript();
        rig.StartAll();

        int ticket = transcript.AddOutgoing(rig.StationA.Callsign, "anyone there", DateTimeOffset.Now);
        ChatDelivery delivery = await rig.A.SendAsync("anyone there").WaitAsync(Patience);
        transcript.Complete(ticket, delivery);

        delivery.IsDelivered.Should().BeFalse();
        transcript.Rows[0].State.Should().Be(ChatRowState.Failed);
        transcript.Rows[0].Attempts.Should().Be(3, "the first go and two retries");
        transcript.Rows[0].Render().Should().Contain("[failed after 3 tries]");
    }

    [Fact]
    public async Task A_Line_Heard_From_The_Far_Station_Carries_Its_Callsign_And_Its_Snr()
    {
        await using ChatRig rig = ChatRig.Create(FastMode, Options());
        var transcript = new ChatTranscript();
        rig.B.MessageReceived += message =>
        {
            lock (transcript)
            {
                transcript.AddIncoming(message);
            }
        };

        rig.StartAll();
        (await rig.A.SendAsync("solid copy, 599")).IsDelivered.Should().BeTrue();

        lock (transcript)
        {
            transcript.Rows.Should().ContainSingle();
            ChatRow row = transcript.Rows[0];
            row.State.Should().Be(ChatRowState.Heard);
            row.Callsign.Should().Be("M0LTE-7");
            row.Text.Should().Be("solid copy, 599");
            row.Attempts.Should().Be(0, "an attempt count is a thing the sender knows");
            row.Render().Should().Contain("solid copy, 599");
        }
    }

    [Fact]
    public async Task A_Conversation_Reads_In_The_Order_It_Happened()
    {
        await using ChatRig rig = ChatRig.Create(FastMode, Options());
        var transcript = new ChatTranscript();
        rig.A.MessageReceived += message =>
        {
            lock (transcript)
            {
                transcript.AddIncoming(message);
            }
        };

        rig.StartAll();

        int first = Add(transcript, rig, "how copy");
        transcript.Complete(first, await rig.A.SendAsync("how copy"));
        (await rig.B.SendAsync("solid copy, 599")).IsDelivered.Should().BeTrue();
        int second = Add(transcript, rig, "many thanks, 73");
        transcript.Complete(second, await rig.A.SendAsync("many thanks, 73"));

        lock (transcript)
        {
            transcript.Rows.Select(r => r.Text).Should()
                .Equal("how copy", "solid copy, 599", "many thanks, 73");
            transcript.Rows.Select(r => r.State).Should().Equal(
                ChatRowState.Delivered, ChatRowState.Heard, ChatRowState.Delivered);
        }

        static int Add(ChatTranscript transcript, ChatRig rig, string text)
        {
            lock (transcript)
            {
                return transcript.AddOutgoing(rig.StationA.Callsign, text, DateTimeOffset.Now);
            }
        }
    }

    [Fact]
    public void A_Note_Is_Not_A_Line_Anybody_Sent()
    {
        var transcript = new ChatTranscript();

        transcript.AddNote("waveform now 7: two retries failed", DateTimeOffset.Now);

        transcript.Rows.Should().ContainSingle();
        transcript.Rows[0].State.Should().Be(ChatRowState.Note);
        transcript.Rows[0].Callsign.Should().BeEmpty();
        transcript.Rows[0].Render().Should().Contain("-- waveform now 7: two retries failed");
    }

    [Fact]
    public void A_Heard_Line_Shows_The_Waveform_It_Arrived_On()
    {
        var transcript = new ChatTranscript();

        transcript.AddIncoming(new ChatMessage(
            "G0OLD-1", 0x5B, 3, "still here",
            Waveform: 6,
            new FrameQuality("ms110d-wn6", 20, 0, true, SnrDb: 4.2),
            DateTimeOffset.Now));

        transcript.Rows[0].Render().Should().Contain("snr 4.2 dB, wf 6");
    }

    [Fact]
    public void The_Oldest_Lines_Fall_Off_And_Their_Tickets_Stop_Resolving()
    {
        var transcript = new ChatTranscript(capacity: 3);

        int first = transcript.AddOutgoing("M0LTE", "one", DateTimeOffset.Now);
        int second = transcript.AddOutgoing("M0LTE", "two", DateTimeOffset.Now);
        transcript.AddOutgoing("M0LTE", "three", DateTimeOffset.Now);
        transcript.AddOutgoing("M0LTE", "four", DateTimeOffset.Now);

        transcript.Rows.Select(r => r.Text).Should().Equal("two", "three", "four");
        transcript.Complete(first, ChatDelivery.Delivered(0, 1, TimeSpan.FromSeconds(1)))
            .Should().BeFalse("that row is off the top of the transcript");
        transcript.Complete(second, ChatDelivery.Delivered(1, 2, TimeSpan.FromSeconds(1)))
            .Should().BeTrue("the rows that are still there still resolve");
        transcript.Rows[0].Attempts.Should().Be(2);
    }

    [Fact]
    public void Clearing_Drops_The_Transcript_And_Every_Ticket_With_It()
    {
        var transcript = new ChatTranscript();
        int ticket = transcript.AddOutgoing("M0LTE", "before the restart", DateTimeOffset.Now);

        transcript.Clear();

        transcript.Rows.Should().BeEmpty();
        transcript.Complete(ticket, ChatDelivery.Delivered(0, 1, TimeSpan.Zero))
            .Should().BeFalse("a station that has gone will never acknowledge anything");
    }

    [Fact]
    public void The_Header_Says_Who_We_Are_Working_And_What_A_Line_May_Weigh()
    {
        ChatActivity.HeaderText("M0LTE-7", null, null, 512).Should()
            .Be("M0LTE-7  with (nobody yet)  max 512 bytes/line");
        ChatActivity.HeaderText("M0LTE-7", "G0OLD-1", 6, 512).Should()
            .Be("M0LTE-7  with G0OLD-1  waveform 6  max 512 bytes/line");
        ChatActivity.HeaderText(null, null, null, 512).Should().StartWith("no station");
    }

    private static ChatOptions Options() => new()
    {
        AckTimeoutBase = TimeSpan.FromSeconds(2),
        MaxRetries = 2,
        BackoffSlot = TimeSpan.FromMilliseconds(10),
        BusyPollInterval = TimeSpan.FromMilliseconds(5),
    };
}
