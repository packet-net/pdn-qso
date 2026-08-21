using Microsoft.Data.Sqlite;
using PdnQso.Link;
using PdnQso.Link.Audio;
using PdnQso.Link.Logging;

namespace PdnQso.Tests;

/// <summary>
/// A station with a frame log keeps the record Monitor exists to produce: one row for the
/// frame that went out, one for the frame that came in, in the daemon's own format.
/// </summary>
public class StationFrameLogTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pdnqso-station-log").FullName;

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

    [Fact]
    public async Task Both_Ends_Log_The_Same_Frame_Once_Each()
    {
        string sending = Path.Combine(_dir, "a.sqlite");
        string hearing = Path.Combine(_dir, "b.sqlite");
        using AudioLink link = AudioLink.Create("bpsk300");

        var options = new StationOptions
        {
            Callsign = "M0LTE-7",
            TxDelayMilliseconds = 150,
            AudioCentreHz = 1500,
            RfHz = 7051600,
        };

        await using (var a = new Station(
                         options, link.DeviceA, link.ModemA, OpenBusyGate.Instance,
                         FrameLogWriter.Open(sending)))
        await using (var b = new Station(
                         options with { Callsign = "G0OLD" }, link.DeviceB, link.ModemB,
                         OpenBusyGate.Instance, FrameLogWriter.Open(hearing)))
        {
            a.Start();
            b.Start();
            await a.SendAsync(a.Frame(LinkFrameType.Chat, 0x44, "logged"u8));
        }

        (string direction, string source, double? snr, double? audio)[] sent = Read(sending);
        sent.Should().ContainSingle();
        sent[0].direction.Should().Be("tx");
        sent[0].source.Should().Be("M0LTE-7");
        sent[0].snr.Should().BeNull("nothing measured our own transmission");
        sent[0].audio.Should().Be(1500);

        (string direction, string source, double? snr, double? audio)[] heard = Read(hearing);
        heard.Should().ContainSingle();
        heard[0].direction.Should().Be("rx");
        heard[0].source.Should().Be("M0LTE-7");
        heard[0].audio.Should().Be(1500);
    }

    private static (string Direction, string Source, double? Snr, double? Audio)[] Read(string path)
    {
        using var reader = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        reader.Open();
        using SqliteCommand query = reader.CreateCommand();
        query.CommandText =
            "SELECT direction, source, snr_db, audio_hz FROM frames ORDER BY id";
        using SqliteDataReader rows = query.ExecuteReader();
        var found = new List<(string, string, double?, double?)>();
        while (rows.Read())
        {
            found.Add((
                rows.GetString(0),
                rows.IsDBNull(1) ? "" : rows.GetString(1),
                rows.IsDBNull(2) ? null : rows.GetDouble(2),
                rows.IsDBNull(3) ? null : rows.GetDouble(3)));
        }

        return [.. found];
    }
}
