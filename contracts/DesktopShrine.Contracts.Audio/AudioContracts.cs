using DesktopShrine.Abstractions;

[assembly: ShrineContractPackage("desktop-shrine.contracts.audio", "1.0.0")]

namespace DesktopShrine.Contracts.Audio;

[ShrineContract("desktop-shrine.audio.spectrum", "1.0.0", DeliveryKind.Stream)]
public sealed record AudioSpectrumFrame : IShrineStreamFrame
{
    public required DateTimeOffset CapturedAt { get; init; }
    public required int Sequence { get; init; }
    public required int SampleRate { get; init; }
    public required int FftSize { get; init; }
    public required IReadOnlyList<float> Spectrum { get; init; }
    public required IReadOnlyList<float> Waveform { get; init; }
}
