namespace PdnQso.Link.Chat;

/// <summary>
/// The last few lines heard, by <c>(source, session, seq)</c>, so that a line whose
/// acknowledgement was lost and which therefore arrives again is acknowledged again and shown
/// once.
/// </summary>
/// <remarks>
/// A fixed ring rather than a growing set: a QSO can run for hours, and the only duplicate
/// that can ever arrive is a retry of something sent seconds ago. Linear scan of a few dozen
/// entries per frame, no allocation, and nothing to expire on a timer.
/// </remarks>
internal sealed class SeenLines(int capacity)
{
    private readonly (string Source, byte Session, byte Seq)[] _slots =
        new (string, byte, byte)[Math.Max(1, capacity)];

    private readonly Lock _gate = new();
    private int _next;
    private int _count;

    /// <summary>
    /// Records a line and says whether it is new. A duplicate is left where it is rather than
    /// moved to the front: the window is about recency of arrival, and a retry storm of one
    /// line must not push everything else out of it.
    /// </summary>
    public bool AddIfNew(string source, byte session, byte seq)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (_gate)
        {
            for (int i = 0; i < _count; i++)
            {
                ref (string Source, byte Session, byte Seq) slot = ref _slots[i];
                if (slot.Seq == seq
                    && slot.Session == session
                    && string.Equals(slot.Source, source, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            _slots[_next] = (source, session, seq);
            _next = (_next + 1) % _slots.Length;
            if (_count < _slots.Length)
            {
                _count++;
            }

            return true;
        }
    }
}
