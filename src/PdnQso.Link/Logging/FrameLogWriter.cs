using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Waterfall;

namespace PdnQso.Link.Logging;

/// <summary>
/// Monitor's record of every frame heard and every frame sent, written in exactly the schema
/// pdn-soundmodem's daemon writes, so a capture taken with this tool can be read by the
/// tooling that already exists for the daemon's logs.
/// </summary>
/// <remarks>
/// <para>
/// The format is a SQLite file, not a text log: one row per frame in a table called
/// <c>frames</c>. The column set, their order, the ISO-8601 round-trip timestamp and the
/// <c>direction</c> values are reproduced field for field from
/// <c>src/Packet.SoundModem.Daemon/FrameLog.cs</c> at pdn-soundmodem 0.39.0 - including
/// <c>snr_db</c>, which that repo added in 0.37.0 - because <c>sm-ota</c>'s replay diff and
/// the survey scripts query these column names directly. It is reimplemented here rather
/// than referenced because the daemon's class is internal to the daemon.
/// </para>
/// <para>
/// Two warts are reproduced deliberately rather than fixed, because fixing either would make
/// a log this tool wrote unreadable by the tooling it exists to feed:
/// </para>
/// <list type="bullet">
/// <item><description><c>heard_at</c> holds the send time on a transmitted row. The daemon
/// documents the same wart; renaming the column would break every query written against a
/// log so far.</description></item>
/// <item><description><c>tx_trim_hz</c> is added by a migration rather than being in the
/// CREATE TABLE, so it is the last column. That is where it lands on a daemon log too, and a
/// column order that differs by one is a column order that differs.</description></item>
/// </list>
/// <para>
/// Writes go to a background thread over an unbounded queue, because the receive path must
/// never wait on a disk, and the queue is dropped rather than allowed to grow without limit if
/// the disk goes away. The database is opened WAL, so it can be read while this is writing.
/// </para>
/// </remarks>
public sealed class FrameLogWriter : IAsyncDisposable
{
    /// <summary>
    /// The columns of the <c>frames</c> table, in the order a daemon log has them - the
    /// CREATE TABLE order with the migrated <c>tx_trim_hz</c> on the end. Public so a test,
    /// or a reader, can assert against the schema rather than against a hand-copied list.
    /// </summary>
    public static IReadOnlyList<string> Columns { get; } =
    [
        "id", "heard_at", "direction", "sub_channel", "mode", "mode_name", "source",
        "destination", "length", "corrected", "crc_valid", "trailer_near_bits", "monitor_only",
        "erased_bytes", "chased_bits", "snr_db", "offset_hz", "audio_hz", "rf_hz", "payload",
        "tx_trim_hz",
    ];

    /// <summary>How many frames may queue before new ones are dropped instead.</summary>
    private const int Backlog = 10_000;

    private readonly SqliteConnection _connection;
    private readonly BlockingCollection<Entry> _pending = new(new ConcurrentQueue<Entry>());
    private readonly TimeProvider _time;
    private readonly Task _writer;
    private long _dropped;
    private bool _disposed;

    private FrameLogWriter(SqliteConnection connection, string path, TimeProvider time)
    {
        _connection = connection;
        _time = time;
        Path = path;
        _writer = Task.Run(WriteLoop);
    }

    /// <summary>The file being written to.</summary>
    public string Path { get; }

