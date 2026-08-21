using PdnQso.Link;
using Terminal.Gui.ViewBase;

namespace PdnQso.Ui;

/// <summary>
/// One thing you can do with a station, as the main window hosts it: Chat, File, Perf, and
/// whatever comes after them.
/// </summary>
/// <remarks>
/// <para>
/// The whole seam, deliberately. The window owns the layout, the Monitor pane, the status bar
/// and the station; an activity owns one <see cref="View"/> and the conversation it is having
/// over the station it was handed. Anything an activity needs beyond that - the ARQ, the
/// fountain coder, the perf counters - lives in <c>PdnQso.Link</c> where it can be tested
/// without a terminal, and the view here is the thin part.
/// </para>
/// <para>
/// <b><see cref="Attach"/> can be called more than once.</b> Changing the device, the mode or
/// the audio centre restarts the station, and every activity is re-attached to the new one. An
/// activity must drop whatever it was holding from the previous station - subscriptions,
/// in-flight transfers, timers - rather than keep talking to a station that has gone.
/// </para>
/// <para>
/// <b>Station events do not arrive on the UI thread.</b> <c>FrameReceived</c> and
/// <c>RawFrameReceived</c> fire on the capture thread. Anything that touches a
/// <see cref="View"/> has to go through <c>IApplication.Invoke</c> first, which is what the
/// window does for its own panes.
/// </para>
/// </remarks>
public interface IActivityView
{
    /// <summary>What to call this activity in the tab strip, e.g. <c>Chat</c>.</summary>
    string Title { get; }

    /// <summary>The view the window puts in the activity pane. Built once, reused.</summary>
    View View { get; }

    /// <summary>
    /// Hands the activity the station it is to work over, replacing any previous one.
    /// </summary>
    /// <param name="station">The live station. It has already been started.</param>
    void Attach(IStation station);

    /// <summary>
    /// Puts the cursor where the operator is about to type, now that this activity is the one
    /// on screen.
    /// </summary>
    /// <remarks>
    /// Called every time the window brings the activity into the pane, which is not the same
    /// occasion as <see cref="Attach"/>: an F-key switches what is on screen without changing
    /// the station. It has to be a separate call because a view that is not visible cannot take
    /// focus, so an activity focusing its own input when it is built focuses nothing - the pane
    /// it is in is still hidden at that point.
    /// </remarks>
    void Shown();
}
