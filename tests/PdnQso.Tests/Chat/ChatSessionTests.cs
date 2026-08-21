using PdnQso.Link;
using PdnQso.Link.Audio;
using PdnQso.Link.Chat;

namespace PdnQso.Tests.Chat;

/// <summary>
/// The chat ARQ of docs/design.md section 3, over two real modems joined by an
/// <see cref="AudioLink"/>: real frames on the wire, a channel the test breaks and mends, and
/// no sleeping where waiting for a fact will do.
/// </summary>
/// <remarks>
/// The fast modes are used deliberately. <c>qpsk2400</c> puts a chat line through in about ten
/// milliseconds of processing, so the ARQ's own behaviour is what the test is measuring rather
/// than the DSP; the two waveform tests have to be MS110D because that is the only modem with
/// a ladder, and they pay for it in seconds.
/// </remarks>
public class ChatSessionTests
{
    private const string FastMode = "qpsk2400";

    [Fact]
    public async Task A_Line_Crosses_And_Is_Acknowledged()
    {
        await using ChatRig rig = ChatRig.Create(FastMode, Options());
        List<ChatMessage> heard = Collect(rig.B);
        rig.StartAll();

        ChatDelivery result = await rig.RunAsync(rig.A.SendAsync("good evening, 59 in reading"));

        result.IsDelivered.Should().BeTrue();
        result.Attempts.Should().Be(1);

        // The old assertion here was that the round trip was positive, which on the wall clock
        // only ever meant "some real time went by while the machine did the work". On the
        // session's own clock it says something real: this rig puts a burst across instantly,
        // so a line acknowledged first time costs no protocol time at all, and anything other
        // than zero here would mean a wait or a retry that this test says did not happen.
        //
        // Not compared against the clock's own elapsed time, which is a different number: the
        // loop driving the clock may move it on once more while it is noticing that the send
        // has finished, and that says nothing about the round trip the session measured.
        result.RoundTrip.Should().Be(TimeSpan.Zero);
        lock (heard)
        {
            heard.Should().ContainSingle();
            heard[0].Source.Should().Be("M0LTE-7");
            heard[0].Text.Should().Be("good evening, 59 in reading");
            heard[0].Session.Should().Be(0x2A, "the line carries the sender's conversation id");
            heard[0].Waveform.Should().BeNull("a QPSK modem has no waveform ladder");
        }

        rig.A.Outstanding.Should().BeNull("nothing is in flight once the line is acknowledged");
        rig.A.Stats.Should().Be(new ChatStats(Sent: 1, Delivered: 1, Failed: 0, Retries: 0, Received: 0, Duplicates: 0));
        rig.B.Stats.Received.Should().Be(1);
    }

