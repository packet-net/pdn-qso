using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Waterfall;
using PdnQso.Link.Logging;

namespace PdnQso.Tests;

/// <summary>
/// Monitor's frame log, which has to be readable by pdn-soundmodem's own tooling: same table,
/// same columns, same order, same timestamp format.
/// </summary>
public class FrameLogWriterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pdnqso-log").FullName;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 8, 21, 14, 30, 0, TimeSpan.Zero));

    private string DbPath => Path.Combine(_dir, "frames.sqlite");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>A real AX.25 UI frame, so the address columns have real callsigns in them.</summary>
    private static byte[] Frame(string from = "M0LTE-7", string to = "GB7RDG") =>
        Ax25UiFrame.Build(from, to, [0x01, 0x2A, 0x41, 0x42]);

    /// <summary>
    /// The daemon's own schema, transcribed from
    /// <c>src/Packet.SoundModem.Daemon/FrameLog.cs</c> at pdn-soundmodem 0.39.0: the
    /// CREATE TABLE order with the migrated <c>tx_trim_hz</c> last.
    /// </summary>
    private static readonly string[] DaemonColumns =
    [
        "id", "heard_at", "direction", "sub_channel", "mode", "mode_name", "source",
        "destination", "length", "corrected", "crc_valid", "trailer_near_bits", "monitor_only",
        "erased_bytes", "chased_bits", "snr_db", "offset_hz", "audio_hz", "rf_hz", "payload",
        "tx_trim_hz",
    ];

    [Fact]
    public async Task The_Schema_Is_The_Daemons_Schema_Column_For_Column_And_In_Order()
    {
        await using (FrameLogWriter log = FrameLogWriter.Open(DbPath, _time))
        {
            log.Path.Should().Be(DbPath);
        }

        ReadColumns().Should().Equal(DaemonColumns);
        FrameLogWriter.Columns.Should().Equal(DaemonColumns, "the published list has to match the file");
    }

    [Fact]
    public async Task A_Heard_Frame_Reads_Back_Field_For_Field()
    {
        var quality = new FrameQuality(
            "bpsk300-il2pc",
            FrameBytes: 20,
            CorrectedBytes: 3,
            CrcValid: true,
            FrequencyOffsetHz: -2.5,
            TrailerNearBits: 1,
            ErasedBytes: 2,
            ChasedBits: 4,
            SnrDb: 12.5);

        byte[] frame = Frame();
        await using (FrameLogWriter log = FrameLogWriter.Open(DbPath, _time))
        {
            log.Record(0, frame, quality, audioHz: 1500, rfHz: 7051600);
        }

        Row row = ReadOnlyRow();
        row.HeardAt.Should().Be("2026-08-21T14:30:00.0000000+00:00");
        row.Direction.Should().Be("rx");
        row.SubChannel.Should().Be(0);
        row.Mode.Should().Be("bpsk300-il2pc");
        row.ModeName.Should().Be(ModeNames.Display("bpsk300-il2pc"));
        row.Source.Should().Be("M0LTE-7");
        row.Destination.Should().Be("GB7RDG");
        row.Length.Should().Be(20);
        row.Corrected.Should().Be(3);
        row.CrcValid.Should().Be(1);
        row.TrailerNearBits.Should().Be(1);
        row.MonitorOnly.Should().Be(0);
        row.ErasedBytes.Should().Be(2);
        row.ChasedBits.Should().Be(4);
        row.SnrDb.Should().Be(12.5);
        row.OffsetHz.Should().Be(-2.5);
        row.AudioHz.Should().Be(1500);
        row.RfHz.Should().Be(7051600);
        row.Payload.Should().Equal(frame);
        row.TxTrimHz.Should().BeNull();
    }

    [Fact]
    public async Task The_Timestamp_Is_The_Round_Trip_Format_The_Daemon_Writes()
    {
        await using (FrameLogWriter log = FrameLogWriter.Open(DbPath, _time))
        {
            log.Record(0, Frame(), new FrameQuality("bpsk300-il2pc", 20, 0, true));
        }

        string heardAt = ReadOnlyRow().HeardAt;

        // The daemon's own test fixture writes rows in exactly this shape:
        // '2026-07-30T08:00:00.0000000+00:00'.
        DateTimeOffset.Parse(heardAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            .Should().Be(_time.GetUtcNow());
        heardAt.Should().MatchRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}\+00:00$");
    }

    [Fact]
    public async Task A_Sent_Frame_Is_A_Tx_Row_With_No_Invented_Measurements()
    {
        byte[] frame = Frame();
        await using (FrameLogWriter log = FrameLogWriter.Open(DbPath, _time))
        {
            log.RecordTransmitted(1, frame, "ms110d-wn4", audioHz: 1800, rfHz: 7051600, txTrimHz: 4.5);
        }

        Row row = ReadOnlyRow();
        row.Direction.Should().Be("tx");
        row.SubChannel.Should().Be(1);
        row.Mode.Should().Be("ms110d-wn4");
        row.ModeName.Should().Be(ModeNames.Display("ms110d-wn4"));
        row.Length.Should().Be(frame.Length);
        row.TxTrimHz.Should().Be(4.5);

        // Nothing measured this transmission, so nothing is written down about it.
        row.Corrected.Should().BeNull();
        row.CrcValid.Should().BeNull();
        row.OffsetHz.Should().BeNull();
        row.SnrDb.Should().BeNull();
        row.TrailerNearBits.Should().BeNull();
        row.MonitorOnly.Should().BeNull();
    }

    [Fact]
    public async Task A_Log_Written_By_An_Older_Daemon_Is_Migrated_Rather_Than_Rejected()
    {
        // The schema exactly as pdn-soundmodem shipped it before direction/snr_db - which is
        // what a station that has been running for a year has on disk.
        using (var old = new SqliteConnection($"Data Source={DbPath}"))
        {
            old.Open();
            using SqliteCommand create = old.CreateCommand();
            create.CommandText = """
                CREATE TABLE frames (
                    id          INTEGER PRIMARY KEY,
                    heard_at    TEXT    NOT NULL,
                    sub_channel INTEGER NOT NULL,
                    mode        TEXT    NOT NULL,
                    mode_name   TEXT    NOT NULL,
                    source      TEXT,
                    destination TEXT,
                    length      INTEGER NOT NULL,
                    corrected   INTEGER,
                    crc_valid   INTEGER,
                    offset_hz   REAL,
                    audio_hz    REAL,
                    rf_hz       REAL,
                    payload     BLOB    NOT NULL
                );
                INSERT INTO frames
                  (heard_at, sub_channel, mode, mode_name, source, destination,
                   length, corrected, crc_valid, offset_hz, audio_hz, rf_hz, payload)
                VALUES
                  ('2026-07-30T08:00:00.0000000+00:00', 0, 'bpsk300-il2pc', 'BPSK300 IL2Pc',
                   'G0OLD', 'GB7RDG', 4, 1, 1, -2.5, 1500.0, 7051600.0, X'01020304');
                """;
            create.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();

        await using (FrameLogWriter log = FrameLogWriter.Open(DbPath, _time))
        {
            log.Record(0, Frame(), new FrameQuality("bpsk300-il2pc", 20, 0, true));
        }

        ReadColumns().Should().Contain(["direction", "snr_db", "tx_trim_hz"]);
        Rows().Should().Be(2, "the old row is still there");
    }

    [Fact]
    public async Task Nothing_Is_Dropped_On_A_Healthy_Station()
    {
        await using FrameLogWriter log = FrameLogWriter.Open(DbPath, _time);

        for (int i = 0; i < 50; i++)
        {
            log.Record(0, Frame(), new FrameQuality("bpsk300-il2pc", 20, 0, true));
        }

        log.Dropped.Should().Be(0);
    }

    private static SqliteConnection OpenForReading(string path)
    {
        var reader = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        reader.Open();
        return reader;
    }

    private List<string> ReadColumns()
    {
        using SqliteConnection reader = OpenForReading(DbPath);
        using SqliteCommand query = reader.CreateCommand();
        query.CommandText = "SELECT name FROM pragma_table_info('frames') ORDER BY cid";
        using SqliteDataReader rows = query.ExecuteReader();
        var columns = new List<string>();
        while (rows.Read())
        {
            columns.Add(rows.GetString(0));
        }

        return columns;
    }

    private int Rows()
    {
        using SqliteConnection reader = OpenForReading(DbPath);
        using SqliteCommand query = reader.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM frames";
        return Convert.ToInt32(query.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private Row ReadOnlyRow()
    {
        using SqliteConnection reader = OpenForReading(DbPath);
        using SqliteCommand query = reader.CreateCommand();
        query.CommandText = """
            SELECT heard_at, direction, sub_channel, mode, mode_name, source, destination,
                   length, corrected, crc_valid, trailer_near_bits, monitor_only, erased_bytes,
                   chased_bits, snr_db, offset_hz, audio_hz, rf_hz, payload, tx_trim_hz
            FROM frames ORDER BY id
            """;
        using SqliteDataReader rows = query.ExecuteReader();
        rows.Read().Should().BeTrue("a row should have been written");
        return new Row(
            rows.GetString(0),
            rows.GetString(1),
            rows.GetInt32(2),
            rows.GetString(3),
            rows.GetString(4),
            rows.IsDBNull(5) ? null : rows.GetString(5),
            rows.IsDBNull(6) ? null : rows.GetString(6),
            rows.GetInt32(7),
            rows.IsDBNull(8) ? null : rows.GetInt32(8),
            rows.IsDBNull(9) ? null : rows.GetInt32(9),
            rows.IsDBNull(10) ? null : rows.GetInt32(10),
            rows.IsDBNull(11) ? null : rows.GetInt32(11),
            rows.IsDBNull(12) ? null : rows.GetInt32(12),
            rows.IsDBNull(13) ? null : rows.GetInt32(13),
            rows.IsDBNull(14) ? null : rows.GetDouble(14),
            rows.IsDBNull(15) ? null : rows.GetDouble(15),
            rows.IsDBNull(16) ? null : rows.GetDouble(16),
            rows.IsDBNull(17) ? null : rows.GetDouble(17),
            (byte[])rows["payload"],
            rows.IsDBNull(19) ? null : rows.GetDouble(19));
    }

    private sealed record Row(
        string HeardAt,
        string Direction,
        int SubChannel,
        string Mode,
        string ModeName,
        string? Source,
        string? Destination,
        int Length,
        int? Corrected,
        int? CrcValid,
        int? TrailerNearBits,
        int? MonitorOnly,
        int? ErasedBytes,
        int? ChasedBits,
        double? SnrDb,
        double? OffsetHz,
        double? AudioHz,
        double? RfHz,
        byte[] Payload,
        double? TxTrimHz);
}