    /// <summary>How many frames were dropped rather than written; 0 on a healthy station.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>
    /// Opens, creating if needed, the log at <paramref name="path"/>, bringing a log written by
    /// an older pdn-soundmodem up to the current schema on the way.
    /// </summary>
    /// <param name="path">The SQLite file.</param>
    /// <param name="time">Clock for the <c>heard_at</c> column; system clock when omitted.</param>
    /// <exception cref="SqliteException">The file cannot be opened. A station told to keep a
    /// log and silently not keeping one is worse than one that says so.</exception>
    public static FrameLogWriter Open(string path, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string? directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        connection.Open();

        using (SqliteCommand schema = connection.CreateCommand())
        {
            // WAL so the file stays readable while we hold it open; NORMAL because losing the
            // last few frames to a power cut costs nothing worth a synchronous write per frame.
            // The column list below is the daemon's CREATE TABLE verbatim - tx_trim_hz is
            // deliberately absent and arrives from the migration, which is where it comes from
            // on a daemon log too.
            schema.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=NORMAL;
                CREATE TABLE IF NOT EXISTS frames (
                    id                INTEGER PRIMARY KEY,
                    heard_at          TEXT    NOT NULL,
                    direction         TEXT    NOT NULL DEFAULT 'rx',
                    sub_channel       INTEGER NOT NULL,
                    mode              TEXT    NOT NULL,
                    mode_name         TEXT    NOT NULL,
                    source            TEXT,
                    destination       TEXT,
                    length            INTEGER NOT NULL,
                    corrected         INTEGER,
                    crc_valid         INTEGER,
                    trailer_near_bits INTEGER,
                    monitor_only      INTEGER,
                    erased_bytes      INTEGER,
                    chased_bits       INTEGER,
                    snr_db            REAL,
                    offset_hz         REAL,
                    audio_hz          REAL,
                    rf_hz             REAL,
                    payload           BLOB    NOT NULL
                );
                CREATE INDEX IF NOT EXISTS frames_heard_at ON frames(heard_at);
                CREATE INDEX IF NOT EXISTS frames_source ON frames(source);
                """;
            schema.ExecuteNonQuery();
        }

        Migrate(connection);
        return new FrameLogWriter(connection, path, time ?? TimeProvider.System);
    }

    /// <summary>
    /// Queues a heard frame. Returns immediately: this is called from the receive path, which
    /// is decoding the next burst while the row is being written.
    /// </summary>
    /// <param name="subChannel">The modem's sub-channel number.</param>
    /// <param name="frame">The AX.25 frame as the modem delivered it.</param>
    /// <param name="quality">What the decode established about it.</param>
    /// <param name="audioHz">The modem's audio centre, or null.</param>
    /// <param name="rfHz">The dial frequency, or null.</param>
    /// <param name="modeName">Overrides the human-readable name derived from the mode string.</param>
    public void Record(
        int subChannel,
        byte[] frame,
        FrameQuality quality,
        double? audioHz = null,
        double? rfHz = null,
        string? modeName = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (Backlogged())
        {
            return;
        }

        Ax25AddressParser.TryParse(frame, out string source, out string destination);
        _pending.Add(new Entry(
            _time.GetUtcNow(),
            Transmitted: false,
            subChannel,
            quality.Mode,
            modeName ?? ModeNames.Display(quality.Mode),
            string.IsNullOrWhiteSpace(source) ? null : source,
            string.IsNullOrWhiteSpace(destination) ? null : destination,
            quality.FrameBytes,
            quality.CorrectedBytes,
            quality.CrcValid,
            quality.TrailerNearBits,
            quality.MonitorOnly,
            quality.ErasedBytes,
            quality.ChasedBits,
            // Band-tracker convention (in-band power over a rolling minimum floor), NOT the
            // 3 kHz reference AudioChannel.SnrDb uses. Same column, same meaning as the daemon.
            quality.SnrDb,
            quality.FrequencyOffsetHz,
            audioHz,
            rfHz,
            frame,
            TxTrimHz: null));
    }

    /// <summary>
    /// Queues a frame this station sent. Call it once the audio has gone to the device, so a
    /// logged row is a frame that actually went on air.
    /// </summary>
    /// <remarks>
    /// The receive measurements - <c>corrected</c>, <c>crc_valid</c>, <c>offset_hz</c>,
    /// <c>snr_db</c>, <c>trailer_near_bits</c>, <c>monitor_only</c> - are left null rather than
    /// filled with plausible values: nothing measured this transmission, and inventing a
    /// measurement of ourselves would be inventing a measurement. Same rule as the daemon.
    /// </remarks>
    /// <param name="subChannel">The modem's sub-channel number.</param>
    /// <param name="frame">The AX.25 frame that was modulated.</param>
    /// <param name="mode">The sending modem's mode string, as that modem reports itself.</param>
    /// <param name="audioHz">The modem's audio centre, or null.</param>
    /// <param name="rfHz">The dial frequency, or null.</param>
    /// <param name="txTrimHz">How far the burst was shifted off the nominal centre to suit the
    /// station it was addressed to; null when it went out straight.</param>
    public void RecordTransmitted(
        int subChannel,
        byte[] frame,
        string mode,
        double? audioHz = null,
        double? rfHz = null,
        double? txTrimHz = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(mode);
        if (Backlogged())
        {
            return;
        }

        Ax25AddressParser.TryParse(frame, out string source, out string destination);
        _pending.Add(new Entry(
            _time.GetUtcNow(),
            Transmitted: true,
            subChannel,
            mode,
            ModeNames.Display(mode),
            string.IsNullOrWhiteSpace(source) ? null : source,
            string.IsNullOrWhiteSpace(destination) ? null : destination,
            frame.Length,
            Corrected: null,
            CrcValid: null,
            TrailerNearBits: null,
            MonitorOnly: null,
            ErasedBytes: null,
            ChasedBits: null,
            SnrDb: null,
            OffsetHz: null,
            audioHz,
            rfHz,
            frame,
            txTrimHz));
    }

    /// <summary>
    /// Stops accepting rows, waits for the queue to drain and closes the file.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pending.CompleteAdding();
        await _writer.ConfigureAwait(false);
        _pending.Dispose();
        _connection.Dispose();
        SqliteConnection.ClearAllPools();
    }

    /// <summary>
    /// Brings a log written by an earlier pdn-soundmodem up to the current schema. Reproduced
    /// from the daemon's own migration, columns and definitions alike, so opening a station's
    /// existing <c>frames.sqlite</c> with this tool leaves it in the shape the daemon expects.
    /// </summary>
    private static void Migrate(SqliteConnection connection)
    {
        foreach ((string column, string definition) in new[]
                 {
                     ("direction", "TEXT NOT NULL DEFAULT 'rx'"),
                     ("trailer_near_bits", "INTEGER"),
                     ("monitor_only", "INTEGER"),
                     ("erased_bytes", "INTEGER"),
                     ("chased_bits", "INTEGER"),
                     ("snr_db", "REAL"),
                     ("tx_trim_hz", "REAL"),
                 })
        {
            using SqliteCommand columns = connection.CreateCommand();
            columns.CommandText =
                "SELECT COUNT(*) FROM pragma_table_info('frames') WHERE name = $column";
            columns.Parameters.AddWithValue("$column", column);
            if (Convert.ToInt64(columns.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
            {
                continue;
            }

            using SqliteCommand add = connection.CreateCommand();
            add.CommandText = $"ALTER TABLE frames ADD COLUMN {column} {definition}";
            add.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Whether the queue has run away from the disk, counting the frame as dropped if it has.
    /// Dropping the newest keeps the memory bounded and the loss visible.
    /// </summary>
    private bool Backlogged()
    {
        if (_pending.IsAddingCompleted)
        {
            Interlocked.Increment(ref _dropped);
            return true;
        }

        if (_pending.Count <= Backlog)
        {
            return false;
        }

        Interlocked.Increment(ref _dropped);
        return true;
    }

    private void WriteLoop()
    {
        using SqliteCommand insert = _connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO frames
              (heard_at, direction, sub_channel, mode, mode_name, source, destination,
               length, corrected, crc_valid, trailer_near_bits, monitor_only, erased_bytes,
               chased_bits, snr_db, offset_hz, audio_hz, rf_hz, payload, tx_trim_hz)
            VALUES
              ($heard_at, $direction, $sub, $mode, $mode_name, $source, $destination,
               $length, $corrected, $crc, $trailer, $monitor, $erased, $chased, $snr,
               $offset, $audio, $rf, $payload, $tx_trim)
            """;
        foreach (string name in new[]
                 {
                     "$heard_at", "$direction", "$sub", "$mode", "$mode_name", "$source",
                     "$destination", "$length", "$corrected", "$crc", "$trailer", "$monitor",
                     "$erased", "$chased", "$snr", "$offset", "$audio", "$rf", "$payload",
                     "$tx_trim",
                 })
        {
            insert.Parameters.Add(new SqliteParameter(name, DBNull.Value));
        }

        foreach (Entry entry in _pending.GetConsumingEnumerable())
        {
            try
            {
                insert.Parameters["$heard_at"].Value = entry.HeardAt.ToString("O", CultureInfo.InvariantCulture);
                insert.Parameters["$direction"].Value = entry.Transmitted ? "tx" : "rx";
                insert.Parameters["$sub"].Value = entry.SubChannel;
                insert.Parameters["$mode"].Value = entry.Mode;
                insert.Parameters["$mode_name"].Value = entry.ModeName;
                insert.Parameters["$source"].Value = (object?)entry.Source ?? DBNull.Value;
                insert.Parameters["$destination"].Value = (object?)entry.Destination ?? DBNull.Value;
                insert.Parameters["$length"].Value = entry.Length;
                insert.Parameters["$corrected"].Value = (object?)entry.Corrected ?? DBNull.Value;
                insert.Parameters["$crc"].Value = entry.CrcValid is bool crc ? crc ? 1 : 0 : DBNull.Value;
                insert.Parameters["$trailer"].Value = (object?)entry.TrailerNearBits ?? DBNull.Value;
                insert.Parameters["$monitor"].Value =
                    entry.MonitorOnly is bool monitor ? monitor ? 1 : 0 : DBNull.Value;
                insert.Parameters["$erased"].Value = (object?)entry.ErasedBytes ?? DBNull.Value;
                insert.Parameters["$chased"].Value = (object?)entry.ChasedBits ?? DBNull.Value;
                insert.Parameters["$snr"].Value = (object?)entry.SnrDb ?? DBNull.Value;
                insert.Parameters["$offset"].Value = (object?)entry.OffsetHz ?? DBNull.Value;
                insert.Parameters["$audio"].Value = (object?)entry.AudioHz ?? DBNull.Value;
                insert.Parameters["$rf"].Value = (object?)entry.RfHz ?? DBNull.Value;
                insert.Parameters["$payload"].Value = entry.Payload;
                insert.Parameters["$tx_trim"].Value = (object?)entry.TxTrimHz ?? DBNull.Value;
                insert.ExecuteNonQuery();
            }
            catch (SqliteException)
            {
                // A disk that has filled or gone away must not take the station down with it.
                Interlocked.Increment(ref _dropped);
            }
        }
    }

    private sealed record Entry(
        DateTimeOffset HeardAt,
        bool Transmitted,
        int SubChannel,
        string Mode,
        string ModeName,
        string? Source,
        string? Destination,
        int Length,
        int? Corrected,
        bool? CrcValid,
        int? TrailerNearBits,
        bool? MonitorOnly,
        int? ErasedBytes,
        int? ChasedBits,
        double? SnrDb,
        double? OffsetHz,
        double? AudioHz,
        double? RfHz,
        byte[] Payload,
        double? TxTrimHz);
}
