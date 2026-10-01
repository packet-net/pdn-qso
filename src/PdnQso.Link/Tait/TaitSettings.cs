using M0LTE.Tait.Ccdi;

namespace PdnQso.Link.Tait;

/// <summary>What it takes to open a Tait radio's modem.</summary>
public sealed record TaitSettings
{
    /// <summary>The radio's CCDI serial port, e.g. <c>/dev/ttyUSB0</c>.</summary>
    public required string PortName { get; init; }

    /// <summary>The CCDI serial rate the radio is programmed with. 28800 out of the box.</summary>
    public int BaudRate { get; init; } = TaitCcdiRadio.DefaultBaudRate;

    /// <summary>
    /// The FFSK modem's over-air rate, 1200 or 2400 as the radio is programmed. Both ends must
    /// match. Used only to estimate air time; the radio itself sets the rate.
    /// </summary>
    public int FfskBaud { get; init; } = 2400;

    /// <summary>
    /// The SDM data identity frames are addressed to: eight characters, <c>*</c> matching any
    /// character. All wildcards reaches every radio on the channel, which is what a two-way
    /// test between two stations wants.
    /// </summary>
    public string SdmDestination { get; init; } = AllRadios;

    /// <summary>The SDM destination that matches any radio.</summary>
    public const string AllRadios = "********";
}