    [Fact]
    public async Task An_Acknowledgement_Already_In_Hand_Beats_Its_Own_Patience()
    {
        await using ChatRig rig = ChatRig.Create(
            FastMode, Options() with { AckTimeout = TimeSpan.FromSeconds(2) });
        List<ChatMessage> heard = Collect(rig.B);
        rig.StartAll();

        // Subscribed after the sessions have started, so this runs second on the same frame and
        // the session has already taken the acknowledgement in and stamped it. That is the
        // moment the fix is about: the answer is in hand, the patience it was racing runs out,
        // and nothing has yet been given a thread to notice the answer with. Six seconds is
        // three times the patience, so on the old reading the line went out again for an answer
        // the station had already decoded, and the round trip came back as the size of the jump.
        //
        // Two things this handler may not assume. It assumed both, and both of them lost on
        // loaded full-suite runs.
        //
        // It does not get to the clock first. The stamp it is standing behind is the thing that
        // released the sending task, on whatever core the pool had spare, so the line can be
        // delivered and this test's assertions run while this handler is still queued. Waiting
        // for the fact below is what puts that right; assuming the order never was.
        //
        // And the jump must not land inside the sending end's own measurement of the burst it
        // has just put out. That station released the channel before the far end answered, and
        // its pump stamps the burst's air time and finish afterwards - so a jump made in this
        // window is charged to the burst, and comes back as a round trip the size of the jump.
        // Sending is up from the moment a frame is posted and down only once the pump has
        // stamped what it measured, which makes it exactly the fact to wait for.
        Exception? jumpFailed = null;
        bool jumped = false;
        rig.StationA.FrameReceived += (frame, _) =>
        {
            if (frame.Type != LinkFrameType.ChatAck)
            {
                return;
            }

            try
            {
                // A fact and not a deadline, in the shape VirtualTime.WaitForAsync has: this
                // handler is a synchronous one, so it spins rather than awaiting.
                SpinWait.SpinUntil(() => !rig.A.Sending);
                rig.Clock.Advance(TimeSpan.FromSeconds(6));
            }
            catch (Exception failure)
            {
                // A station swallows whatever escapes a receive handler, so an exception thrown
                // here would otherwise be reported as the clock simply not having moved.
                jumpFailed = failure;
            }
            finally
            {
                Volatile.Write(ref jumped, true);
            }
        };

        ChatDelivery result = await rig.RunAsync(rig.A.SendAsync("good evening, 59 in reading"));

        result.IsDelivered.Should().BeTrue();
        result.Attempts.Should().Be(
            1, "the acknowledgement arrived; time moving on afterwards is not a lost answer");
        result.RoundTrip.Should().Be(
            TimeSpan.Zero, "the answer took no time at all, and the jump came after it");
        rig.A.Stats.Retries.Should().Be(0);

        // The jump runs on the far end's transmitting thread and this task was released by the
        // stamp that precedes it, so waiting for it is the whole difference between a claim
        // about the clock and a race with it.
        await ChatRig.WaitUntilAsync(() => Volatile.Read(ref jumped));
        jumpFailed.Should().BeNull("moving the clock is this test's own work and it must not throw");
        rig.Clock.Elapsed.Should().BeGreaterThanOrEqualTo(
            TimeSpan.FromSeconds(6), "the clock really was moved past the patience");
        lock (heard)
        {
            heard.Should().ContainSingle("the line went out once");
        }
    }

    [Fact]
    public async Task A_Conversation_Runs_Both_Ways()
    {
        await using ChatRig rig = ChatRig.Create(FastMode, Options());
        List<ChatMessage> atA = Collect(rig.A);
        List<ChatMessage> atB = Collect(rig.B);
        rig.StartAll();

        (await rig.RunAsync(rig.A.SendAsync("how copy"))).IsDelivered.Should().BeTrue();
        (await rig.RunAsync(rig.B.SendAsync("solid copy, 599"))).IsDelivered.Should().BeTrue();
        (await rig.RunAsync(rig.A.SendAsync("many thanks, 73"))).IsDelivered.Should().BeTrue();

        lock (atB)
        {
            atB.Select(m => m.Text).Should().Equal("how copy", "many thanks, 73");
            atB.Select(m => m.Seq).Should().Equal([(byte)0, (byte)1], "sequence numbers count up");
        }

        lock (atA)
        {
            atA.Should().ContainSingle().Which.Text.Should().Be("solid copy, 599");
        }
    }

    [Fact]
    public async Task A_Lost_Acknowledgement_Is_Retried_And_The_Line_Shows_Once()
    {
        ChatOptions options = Options() with { AckTimeout = TimeSpan.FromMilliseconds(600), MaxRetries = 3 };
        await using ChatRig rig = ChatRig.Create(FastMode, options);
        List<ChatMessage> heard = Collect(rig.B);

        // Break the return path the instant B has decoded the line and before its session has
        // queued the answer, so exactly one acknowledgement is lost. Subscribed before the
        // sessions start, which is what puts this handler ahead of theirs.
        int decoded = 0;
        rig.StationB.FrameReceived += (frame, _) =>
        {
            if (frame.Type == LinkFrameType.Chat && Interlocked.Increment(ref decoded) == 1)
            {
                rig.Link.Channel = ChatRig.Dead;
            }
        };

        // Mend it as soon as the sending end has given up on that attempt, so the retry has a
        // path and the test is about the retry rather than about the band.
        rig.A.AttemptFailed += _ => rig.Link.Channel = ChatRig.Clean;
        rig.StartAll();

        ChatDelivery result = await rig.RunAsync(rig.A.SendAsync("did you get that one"));

        result.IsDelivered.Should().BeTrue();
        result.Attempts.Should().Be(2, "the first acknowledgement was lost");
        decoded.Should().Be(2, "the line itself crossed twice");
        lock (heard)
        {
            heard.Should().ContainSingle("a lost acknowledgement must not duplicate a line in the UI");
            heard[0].Text.Should().Be("did you get that one");
        }

        rig.B.Stats.Received.Should().Be(1);
        rig.B.Stats.Duplicates.Should().Be(1, "the second copy was recognised and acknowledged again");
        rig.A.Stats.Retries.Should().Be(1);
    }

    [Fact]
    public async Task A_Dead_Link_Fails_After_Its_Retries_With_An_Honest_Count()
    {
        ChatOptions options = Options() with { AckTimeout = TimeSpan.FromMilliseconds(200), MaxRetries = 4 };
        await using ChatRig rig = ChatRig.Create(FastMode, options);
        List<ChatMessage> heard = Collect(rig.B);
        var attempts = new List<ChatAttempt>();
        rig.A.AttemptFailed += a =>
        {
            lock (attempts)
            {
                attempts.Add(a);
            }
        };

        rig.Link.Channel = ChatRig.Dead;
        rig.StartAll();

        ChatDelivery result = await rig.RunAsync(rig.A.SendAsync("anybody on frequency"));

        result.IsDelivered.Should().BeFalse();
        result.Attempts.Should().Be(5, "one attempt and four retries, counted honestly");
        result.RoundTrip.Should().Be(TimeSpan.Zero);
        lock (attempts)
        {
            attempts.Select(a => a.Attempt).Should().Equal(1, 2, 3, 4, 5);
        }

        lock (heard)
        {
            heard.Should().BeEmpty();
        }

        rig.A.Stats.Should().Be(new ChatStats(Sent: 1, Delivered: 0, Failed: 1, Retries: 4, Received: 0, Duplicates: 0));
    }

    [Fact]
    public async Task A_Duplicate_Line_Is_Acknowledged_Again_And_Shown_Once()
    {
        await using ChatRig rig = ChatRig.Create(FastMode, Options());
        List<ChatMessage> heard = Collect(rig.B);
        var acks = new List<LinkFrame>();
        rig.StationA.FrameReceived += (frame, _) =>
        {
            if (frame.Type == LinkFrameType.ChatAck)
            {
                lock (acks)
                {
                    acks.Add(frame);
                }
            }
        };

        rig.StartAll();

        // The same line twice, exactly as a sender that heard no acknowledgement would send it.
        LinkFrame line = rig.StationA.Frame(
            LinkFrameType.Chat, 0x77, ChatPayload.Encode(seq: 9, waveform: null, "said once"));
        await rig.StationA.SendAsync(line);
        await rig.StationA.SendAsync(line);
        await rig.RunUntilAsync(() => AckCount(acks) >= 2, "both copies are acknowledged");

        lock (heard)
        {
            heard.Should().ContainSingle();
        }

        rig.B.Stats.Received.Should().Be(1);
        rig.B.Stats.Duplicates.Should().Be(1);
        lock (acks)
        {
            acks.Should().HaveCount(2);
            acks.Should().OnlyContain(a => a.Session == 0x77, "an ack belongs to the conversation it answers");
            acks[0].Payload.ToArray().Should().Equal((byte)9);
        }
    }

    [Fact]
    public async Task A_Backoff_Waits_For_The_Channel_To_Clear()
    {
        var gate = new ManualBusyGate();
        ChatOptions options = Options() with
        {
            AckTimeout = TimeSpan.FromMilliseconds(500),
            MaxRetries = 3,
            BusyPollInterval = TimeSpan.FromMilliseconds(5),
        };
        await using ChatRig rig = ChatRig.Create(FastMode, options, gateA: gate);
        List<ChatMessage> heard = Collect(rig.B);

        // The first attempt goes into a dead channel. The moment it is given up on, the band
        // comes back and somebody else starts transmitting, so the only thing that can hold
        // the retry back is the backoff's own wait for a clear channel.
        rig.Link.Channel = ChatRig.Dead;
        int failures = 0;
        rig.A.AttemptFailed += _ =>
        {
            if (Interlocked.Increment(ref failures) == 1)
            {
                rig.Link.Channel = ChatRig.Clean;
                gate.Held = true;
            }
        };

        rig.StartAll();
        Task<ChatDelivery> send = rig.A.SendAsync("waiting my turn");

        await rig.RunUntilAsync(
            () => rig.A.WaitingForChannel,
            "the retry should be waiting for the channel, not transmitting over it");

        // Two hundred milliseconds of the protocol's own time, moved deliberately rather than
        // waited out: the claim is that the backoff does not give up on a held channel, and
        // that claim should not depend on how long the machine took to get here.
        rig.Clock.Advance(TimeSpan.FromMilliseconds(200));
        rig.A.WaitingForChannel.Should().BeTrue("somebody else is still holding the channel");
        send.IsCompleted.Should().BeFalse();
        lock (heard)
        {
            heard.Should().BeEmpty("nothing may go out while the channel is held");
        }

        gate.Held = false;
        ChatDelivery result = await rig.RunAsync(send);

        result.IsDelivered.Should().BeTrue();
        result.Attempts.Should().Be(2);
        lock (heard)
        {
            heard.Should().ContainSingle();
        }
    }

    [Fact]
    public async Task A_Modem_With_No_Waveform_Ladder_Never_Steps()
    {
        ChatOptions options = Options() with
        {
            AckTimeout = TimeSpan.FromMilliseconds(200),
            MaxRetries = 2,
            StepWaveform = true,
            StepDownAfter = 2,
        };
        await using ChatRig rig = ChatRig.Create(FastMode, options);
        var waveforms = new List<int>();
        rig.A.WaveformChanged += (wn, _) =>
        {
            lock (waveforms)
            {
                waveforms.Add(wn);
            }
        };

        rig.Link.Channel = ChatRig.Dead;
        rig.StartAll();

        ChatDelivery result = await rig.RunAsync(rig.A.SendAsync("three goes at nothing"));

        result.IsDelivered.Should().BeFalse();
        result.Attempts.Should().Be(3);
        rig.A.Ladder.Enabled.Should().BeFalse("a QPSK modem has no waveform to step");
        rig.A.CurrentWaveform.Should().BeNull();
        rig.StationA.Mode.Should().StartWith(FastMode);
        lock (waveforms)
        {
            waveforms.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Two_Failed_Attempts_Step_The_Waveform_Down()
    {
        ChatOptions options = Options() with
        {
            AckTimeoutBase = TimeSpan.FromMilliseconds(900),
            MaxRetries = 1,
            StepDownAfter = 2,
        };
        await using ChatRig rig = ChatRig.Create("ms110d-wn8", options);
        List<ChatMessage> heard = Collect(rig.B);
        var waveforms = new List<(int Waveform, string Reason)>();
        rig.A.WaveformChanged += (wn, why) =>
        {
            lock (waveforms)
            {
                waveforms.Add((wn, why));
            }
        };

        rig.A.CurrentWaveform.Should().Be(8);
        rig.Link.Channel = ChatRig.Dead;
        rig.StartAll();

        ChatDelivery lost = await rig.RunAsync(rig.A.SendAsync("into the noise"));

        lost.IsDelivered.Should().BeFalse();
        lost.Attempts.Should().Be(2);
        rig.A.CurrentWaveform.Should().Be(7, "two unacknowledged attempts step to the next waveform down");
        rig.StationA.Mode.Should().Be("ms110d-wn7", "the modem itself moved, not just our idea of it");
        lock (waveforms)
        {
            waveforms.Should().ContainSingle();
            waveforms[0].Waveform.Should().Be(7);
            waveforms[0].Reason.Should().Contain("unacknowledged");
        }

        rig.Link.Channel = ChatRig.Clean;
        ChatDelivery next = await rig.RunAsync(rig.A.SendAsync("better now"));

        next.IsDelivered.Should().BeTrue();
        next.Attempts.Should().Be(1);
        lock (heard)
        {
            heard.Should().ContainSingle();
            heard[0].Waveform.Should().Be(7, "the line says which waveform it went out on");
        }
    }

    [Fact]
    public async Task Three_Clean_Deliveries_Step_The_Waveform_Back_Up()
    {
        ChatOptions options = Options() with
        {
            AckTimeoutBase = TimeSpan.FromSeconds(2),
            MaxRetries = 2,
            StepUpAfter = 3,
        };
        await using ChatRig rig = ChatRig.Create("ms110d-wn6", options);
        List<ChatMessage> heard = Collect(rig.B);
        var waveforms = new List<(int Waveform, string Reason)>();
        rig.A.WaveformChanged += (wn, why) =>
        {
            lock (waveforms)
            {
                waveforms.Add((wn, why));
            }
        };

        rig.StartAll();

        for (int line = 1; line <= 3; line++)
        {
            ChatDelivery result = await rig.A.SendAsync($"line {line} of three");
            result.IsDelivered.Should().BeTrue();
            result.Attempts.Should().Be(1, "the channel is clean");
        }

        rig.A.CurrentWaveform.Should().Be(7, "three first-time deliveries earn a step up");
        rig.StationA.Mode.Should().Be("ms110d-wn7");
        lock (waveforms)
        {
            waveforms.Should().ContainSingle();
            waveforms[0].Waveform.Should().Be(7);
            waveforms[0].Reason.Should().Contain("delivered");
        }

        (await rig.RunAsync(rig.A.SendAsync("and one more"))).IsDelivered.Should().BeTrue();
        lock (heard)
        {
            heard.Should().HaveCount(4);
            heard.Take(3).Should().OnlyContain(m => m.Waveform == 6);
            heard[3].Waveform.Should().Be(7);
        }
    }

    [Fact]
    public async Task Hello_Names_The_Correspondent_At_Both_Ends()
    {
        await using ChatRig rig = ChatRig.Create(FastMode, Options());
        var atA = new List<string>();
        var atB = new List<string>();
        rig.A.CorrespondentSeen += call =>
        {
            lock (atA)
            {
                atA.Add(call);
            }
        };
        rig.B.CorrespondentSeen += call =>
        {
            lock (atB)
            {
                atB.Add(call);
            }
        };

        rig.StartAll();

        await ChatRig.WaitUntilAsync(() => Count(atA) >= 1 && Count(atB) >= 1);
        lock (atA)
        {
            atA.Should().AllBe("G0OLD-1");
        }

        lock (atB)
        {
            atB.Should().AllBe("M0LTE-7");
        }

        await rig.A.SendHelloAsync();
        await ChatRig.WaitUntilAsync(() => Count(atB) >= 2);
    }

    [Fact]
    public async Task A_Line_From_Somebody_Else_Is_Left_Alone_When_A_Correspondent_Is_Named()
    {
        ChatOptions impatient = Options() with
        {
            AckTimeout = TimeSpan.FromMilliseconds(150),
            MaxRetries = 1,
        };
        await using ChatRig rig = ChatRig.Create(
            FastMode, optionsA: impatient, optionsB: impatient with { Correspondent = "GB7RDG" });
        List<ChatMessage> heard = Collect(rig.B);
        rig.StartAll();

        ChatDelivery result = await rig.RunAsync(rig.A.SendAsync("not for you"));

        result.IsDelivered.Should().BeFalse("a station that is not listening to us does not answer");
        lock (heard)
        {
            heard.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task An_Empty_Or_Oversized_Line_Is_Refused_Before_It_Is_Sent()
    {
        await using ChatRig rig = ChatRig.Create(FastMode, Options() with { MaxTextBytes = 32 });
        rig.StartAll();

        Func<Task> empty = () => rig.A.SendAsync("   \t  ".Trim());
        Func<Task> huge = () => rig.A.SendAsync(new string('x', 33));

        await empty.Should().ThrowAsync<ArgumentException>().WithMessage("*empty line*");
        await huge.Should().ThrowAsync<ArgumentException>().WithMessage("*33 bytes*");
    }

    /// <summary>The options every test starts from: impatient, and quick to give up.</summary>
    private static ChatOptions Options() => new()
    {
        AckTimeoutBase = TimeSpan.FromSeconds(2),
        MaxRetries = 2,
        BackoffSlot = TimeSpan.FromMilliseconds(10),
        BusyPollInterval = TimeSpan.FromMilliseconds(5),
    };

    private static List<ChatMessage> Collect(ChatSession session)
    {
        var heard = new List<ChatMessage>();
        session.MessageReceived += message =>
        {
            lock (heard)
            {
                heard.Add(message);
            }
        };

        return heard;
    }

    private static int Count(List<string> items)
    {
        lock (items)
        {
            return items.Count;
        }
    }

    private static int AckCount(List<LinkFrame> acks)
    {
        lock (acks)
        {
            return acks.Count;
        }
    }
}
